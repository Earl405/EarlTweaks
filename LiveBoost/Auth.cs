using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveBoost;

public record AuthSession(string Username, string Role);
public record LoginResult(bool Ok, string Error);
public record AccountRow(string Username, string Role, string Status, string Created, string LastLogin,
                         string Device = "Not registered", string DeviceLastSeen = "never");
public record DeviceInfo(bool Registered, string Name, string Fingerprint, string RegisteredOn, string LastSeen);
public record LoginEntry(string When, string Result, string Device, string Country, string Note);

/// <summary>
/// Centralized authentication through Firebase Authentication. Passwords are never stored by this app (or by us):
/// sign-in goes straight to Firebase (email/password REST API, username mapped to username@EmailDomain), and all
/// admin operations (create / list / disable / reset / delete / device reset) go to the account server (Cloudflare
/// Worker) that re-verifies the caller's ID token, Admin role and registered device on every call.
/// Accounts are hardware-locked: the first sign-in registers this PC, later sign-ins from any other PC are refused
/// by the server, and a background check ends the session if an admin resets or the device stops matching.
/// The client holds no service-account key or admin secret.
/// </summary>
public static class Auth
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static string _idToken = "", _refreshToken = "";
    static DateTime _expires = DateTime.MinValue;
    static System.Threading.Timer? _watch;
    public const int MinPassword = 12;

    public static AuthSession? Current { get; private set; }
    public static bool IsAuthenticated => Current != null;
    public static bool IsAdmin => Current?.Role == "Admin";
    public static bool LoggingOut { get; set; }

    /// <summary>Raised (on the UI thread) when the server ends the session: device reset or device mismatch.
    /// Auth.Current is already null by then; the UI should return to the login screen and show the message.</summary>
    public static event Action<string>? SessionRevoked;

    const string Offline = "Cannot reach the account server. Check your internet connection and try again.";
    static string Email(string u) => $"{u.Trim().ToLowerInvariant()}@{CloudConfig.EmailDomain}";

    // ---------- validation ----------
    public static string? ValidateUsername(string u)
    {
        u = (u ?? "").Trim();
        if (u.Length < 3 || u.Length > 32) return "Username must be 3 to 32 characters.";
        if (!u.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')) return "Username may only use letters, numbers, _ . and -";
        return null;
    }

    public static string? ValidatePassword(string p)
    {
        p ??= "";
        if (p.Length < MinPassword) return $"Password must be at least {MinPassword} characters.";
        if (p.Length > 128) return "Password is too long (128 characters maximum).";
        int kinds = (p.Any(char.IsLower) ? 1 : 0) + (p.Any(char.IsUpper) ? 1 : 0) + (p.Any(char.IsDigit) ? 1 : 0) + (p.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        if (kinds < 3) return "Use at least 3 kinds of characters: lowercase, uppercase, numbers, symbols.";
        return null;
    }

    static void RequireAdmin() { if (!IsAdmin) throw new UnauthorizedAccessException("Administrator access is required."); }

    // ---------- Firebase REST ----------
    static async Task<System.Text.Json.JsonElement> FirebasePost(string url, object body)
    {
        using var resp = await Http.PostAsJsonAsync(url, body);
        var json = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        if (!resp.IsSuccessStatusCode)
        {
            var code = json.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            throw new FirebaseError(code);
        }
        return json;
    }

    sealed class FirebaseError(string code) : Exception(code) { public string Code { get; } = code; }

    static string RoleFromToken(string jwt)
    {
        try   // UI hint only; the server enforces roles on every admin call
        {
            var p = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
            using var d = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(p));
            return d.RootElement.TryGetProperty("role", out var r) && r.GetString() == "Admin" ? "Admin" : "User";
        }
        catch { return "User"; }
    }

    static void SetTokens(System.Text.Json.JsonElement j, bool refresh)
    {
        _idToken = j.GetProperty(refresh ? "id_token" : "idToken").GetString()!;
        _refreshToken = j.GetProperty(refresh ? "refresh_token" : "refreshToken").GetString()!;
        _expires = DateTime.UtcNow.AddSeconds(int.Parse(j.GetProperty(refresh ? "expires_in" : "expiresIn").GetString()!) - 60);
    }

    static async Task<string> GetToken()
    {
        if (!IsAuthenticated) throw new UnauthorizedAccessException("Sign in first.");
        if (DateTime.UtcNow >= _expires)
            SetTokens(await FirebasePost($"https://securetoken.googleapis.com/v1/token?key={CloudConfig.ApiKey}",
                new { grant_type = "refresh_token", refresh_token = _refreshToken }), true);
        return _idToken;
    }

    static T Sync<T>(Func<Task<T>> f)
    {
        try { return Task.Run(f).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { throw new InvalidOperationException(Offline); }
        catch (FirebaseError ex) when (ex.Code.Contains("TOKEN") || ex.Code.Contains("USER_DISABLED") || ex.Code.Contains("USER_NOT_FOUND"))
        { throw new InvalidOperationException("Your session expired. Please log out and sign in again."); }
    }

    public sealed class DuplicateUsernameException() : Exception("That username already exists.") { }

    /// <summary>The account server refused this PC (not the registered device, or the registration was reset).</summary>
    public sealed class DeviceBlockedException(string message) : Exception(message) { }

    // ---------- account server ----------
    static string Str(System.Text.Json.JsonElement e, string name) =>
        e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString() ?? "" : "";

    static string LocalTime(string? iso) => DateTime.TryParse(iso, out var d) ? d.ToLocalTime().ToString("g") : "never";

    /// <summary>Every call carries the ID token and this PC's hashed hardware parts; the server verifies both.</summary>
    static System.Text.Json.JsonElement CallServer(object body, bool adminOnly)
    {
        if (adminOnly) RequireAdmin();
        else if (!IsAuthenticated) throw new UnauthorizedAccessException("Sign in first.");
        if (!CloudConfig.AdminConfigured) throw new InvalidOperationException("The admin server address is not set. Fill in AdminApiBase in CloudConfig.cs and rebuild.");
        return Sync(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, CloudConfig.AdminApiUrl) { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new("Bearer", await GetToken());
            req.Headers.Add("X-Device", DeviceId.Header());
            using var resp = await Http.SendAsync(req);
            System.Text.Json.JsonElement j = default;
            try { j = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(); } catch { }
            if (resp.IsSuccessStatusCode) return j;
            var msg = Str(j, "error");
            var code = Str(j, "code");
            if (code == "DEVICE_MISMATCH")
                throw new DeviceBlockedException("This account is registered to another device. If you replaced or upgraded your PC, ask your administrator to reset your device lock.");
            if (code == "DEVICE_NOT_BOUND")
                throw new DeviceBlockedException("This device is no longer registered to your account. Please sign in again.");
            if (code == "DEVICE_UNSUPPORTED")
                throw new DeviceBlockedException(msg.Length > 0 ? msg : "This PC cannot be registered.");
            if ((int)resp.StatusCode == 409) throw new DuplicateUsernameException();
            throw new InvalidOperationException(msg.Length > 0 ? msg : $"The account server returned an error ({(int)resp.StatusCode}).");
        });
    }

    static System.Text.Json.JsonElement CallAdmin(object body) => CallServer(body, true);

    // ---------- device lock ----------
    /// <summary>Returns null when this PC is accepted, otherwise the message to show. Fails closed.</summary>
    static string? VerifyDevice(string name)
    {
        try { CallServer(new { action = "device", reason = "login" }, false); return null; }
        catch (DeviceBlockedException ex) { Storage.Log($"Sign-in refused: {name} used an unregistered device."); return ex.Message; }
        catch (InvalidOperationException ex) { Storage.Log("Device check failed: " + ex.Message); return ex.Message; }
        catch (Exception ex) { Storage.Log("Device check failed: " + ex.Message); return "Could not verify this device. Try again."; }
    }

    static void StartSessionWatch()
    {
        StopSessionWatch();
        _watch = new System.Threading.Timer(_ => Heartbeat(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    static void StopSessionWatch() { _watch?.Dispose(); _watch = null; }

    static void Heartbeat()
    {
        if (!IsAuthenticated) return;
        try { CallServer(new { action = "device", reason = "heartbeat" }, false); }
        catch (DeviceBlockedException ex)
        {
            Storage.Log("Session ended: the server no longer accepts this device.");
            var message = ex.Message;
            SignOut();
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null) dispatcher.BeginInvoke(new Action(() => SessionRevoked?.Invoke(message)));
            else SessionRevoked?.Invoke(message);
        }
        catch { /* offline or a temporary server problem: check again next time */ }
    }

    // ---------- login / logout ----------
    /// <param name="requiredRole">"Admin" or "User": which login screen was used. An account may only sign in through its own screen.</param>
    public static LoginResult Login(string username, string password, string requiredRole)
    {
        const string Generic = "Invalid username or password.";
        var name = (username ?? "").Trim();
        password ??= "";
        if (!CloudConfig.IsConfigured) return new(false, "Cloud sign-in is not configured. Set the Firebase values in CloudConfig.cs and rebuild.");
        if (ValidateUsername(name) != null || password.Length == 0) return new(false, Generic);
        try
        {
            var j = Task.Run(() => FirebasePost($"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={CloudConfig.ApiKey}",
                new { email = Email(name), password, returnSecureToken = true })).GetAwaiter().GetResult();
            SetTokens(j, false);
            var role = RoleFromToken(_idToken);
            if (role != requiredRole)
            {
                SignOut();
                Storage.Log($"Sign-in refused: {name} used the wrong login screen.");
                return new(false, role == "Admin" ? "This is an administrator account. Use the Admin login." : "This is a user account. Use the User login.");
            }

            Current = new AuthSession(name, role);
            var deviceError = VerifyDevice(name);     // registers this PC on first sign-in, otherwise must match
            if (deviceError != null) { SignOut(); return new(false, deviceError); }

            Storage.Log($"Sign-in: {name}");
            StartSessionWatch();
            return new(true, "");
        }
        catch (FirebaseError ex)
        {
            Storage.Log("Failed sign-in attempt (" + ex.Code + ").");
            if (ex.Code.Contains("OPERATION_NOT_ALLOWED")) return new(false, "Email/Password sign-in is not enabled in Firebase. Enable it under Authentication > Sign-in method.");
            if (ex.Code.Contains("CONFIGURATION_NOT_FOUND")) return new(false, "Firebase Authentication is not set up for this project. In the Firebase console open Authentication and click Get started.");
            if (ex.Code.Contains("API key") || ex.Code.Contains("API_KEY")) return new(false, "The Firebase API key in CloudConfig.cs is wrong or restricted. Re-copy it from Firebase Project settings and rebuild.");
            if (ex.Code.Contains("TOO_MANY_ATTEMPTS")) return new(false, "Too many failed attempts. Wait a few minutes and try again.");
            if (ex.Code.Contains("USER_DISABLED")) return new(false, "This account has been disabled. Contact your administrator.");
            return new(false, Generic);        // never says which part was wrong
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or AggregateException)
        {
            Storage.Log("Sign-in failed: server unreachable.");
            return new(false, Offline);
        }
    }

    public static void SignOut()
    {
        StopSessionWatch();
        Current = null; _idToken = _refreshToken = ""; _expires = DateTime.MinValue;
    }

    // ---------- generators ----------
    static char Pick(string s) => s[RandomNumberGenerator.GetInt32(s.Length)];

    static bool HasRun(string p)
    {
        for (int i = 2; i < p.Length; i++)
        {
            if (p[i] == p[i - 1] && p[i] == p[i - 2]) return true;
            if (p[i] - p[i - 1] == 1 && p[i - 1] - p[i - 2] == 1) return true;
        }
        return false;
    }

    public static string GeneratePassword(int length = 16)
    {
        const string U = "ABCDEFGHJKLMNPQRSTUVWXYZ", Lo = "abcdefghijkmnopqrstuvwxyz", D = "23456789", S = "!@#$%^&*-_=+?";
        length = Math.Clamp(length, MinPassword, 64);
        const string all = U + Lo + D + S;
        while (true)
        {
            var c = new List<char> { Pick(U), Pick(Lo), Pick(D), Pick(S) };
            while (c.Count < length) c.Add(Pick(all));
            for (int i = c.Count - 1; i > 0; i--) { int j = RandomNumberGenerator.GetInt32(i + 1); (c[i], c[j]) = (c[j], c[i]); }
            var pw = new string(c.ToArray());
            if (!HasRun(pw)) return pw;
        }
    }

    /// <summary>Random suggestion only; uniqueness is enforced by the backend when the account is created.</summary>
    public static string GenerateUsername() { RequireAdmin(); return $"EARL_USER{RandomNumberGenerator.GetInt32(100000, 1000000)}"; }

    public static bool UsernameExists(string name) =>
        CallAdmin(new { action = "exists", username = (name ?? "").Trim() }).GetProperty("exists").GetBoolean();

    // ---------- admin operations (server re-verifies Admin role and device on every call) ----------
    public static List<AccountRow> ListAccounts()
    {
        var j = CallAdmin(new { action = "list" });
        return j.GetProperty("accounts").EnumerateArray().Select(a =>
            new AccountRow(a.GetProperty("username").GetString() ?? "", a.GetProperty("role").GetString() ?? "User",
                a.GetProperty("disabled").GetBoolean() ? "Disabled" : "Active",
                LocalTime(Str(a, "created")),
                LocalTime(Str(a, "lastLogin")),
                Str(a, "device") is { Length: > 0 } dv ? dv : "Not registered",
                LocalTime(Str(a, "deviceLastSeen"))))
            .OrderBy(r => r.Username, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The device an account is locked to (name and a short non-reversible fingerprint; no raw hardware IDs).</summary>
    public static DeviceInfo GetDevice(string username)
    {
        var j = CallAdmin(new { action = "deviceInfo", username });
        var registered = j.TryGetProperty("registered", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True;
        return new(registered, Str(j, "name"), Str(j, "fingerprint"), LocalTime(Str(j, "registeredOn")), LocalTime(Str(j, "lastSeen")));
    }

    /// <summary>Latest 50 sign-in events for an account: registered, ok, blocked (other PC), reset.</summary>
    public static List<LoginEntry> GetLoginHistory(string username)
    {
        var j = CallAdmin(new { action = "loginHistory", username });
        return j.GetProperty("entries").EnumerateArray()
            .Select(e => new LoginEntry(LocalTime(Str(e, "at")), Str(e, "result"), Str(e, "device"), Str(e, "country"), Str(e, "note")))
            .ToList();
    }

    /// <summary>Authorized hardware reset: the user's next sign-in registers whichever PC they use then.</summary>
    public static void ResetDevice(string username)
    {
        CallAdmin(new { action = "resetDevice", username });
        Storage.Log($"Device reset: {username} by {Current!.Username}");
    }

    /// <summary>Creates the account in Firebase through the backend. Throws only after the server reports failure; returns only on confirmed success.</summary>
    public static void CreateUser(string username, string password, string role)
    {
        RequireAdmin();
        var name = (username ?? "").Trim();
        var err = ValidateUsername(name) ?? ValidatePassword(password);
        if (err != null) throw new ArgumentException(err);
        var j = CallAdmin(new { action = "create", username = name, password, role = role == "Admin" ? "Admin" : "User" });
        if (!j.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) throw new InvalidOperationException("The server did not confirm the account was created.");
        Storage.Log($"Account created: {name} ({role}) by {Current!.Username}");
    }

    public static void SetDisabled(string username, bool disabled)
    {
        CallAdmin(new { action = "setDisabled", username, disabled });
        Storage.Log($"Account {(disabled ? "disabled" : "re-enabled")}: {username} by {Current!.Username}");
    }

    public static void ResetPassword(string username, string newPassword)
    {
        var err = ValidatePassword(newPassword);
        if (err != null) throw new ArgumentException(err);
        CallAdmin(new { action = "resetPassword", username, password = newPassword });
        Storage.Log($"Password reset: {username} by {Current!.Username}");
    }

    public static void DeleteUser(string username)
    {
        CallAdmin(new { action = "delete", username });
        Storage.Log($"Account deleted: {username} by {Current!.Username}");
    }

    public static void ChangeOwnPassword(string current, string newPassword)
    {
        if (!IsAuthenticated) throw new UnauthorizedAccessException("Sign in first.");
        var err = ValidatePassword(newPassword);
        if (err != null) throw new ArgumentException(err);
        if (newPassword == current) throw new ArgumentException("Choose a different password.");
        var name = Current!.Username;
        try
        {
            Sync(async () =>
            {
                try { await FirebasePost($"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={CloudConfig.ApiKey}", new { email = Email(name), password = current ?? "", returnSecureToken = true }); }
                catch (FirebaseError) { throw new ArgumentException("The current password is incorrect."); }
                var j = await FirebasePost($"https://identitytoolkit.googleapis.com/v1/accounts:update?key={CloudConfig.ApiKey}",
                    new { idToken = await GetToken(), password = newPassword, returnSecureToken = true });
                SetTokens(j, false);
                return true;
            });
        }
        catch (FirebaseError) { throw new InvalidOperationException("Could not change the password. Try again."); }
        Storage.Log($"Password changed: {name}");
    }
}
