using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiveBoost;

public enum Importance { Essential, Optional, Unknown }
public record GameInfo(int Pid, string Name);

public record ProcRow(int Pid, string Name, double Cpu, double MemMb, Importance Kind)
{
    public string KindText => Kind.ToString();
    public string CpuText => $"{Cpu:F1}%";
    public string MemText => $"{MemMb:F0} MB";
    public string GpuText => "N/A"; // per-process GPU needs GPU Engine perf counters; not measured
}

public sealed class ProcessService
{
    static readonly string[] EssentialMarkers =
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost",
        "dwm", "explorer", "audiodg", "fontdrvhost", "sihost", "taskhostw", "ctfmon", "runtimebroker",
        "searchhost", "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "spoolsv", "wudfhost",
        "msmpeng", "nissrv", "securityhealth", "mpdefender", "windefend", "avast", "avg", "kaspersky", "norton",
        "bitdefender", "mcafee", "eset", "malwarebytes", "realtek", "rtkaud", "nahimic", "nvcontainer",
        "nvdisplay", "amddvr", "radeon", "obs", "streamlabs", "tiktok", "livestudio", "livecompanion", "liveboost"
    };
    static readonly string[] OptionalMarkers =
    {
        "chrome", "msedge", "firefox", "opera", "brave", "discord", "spotify", "onedrive", "dropbox", "teams",
        "slack", "telegram", "whatsapp", "skype", "zoom", "steamwebhelper", "epicwebhelper", "epicgameslauncher",
        "origin", "ubisoftconnect", "uplay", "battle.net", "googledrivefs", "adobe", "creative cloud", "cortana", "widgets"
    };
    // Exact (lowercase) process names of popular games, used for auto-detection. You can always pick a game manually.
    public static readonly HashSet<string> KnownGames = new()
    {
        "valorant-win64-shipping", "cs2", "csgo", "fortniteclient-win64-shipping", "robloxplayerbeta", "gta5",
        "eldenring", "r5apex", "overwatch", "leagueoflegends", "league of legends", "dota2", "tslgame",
        "rainbowsix", "rainbowsix_vulkan", "rocketleague", "genshinimpact", "starrail", "modernwarfare",
        "cod", "helldivers2", "destiny2", "cyberpunk2077", "witcher3", "wotblitz", "mobilelegends", "minecraft.windows",
        "bg3", "bg3_dx11", "palworld-win64-shipping", "deadlock", "marvel-win64-shipping", "zenlesszonezero", "warframe.x64", "rustclient", "wow", "ffxiv_dx11"
    };

    [DllImport("psapi.dll")]
    static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>Asks Windows to page out idle memory of OPTIONAL apps only (never the game, never essential apps). Nothing is closed.</summary>
    public static (int count, double freedMb) TrimOptional(int? exceptPid)
    {
        int n = 0; double freed = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == exceptPid || p.Id == Environment.ProcessId) continue;
                if (Classify(p.ProcessName) != Importance.Optional) continue;
                long before = p.WorkingSet64;
                if (EmptyWorkingSet(p.Handle)) { p.Refresh(); freed += Math.Max(0, before - p.WorkingSet64) / 1048576.0; n++; }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return (n, freed);
    }

    readonly Dictionary<int, (TimeSpan cpu, DateTime at)> _prev = new();

    public static Importance Classify(string name)
    {
        var n = name.ToLowerInvariant();
        if (EssentialMarkers.Any(n.Contains)) return Importance.Essential;
        if (OptionalMarkers.Any(n.Contains)) return Importance.Optional;
        return Importance.Unknown;
    }

    public static bool IsTikTok(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("tiktok") && n.Contains("live");
    }

    public List<ProcRow> Sample()
    {
        var rows = new List<ProcRow>();
        var now = DateTime.UtcNow;
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                double cpu = 0;
                TimeSpan t = p.TotalProcessorTime;
                if (_prev.TryGetValue(p.Id, out var old))
                {
                    double dt = (now - old.at).TotalMilliseconds;
                    if (dt > 0) cpu = (t - old.cpu).TotalMilliseconds / dt / Environment.ProcessorCount * 100.0;
                }
                _prev[p.Id] = (t, now);
                seen.Add(p.Id);
                rows.Add(new ProcRow(p.Id, p.ProcessName, Math.Max(0, cpu), p.WorkingSet64 / 1048576.0, Classify(p.ProcessName)));
            }
            catch { /* access denied for protected processes: skip, never guess */ }
            finally { p.Dispose(); }
        }
        foreach (var k in _prev.Keys.Where(k => !seen.Contains(k)).ToList()) _prev.Remove(k);
        return rows;
    }

    public static bool IsTikTokRunning()
    {
        foreach (var p in Process.GetProcesses())
        {
            try { if (IsTikTok(p.ProcessName)) return true; } catch { } finally { p.Dispose(); }
        }
        return false;
    }

    public static GameInfo? DetectGame()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (KnownGames.Contains(p.ProcessName.ToLowerInvariant())) return new GameInfo(p.Id, p.ProcessName);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return null;
    }

    /// <summary>Apps with a visible window that are not essential or known-optional: candidates for "the game".</summary>
    public static List<(int pid, string name, string title)> WindowedCandidates()
    {
        var list = new List<(int pid, string name, string title)>();
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == me || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                var k = Classify(p.ProcessName);
                bool known = KnownGames.Contains(p.ProcessName.ToLowerInvariant());
                if (!known && k != Importance.Unknown) continue;
                list.Add((p.Id, p.ProcessName, p.MainWindowTitle));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list.OrderBy(x => x.name).ToList();
    }

    /// <summary>Politely asks the app to close (like clicking X). Never kills, never touches Essential/Unknown.</summary>
    public static (bool ok, string message) TryClose(ProcRow row)
    {
        if (row.Kind != Importance.Optional) return (false, $"{row.Name} is not classified as optional, so it is protected.");
        try
        {
            using var p = Process.GetProcessById(row.Pid);
            if (!p.CloseMainWindow()) return (false, $"{row.Name} has no window to close gracefully. Close it from the app itself.");
            return (true, $"Asked {row.Name} to close.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
