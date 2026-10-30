namespace LiveBoost;

/// <summary>
/// Public Firebase client settings (safe to ship: the Web API key only identifies the project; it is not a secret).
/// NEVER put a service-account key or any admin secret here. Fill these in from Firebase console > Project settings.
/// </summary>
public static class CloudConfig
{
    public const string ApiKey = "AIzaSyD7zR37AETrgEYEsaMizQO8dj65ZTN0N3A";
    public const string ProjectId = "earltweaks";
    /// <summary>Your Cloudflare Worker address, e.g. https://earl-tweaks-admin.yourname.workers.dev (no trailing slash).</summary>
    public const string AdminApiBase = "https://earl-tweaks-admin.belenearl46.workers.dev";
    /// <summary>Usernames map to a non-deliverable Firebase email: username@EmailDomain (must match backend/functions/index.js).</summary>
    public const string EmailDomain = "earltweaks.example";

    public static bool IsConfigured => !ApiKey.StartsWith("PASTE_") && !ProjectId.StartsWith("PASTE_");
    public static bool AdminConfigured => !AdminApiBase.StartsWith("PASTE_");
    public static string AdminApiUrl => AdminApiBase.TrimEnd('/');
}
