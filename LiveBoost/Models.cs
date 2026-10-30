using System.Security.Principal;
namespace LiveBoost;

public class Profile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool BuiltIn { get; set; }
    public bool SetPowerPlan { get; set; } = true;
    public string PowerScheme { get; set; } = "high"; // "high" or "balanced"
    public bool BoostGame { get; set; }
    public bool BoostTikTok { get; set; }
    public bool LowerBackground { get; set; }
    public bool GameModeTweaks { get; set; }
    public bool GpuPreference { get; set; }
    public bool TrimMemory { get; set; }
    public override string ToString() => Name;
}

public static class Profiles
{
    public static List<Profile> BuiltIns() => new()
    {
        new Profile { Name = "Gaming + TikTok Live", BuiltIn = true, SetPowerPlan = true, PowerScheme = "high", BoostGame = true, BoostTikTok = true, LowerBackground = true, GameModeTweaks = true, GpuPreference = true, TrimMemory = true,
            Description = "Balances both: game and TikTok LIVE Studio get Above Normal priority, optional background apps get Below Normal." },
        new Profile { Name = "Maximum Gaming Performance", BuiltIn = true, SetPowerPlan = true, PowerScheme = "high", BoostGame = true, BoostTikTok = false, LowerBackground = true, GameModeTweaks = true, GpuPreference = true, TrimMemory = true,
            Description = "Game gets Above Normal priority; optional background apps get Below Normal. Essential services are untouched." },
        new Profile { Name = "Streaming Stability", BuiltIn = true, SetPowerPlan = true, PowerScheme = "high", BoostGame = false, BoostTikTok = true, LowerBackground = true, GameModeTweaks = true, GpuPreference = true, TrimMemory = true,
            Description = "TikTok LIVE Studio gets Above Normal priority so encoding stays smooth; optional background apps get Below Normal." },
        new Profile { Name = "Balanced Mode", BuiltIn = true, SetPowerPlan = true, PowerScheme = "balanced", BoostGame = false, BoostTikTok = false, LowerBackground = false,
            Description = "Minimal change: only switches Windows to the Balanced power plan." }
    };
}

public class AppSettings
{
    public bool AutoSuggest { get; set; } = true;
    public bool FpsCapture { get; set; } = true;
    public double UploadMbps { get; set; }
    public string PingHost { get; set; } = "1.1.1.1";
    public bool RestoreOnExit { get; set; }
    public bool AutoBoost { get; set; }
    public double OverlayL { get; set; } = double.NaN;
    public double OverlayT { get; set; } = double.NaN;
    public double WinW { get; set; }
    public double WinH { get; set; }
    public double WinL { get; set; } = double.NaN;
    public double WinT { get; set; } = double.NaN;
}

public class PriorityChange
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public string Original { get; set; } = "";
    public long StartTicks { get; set; }
}

public class SavedState
{
    public string? OriginalScheme { get; set; }
    public string? CreatedScheme { get; set; }
    public List<PriorityChange> Priorities { get; set; } = new();
    public bool GameModeSaved { get; set; }
    public int? GameModeOrig { get; set; }
    public bool CaptureSaved { get; set; }
    public int? CaptureOrig { get; set; }
    public List<string> GpuPrefAdded { get; set; } = new();
    public Dictionary<string, string> GpuPrefOld { get; set; } = new();
}

public class BenchResult
{
    public string Label { get; set; } = "";
    public DateTime Time { get; set; }
    public int Seconds { get; set; }
    public string Game { get; set; } = "";
    public double? AvgFps { get; set; }
    public double? Low1 { get; set; }
    public double? FrameStdMs { get; set; }
    public int Frames { get; set; }
    public double CpuAvg { get; set; }
    public double? GpuAvg { get; set; }
    public double RamUsedMb { get; set; }
    public double? VramUsedMb { get; set; }
    public double? EncAvg { get; set; }
}

public static class Elevation
{
    public static bool IsAdmin
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
