using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LiveBoost;

// Hardware Intelligence page: live cards built in code into HwHost (above the process manager).
// Every number comes from MonitorService; anything the sensors cannot report is shown as unavailable.
public partial class MainWindow
{
    sealed class HwCard
    {
        public readonly TextBlock Big = new(), Sub = new(), Extra = new();
        public readonly ProgressBar Bar = new();
    }

    bool _hwBuilt;
    readonly HwCard _hwCpu = new(), _hwGpu = new(), _hwRam = new(), _hwVram = new();
    readonly TextBlock _hwVerdict = new();

    void BuildHardware()
    {
        if (_hwBuilt) return;
        _hwBuilt = true;
        var grid = new UniformGrid { Columns = 4 };
        grid.Children.Add(MakeHwCard("CPU", _hwCpu));
        grid.Children.Add(MakeHwCard("GPU", _hwGpu));
        grid.Children.Add(MakeHwCard("RAM", _hwRam));
        grid.Children.Add(MakeHwCard("VRAM", _hwVram));
        HwHost.Children.Add(grid);

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "Bottleneck indicator (from the live readings above)", Foreground = (Brush)FindResource("Muted") });
        _hwVerdict.TextWrapping = TextWrapping.Wrap;
        _hwVerdict.Margin = new Thickness(0, 4, 0, 0);
        sp.Children.Add(_hwVerdict);
        HwHost.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = sp });
    }

    Border MakeHwCard(string title, HwCard c)
    {
        var muted = (Brush)FindResource("Muted");
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, Foreground = muted });
        c.Big.FontSize = 26; c.Big.FontWeight = FontWeights.Bold;
        sp.Children.Add(c.Big);
        c.Bar.Height = 8; c.Bar.Minimum = 0; c.Bar.Maximum = 100;
        c.Bar.Margin = new Thickness(0, 6, 0, 6);
        c.Bar.BorderThickness = new Thickness(0);
        c.Bar.Foreground = (Brush)FindResource("Accent");
        c.Bar.Background = (Brush)FindResource("PanelHi");
        sp.Children.Add(c.Bar);
        c.Sub.Foreground = muted; c.Sub.FontSize = 12; c.Sub.TextWrapping = TextWrapping.Wrap;
        c.Extra.Foreground = muted; c.Extra.FontSize = 12; c.Extra.TextWrapping = TextWrapping.Wrap;
        sp.Children.Add(c.Sub);
        sp.Children.Add(c.Extra);
        return new Border { Style = (Style)FindResource("Card"), Child = sp };
    }

    void SetHw(HwCard c, double? pct, string big, string sub, string extra)
    {
        c.Big.Text = big;
        c.Bar.Value = pct ?? 0;
        c.Bar.Foreground = pct >= 90 ? Brushes.OrangeRed : (Brush)FindResource("Accent");
        c.Sub.Text = sub;
        c.Extra.Text = extra;
    }

    void UpdateHardware(Snapshot s)
    {
        BuildHardware();
        SetHw(_hwCpu, s.CpuPercent, $"{s.CpuPercent:F0}%", _mon.CpuName,
            $"{Environment.ProcessorCount} logical processors  |  " +
            (s.CpuTempC is double ct ? $"{ct:F0} °C" : "temperature unavailable (try administrator mode)"));

        SetHw(_hwGpu, s.GpuPercent, s.GpuPercent is double g ? $"{g:F0}%" : "N/A", _mon.GpuName,
            (s.GpuTempC is double gt ? $"{gt:F0} °C" : "temperature unavailable") +
            (s.EncoderPercent is double en ? $"  |  encoder {en:F0}%" : ""));

        double ramPct = s.RamTotalMb > 0 ? s.RamUsedMb / s.RamTotalMb * 100 : 0;
        SetHw(_hwRam, ramPct, $"{ramPct:F0}%", $"{s.RamUsedMb / 1024:F1} / {s.RamTotalMb / 1024:F1} GB used",
            $"{(s.RamTotalMb - s.RamUsedMb) / 1024:F1} GB free");

        if (s.VramUsedMb is double vu && s.VramTotalMb is double vt && vt > 0)
        {
            double vp = vu / vt * 100;
            SetHw(_hwVram, vp, $"{vp:F0}%", $"{vu / 1024:F1} / {vt / 1024:F1} GB used", "");
        }
        else SetHw(_hwVram, null, "N/A", "VRAM not reported by this GPU", "");

        var notes = new List<string>();
        if (s.GpuPercent is double gl)
        {
            if (gl >= 95 && s.CpuPercent < 85) notes.Add("GPU is near its limit while the CPU has headroom: GPU-bound. Lower resolution or graphics settings to gain FPS.");
            else if (s.CpuPercent >= 90 && gl < 85) notes.Add("CPU is near its limit while the GPU has headroom: CPU-bound. Close background apps or lower CPU-heavy settings.");
            else if (s.CpuPercent >= 90 && gl >= 95) notes.Add("Both CPU and GPU are heavily loaded.");
        }
        if (s.CpuTempC >= 90) notes.Add($"CPU is hot ({s.CpuTempC:F0} °C). Check cooling.");
        if (s.GpuTempC >= 85) notes.Add($"GPU is hot ({s.GpuTempC:F0} °C). Check airflow and fan curve.");
        if (s.RamTotalMb - s.RamUsedMb < 1024) notes.Add("Free RAM is under 1 GB.");
        if (s.VramUsedMb is double v2 && s.VramTotalMb is double t2 && t2 > 0 && v2 / t2 > 0.92) notes.Add("VRAM is almost full. Lower texture quality.");
        _hwVerdict.Text = notes.Count > 0 ? string.Join("\n", notes)
            : "No bottleneck detected right now. Start a game to see which part limits it. This is an indication from current load, not a guarantee.";
    }
}

public partial class MainWindow
{
    // System Health score: 100 minus penalties computed only from live readings (load, temperature, memory).
    // It is a transparent indicator of current headroom, not a benchmark result or an FPS prediction.
    void UpdateHealthScore(Snapshot s)
    {
        double ramPct = s.RamTotalMb > 0 ? s.RamUsedMb / s.RamTotalMb * 100 : 0;
        double score = 100;
        var why = new List<string>();
        void Pen(double v, string msg) { if (v >= 1) { score -= v; why.Add(msg); } }
        Pen(Math.Max(0, s.CpuPercent - 60) * 0.4, "high CPU load");
        if (s.GpuPercent is double gl) Pen(Math.Max(0, gl - 90) * 0.5, "GPU near its limit");
        Pen(Math.Max(0, ramPct - 70) * 0.5, "high RAM use");
        if (s.CpuTempC is double ct) Pen(Math.Max(0, ct - 75) * 1.0, "hot CPU");
        if (s.GpuTempC is double gt) Pen(Math.Max(0, gt - 75) * 1.0, "hot GPU");
        if (s.VramUsedMb is double vu && s.VramTotalMb is double vt && vt > 0) Pen(Math.Max(0, vu / vt * 100 - 90) * 1.0, "VRAM nearly full");
        score = Math.Clamp(Math.Round(score), 0, 100);
        ScoreText.Text = score.ToString("F0");
        ScoreBar.Value = score;
        ScoreSub.Text = why.Count == 0
            ? "Plenty of headroom right now. Calculated from live CPU/GPU load, temperatures and memory."
            : "Reduced by: " + string.Join(", ", why) + ". Calculated from live readings, not a benchmark.";
    }
}

public partial class MainWindow
{
    // Dashboard "system specs" row. All values are read from this PC; nothing is guessed.
    void FillSpecs()
    {
        SpecBoard.Text = BoardName();
        SpecGpu.Text = _mon.GpuName;
        SpecCpu.Text = _mon.CpuName;
        SpecRam.Text = $"{_mon.Read().RamTotalMb / 1024:F0} GB installed";
    }

    static string BoardName()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var t = $"{k?.GetValue("BaseBoardManufacturer") as string} {k?.GetValue("BaseBoardProduct") as string}".Trim();
            return t.Length > 0 ? t : "Not reported by this PC";
        }
        catch { return "Not reported by this PC"; }
    }
}
