using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LiveBoost;

/// <summary>One saved play session. Null values mean "not measured" (never estimated).</summary>
public class SessionPoint
{
    public int T { get; set; }              // seconds since session start
    public double? Fps { get; set; }
    public double? Cpu { get; set; }
    public double? Gpu { get; set; }
    public double? CpuTemp { get; set; }
    public double? GpuTemp { get; set; }
    public double? FrameMs { get; set; }
}

public class GameSession
{
    public string Game { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public int DurationSec { get; set; }
    public double? AvgFps { get; set; }
    public double? MaxFps { get; set; }
    public double? MinFps { get; set; }
    public double? Low1Fps { get; set; }
    public double? AvgFrameMs { get; set; }
    public double? AvgCpu { get; set; }
    public double? AvgGpu { get; set; }
    public double? MaxCpuTemp { get; set; }
    public double? MaxGpuTemp { get; set; }
    public string ExePath { get; set; } = "";   // game program path, used for its icon
    public bool Optimized { get; set; }      // true if a LiveBoost profile was applied during the session
    public List<SessionPoint> Points { get; set; } = new();   // one point every 5 s, for graphs
}

/// <summary>
/// Records real metrics once per second while a game is detected, and saves the session when it closes.
/// Call Update() from the 1-second tick. Sessions under MinSeconds are discarded (launcher blips).
/// </summary>
public sealed class GameSessionTracker
{
    const string FileName = "game_sessions.json";
    const int MinSeconds = 60;
    const int MaxSessions = 300;

    public List<GameSession> Sessions { get; }
    public event Action<GameSession>? SessionSaved;
    public bool Active => _name != null;
    public string? CurrentGame => _name;
    public int ElapsedSec => _name == null ? 0 : (int)(DateTime.Now - _start).TotalSeconds;

    string? _name; string _exe = ""; int _pid; DateTime _start; int _miss;
    readonly List<(double? fps, double? low1, double? ms, double cpu, double? gpu, double? ct, double? gt)> _s = new();
    readonly List<SessionPoint> _pts = new();
    bool _optimized;

    public GameSessionTracker() { Sessions = Storage.Load(FileName, new List<GameSession>()); }

    public void Update(GameInfo? game, Snapshot s, FpsStats? fps, bool optimized)
    {
        if (game == null)
        {
            // tolerate brief detection gaps (alt-tab, loading screens) before ending the session
            if (_name != null && ++_miss >= 10) End();
            return;
        }
        _miss = 0;
        if (_name != null && (game.Pid != _pid || game.Name != _name)) End();
        if (_name == null) { _name = game.Name; _pid = game.Pid; _start = DateTime.Now; _s.Clear(); _pts.Clear(); _optimized = false; _exe = GameIcons.Capture(game.Pid, game.Name) ?? ""; }

        _optimized |= optimized;
        _s.Add((fps?.Avg, fps?.Low1, fps != null && fps.Avg > 0 ? 1000.0 / fps.Avg : null, s.CpuPercent, s.GpuPercent, s.CpuTempC, s.GpuTempC));
        int t = _s.Count - 1;
        if (t % 5 == 0)
            _pts.Add(new SessionPoint { T = t, Fps = fps?.Avg, Cpu = s.CpuPercent, Gpu = s.GpuPercent, CpuTemp = s.CpuTempC, GpuTemp = s.GpuTempC, FrameMs = _s[t].ms });
    }

    /// <summary>Ends and saves the current session (also call when the app closes).</summary>
    public void End()
    {
        if (_name == null) return;
        var name = _name; _name = null; _miss = 0;
        int dur = _s.Count;
        if (dur < MinSeconds) { _s.Clear(); _pts.Clear(); return; }

        static double? Avg(IEnumerable<double> v) { var l = v.ToList(); return l.Count == 0 ? null : l.Average(); }
        var fps = _s.Where(x => x.fps != null).Select(x => x.fps!.Value).ToList();
        var lows = _s.Where(x => x.low1 != null).Select(x => x.low1!.Value).ToList();
        var g = new GameSession
        {
            Game = name, ExePath = _exe, Start = _start, End = DateTime.Now, DurationSec = dur, Optimized = _optimized,
            AvgFps = Avg(fps), MaxFps = fps.Count > 0 ? fps.Max() : null, MinFps = fps.Count > 0 ? fps.Min() : null,
            Low1Fps = lows.Count > 0 ? lows.Min() : null,   // worst 1% low seen during the session
            AvgFrameMs = Avg(_s.Where(x => x.ms != null).Select(x => x.ms!.Value)),
            AvgCpu = Avg(_s.Select(x => x.cpu)),
            AvgGpu = Avg(_s.Where(x => x.gpu != null).Select(x => x.gpu!.Value)),
            MaxCpuTemp = _s.Where(x => x.ct != null).Select(x => x.ct!.Value).DefaultIfEmpty(double.NaN).Max() is var c && !double.IsNaN(c) ? c : null,
            MaxGpuTemp = _s.Where(x => x.gt != null).Select(x => x.gt!.Value).DefaultIfEmpty(double.NaN).Max() is var gt && !double.IsNaN(gt) ? gt : null,
            Points = _pts.ToList()
        };
        _s.Clear(); _pts.Clear();
        Sessions.Add(g);
        if (Sessions.Count > MaxSessions) Sessions.RemoveRange(0, Sessions.Count - MaxSessions);
        Storage.Save(FileName, Sessions);
        SessionSaved?.Invoke(g);
    }

    // ---------- summary window (built in code, uses the app theme) ----------
    public static void ShowSummary(Window owner, GameSession g)
    {
        string F(double? v, string f = "F0", string u = "") => v is double d ? d.ToString(f) + u : "N/A";
        var dur = TimeSpan.FromSeconds(g.DurationSec);
        var rows = new (string, string)[]
        {
            ("Duration", $"{(int)dur.TotalHours}h {dur.Minutes:D2}m {dur.Seconds:D2}s"),
            ("Average FPS", F(g.AvgFps)), ("Max / Min FPS", $"{F(g.MaxFps)} / {F(g.MinFps)}"),
            ("1% low FPS", F(g.Low1Fps)), ("Average frametime", F(g.AvgFrameMs, "F1", " ms")),
            ("Average CPU usage", F(g.AvgCpu, "F0", " %")), ("Average GPU usage", F(g.AvgGpu, "F0", " %")),
            ("Max CPU temperature", F(g.MaxCpuTemp, "F0", " °C")), ("Max GPU temperature", F(g.MaxGpuTemp, "F0", " °C")),
        };
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var a = new TextBlock { Text = rows[i].Item1, Foreground = (Brush)Application.Current.FindResource("Muted"), Margin = new Thickness(0, 3, 24, 3) };
            var b = new TextBlock { Text = rows[i].Item2, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(a, i); Grid.SetRow(b, i); Grid.SetColumn(b, 1);
            grid.Children.Add(a); grid.Children.Add(b);
        }
        var note = new TextBlock
        {
            Text = g.AvgFps == null ? "FPS was not measured: needs administrator mode and a DirectX 11/12 game." : "Saved to My Games history.",
            Foreground = (Brush)Application.Current.FindResource("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap
        };
        var ok = new Button { Content = "Close", Style = (Style)Application.Current.FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Session summary", FontSize = 20, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = g.Game, Foreground = (Brush)Application.Current.FindResource("Accent") });
        panel.Children.Add(grid); panel.Children.Add(note); panel.Children.Add(ok);
        var w = new Window
        {
            Title = "Session summary", Width = 400, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner, Content = panel,
            Background = (Brush)Application.Current.FindResource("Bg"), Foreground = (Brush)Application.Current.FindResource("Text"),
            FontFamily = new FontFamily("Segoe UI"), ShowInTaskbar = false
        };
        ok.Click += (_, _) => w.Close();
        w.Show();
    }
}
