using System.Text;

namespace LiveBoost;

public class StreamRec
{
    public string Encoder = "", Resolution = "", Notes = "";
    public int Fps, BitrateKbps;
    public bool BitrateAssumed;

    public string ToText(bool withGame)
    {
        var sb = new StringBuilder();
        sb.AppendLine(withGame ? "Profile: streaming WITH a game" : "Profile: streaming WITHOUT a game");
        sb.AppendLine($"Encoder:    {Encoder}");
        sb.AppendLine($"Resolution: {Resolution} at {Fps} fps");
        sb.AppendLine($"Bitrate:    about {BitrateKbps} kbps" + (BitrateAssumed ? "  (assumes enough upload speed - enter yours above)" : ""));
        if (Notes.Length > 0) { sb.AppendLine(); sb.AppendLine(Notes.TrimEnd()); }
        sb.AppendLine();
        sb.AppendLine("How to apply (manual): LiveBoost cannot change TikTok LIVE Studio's settings. In LIVE Studio, open its settings /");
        sb.AppendLine("stream quality options (menu names differ by version) and choose the encoder, resolution, frame rate and bitrate above.");
        sb.AppendLine("Then run a short test stream and watch for dropped frames. These are general guideline values, not official TikTok");
        sb.AppendLine("requirements; if LIVE Studio limits a value, use the closest allowed one.");
        return sb.ToString();
    }
}

public static class Recommender
{
    public static string Vendor(string gpu)
    {
        var n = gpu.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("rtx") || n.Contains("gtx")) return "nvidia";
        if (n.Contains("amd") || n.Contains("radeon")) return "amd";
        if (n.Contains("intel") || n.Contains("arc") || n.Contains("uhd") || n.Contains("iris")) return "intel";
        return "none";
    }

    static readonly (int h, int fps, int kbps)[] Tiers = { (1080, 60, 6000), (1080, 30, 4500), (720, 60, 4000), (720, 30, 2500), (540, 30, 1500) };

    public static StreamRec Recommend(string gpuName, int cores, double? vramMb, double uploadMbps, bool withGame)
    {
        var v = Vendor(gpuName);
        var r = new StreamRec();
        r.Encoder = v switch
        {
            "nvidia" => "NVIDIA NVENC (H.264)",
            "amd" => "AMD AMF / VCN (H.264)",
            "intel" => "Intel Quick Sync (H.264)",
            _ => "x264 software (CPU), 'veryfast' preset"
        };
        var notes = new StringBuilder();
        int start;
        if (v == "none")
        {
            start = withGame ? (cores >= 12 ? 2 : 3) : (cores >= 8 ? 1 : 3);
            notes.AppendLine("No hardware encoder was detected, so encoding uses the CPU and competes with the game. Resolution is kept conservative.");
        }
        else
        {
            start = (withGame && vramMb != null && vramMb < 6144) ? 2 : 0;
            if (start == 2) notes.AppendLine("Your GPU has under 6 GB of VRAM; sharing it between a game and the encoder, 1080p60 is not recommended.");
            notes.AppendLine("A hardware encoder keeps CPU free for the game. Leave the encoder preset on the quality/performance balance LIVE Studio offers.");
        }
        int idx = start;
        if (uploadMbps <= 0) r.BitrateAssumed = true;
        else
        {
            double usable = uploadMbps * 0.7 * 1000; // keep ~30% headroom
            idx = -1;
            for (int i = start; i < Tiers.Length; i++) if (Tiers[i].kbps <= usable) { idx = i; break; }
            if (idx < 0) { idx = Tiers.Length - 1; notes.AppendLine("Your upload speed is very low; even the lowest tier may be unstable. Consider a wired connection."); }
        }
        var t = Tiers[idx];
        r.Resolution = $"{t.h}p"; r.Fps = t.fps; r.BitrateKbps = t.kbps; r.Notes = notes.ToString();
        return r;
    }

    public static List<string> Diagnose(Snapshot s, (double loss, double avg, double jitter)? net)
    {
        var l = new List<string>();
        double free = s.RamTotalMb - s.RamUsedMb;
        if (s.CpuPercent >= 90) l.Add("CPU limited: total CPU is above 90%. Close heavy apps, lower CPU-heavy game settings (crowds, view distance, physics) or use a hardware encoder.");
        if (s.GpuPercent >= 95) l.Add("GPU limited: GPU load is above 95%. Lower the game's resolution/graphics, cap its FPS, or lower the stream resolution.");
        if (s.EncoderPercent >= 90) l.Add("Encoder overloaded: the GPU video engine is above 90%. Lower stream resolution, frame rate or bitrate.");
        if (free < 1500 || s.RamUsedMb / s.RamTotalMb > 0.9) l.Add($"Memory pressure: only {free:F0} MB RAM free. Close optional apps (browser tabs, launchers).");
        if (s.VramUsedMb / s.VramTotalMb > 0.92) l.Add("VRAM nearly full: lower texture quality or stream resolution to avoid stutter.");
        if (l.Count == 0) l.Add("No CPU, GPU or memory bottleneck detected right now. (Check again while streaming with the game running.)");
        if (net == null) l.Add("Network: not tested yet. Use the network test to check stability.");
        else if (net.Value.loss >= 1 || net.Value.jitter >= 30 || net.Value.avg >= 150)
            l.Add($"Network unstable: {net.Value.loss:F0}% loss, {net.Value.avg:F0} ms average, {net.Value.jitter:F0} ms jitter. Use a wired connection and lower the bitrate.");
        else l.Add($"Network looks stable: {net.Value.loss:F0}% loss, {net.Value.avg:F0} ms average, {net.Value.jitter:F0} ms jitter.");
        return l;
    }

    public static List<string> GpuTips(string gpuName)
    {
        var tips = new List<string>
        {
            "Keep your GPU driver current from the vendor (NVIDIA app, AMD Software, Intel Driver & Support Assistant) or Windows Update.",
            "Windows Settings > System > Display > Graphics: if offered, try 'Hardware-accelerated GPU scheduling'. Effects vary by system; test before keeping it.",
            "Make sure Game Mode is On (Settings > Gaming > Game Mode).",
            "Cap the in-game frame rate slightly below your monitor's refresh rate to reduce frame-time spikes; lower shadows/volumetrics before resolution."
        };
        switch (Vendor(gpuName))
        {
            case "nvidia": tips.Add("NVIDIA: in the NVIDIA app / Control Panel you can set 'Power management mode' to 'Prefer maximum performance' for your game."); break;
            case "amd": tips.Add("AMD: in AMD Software, check per-game profile settings and turn off overlays or features you don't use."); break;
            case "intel": tips.Add("Intel graphics: if the PC also has a dedicated GPU, set the game to use it (Settings > System > Display > Graphics)."); break;
        }
        return tips;
    }
}
