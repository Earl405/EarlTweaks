using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using System.Windows.Media;
using System.Windows.Threading;

namespace LiveBoost;

public class GameItem
{
    public int Pid;
    public string Name = "";
    public string Title = "";
    public override string ToString() => Pid == 0 ? "Auto-detect known games" : $"{Name}  -  {(Title.Length > 32 ? Title.Substring(0, 32) + "..." : Title)}";
}

class BenchRun
{
    public int Seconds, Elapsed;
    public GameInfo? Game;
    public string Label = "";
    public List<Snapshot> Samples = new();
}

public partial class MainWindow : Window
{
    const int Points = 60;
    readonly MonitorService _mon = new();
    readonly ProcessService _procs = new();
    readonly Optimizer _opt = new();
    readonly FpsCapture _fps = new();
    readonly GameSessionTracker _sessions = new();
    readonly AppSettings _set = Storage.Load("settings.json", new AppSettings());
    readonly List<Profile> _custom = Storage.Load("profiles.json", new List<Profile>());
    readonly List<BenchResult> _bench = Storage.Load("benchmarks.json", new List<BenchResult>());
    readonly List<double> _cpuHist = new(), _gpuHist = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    Snapshot? _last, _beforeBoost;
    (double loss, double avg, double jitter)? _net;
    BenchRun? _run;
    GameInfo? _game;
    bool _tikTok, _loading;
    string _page = "Dashboard";
    int _tick, _autoPid = -1;
    bool _autoApplied;
    OverlayWindow? _overlay;
    List<(DateTime t, Snapshot s, double? fps, string game)>? _rec;

    public MainWindow()
    {
        if (!Auth.IsAuthenticated) throw new InvalidOperationException("Sign in is required before the main window can open.");
        InitializeComponent();
        SignedInText.Text = $"Signed in: {Auth.Current!.Username} ({Auth.Current.Role})";
        AcctInfo.Text = SignedInText.Text;
        NavAccounts.Visibility = Auth.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        Auth.SessionRevoked += OnSessionRevoked;
        _sessions.SessionSaved += g => Dispatcher.BeginInvoke(() => { if (IsVisible) GameSessionTracker.ShowSummary(this, g); });
        if (_set.WinW >= MinWidth && _set.WinH >= MinHeight && _set.WinW <= SystemParameters.VirtualScreenWidth && _set.WinH <= SystemParameters.VirtualScreenHeight)
        {
            Width = _set.WinW; Height = _set.WinH;
            if (!double.IsNaN(_set.WinL) && !double.IsNaN(_set.WinT) &&
                _set.WinL >= SystemParameters.VirtualScreenLeft && _set.WinT >= SystemParameters.VirtualScreenTop &&
                _set.WinL + 200 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                _set.WinT + 100 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
            {
                WindowStartupLocation = WindowStartupLocation.Manual; Left = _set.WinL; Top = _set.WinT;
            }
        }
        _loading = true;
        CbAutoSuggest.IsChecked = _set.AutoSuggest;
        CbFps.IsChecked = _set.FpsCapture;
        CbRestoreExit.IsChecked = _set.RestoreOnExit;
        CbAutoBoost.IsChecked = _set.AutoBoost;
        CbStartup.IsChecked = StartupEnabled();
        PingHostBox.Text = _set.PingHost;
        UploadBox.Text = _set.UploadMbps > 0 ? _set.UploadMbps.ToString(CultureInfo.InvariantCulture) : "";
        _loading = false;

        LoadProfiles();
        RefreshGames();
        FillSpecs();
        HwInfo.Text = $"CPU: {_mon.CpuName}\nGPU: {_mon.GpuName}\nLogical processors: {Environment.ProcessorCount}\nOS: {Environment.OSVersion}\n" +
                      "CPU temperature and some GPU sensors may need LiveBoost to run as administrator.";
        if (_set.FpsCapture) _fps.Start();
        LogBox.Text = Storage.ReadLogTail(40);
        if (_opt.HasChanges) AppendLog(new[] { "Earlier LiveBoost changes are still active. Use 'Restore previous settings' to undo them." });
        UpdateAdminText();
        HighlightNav();
        PreviewKeyDown += OnKeys;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    // ---------- navigation ----------
    void Nav_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender).Tag;
        if (tag == "Accounts" && !Auth.IsAdmin) return;          // role is re-checked here, not just hidden in the menu
        if (_page == "Accounts" && tag != "Accounts") { AccPass.Text = ""; AccUser.Text = ""; AccStatus.Text = ""; CreateAccBtn.IsEnabled = false; }
        _page = tag;
        HighlightNav();
        PageDashboard.Visibility = Vis("Dashboard");
        PageBooster.Visibility = Vis("Booster");
        PageLive.Visibility = Vis("Live");
        PageMonitor.Visibility = Vis("Monitor");
        PageProcesses.Visibility = Vis("Processes");
        PageBenchmark.Visibility = Vis("Benchmark");
        PageSettings.Visibility = Vis("Settings");
        PageEarl.Visibility = Vis("Earl");
        PageAdvanced.Visibility = Vis("Advanced");
        PageAccounts.Visibility = Vis("Accounts");
        PageGames.Visibility = Vis("Games");
        PageHistory.Visibility = Vis("History");
        if (_page == "Games") BuildGames();
        if (_page == "History") BuildHistory();
        if (_page == "Accounts") OpenAccountsPage();
        if (_page == "Advanced") UpdateAdvStatus();
        if (_page == "Booster") RefreshRecs();
        if (_page == "Live") { RbWithGame.IsChecked = _game != null; RbNoGame.IsChecked = _game == null; RefreshLiveRec(); }
        if (_page == "Benchmark") ShowBench();
        if (_page == "Settings") UpdateAdminText();
        Tick();
    }
    void HighlightNav()
    {
        foreach (var b in NavPanel.Children.OfType<Button>().Append(NavSettings))
        {
            bool on = (string)b.Tag == _page;
            b.Background = on ? (Brush)FindResource("PanelHi") : Brushes.Transparent;
            b.Foreground = on ? (Brush)FindResource("Text") : (Brush)FindResource("Muted");
        }
        GameBar.Visibility = _page is "Dashboard" or "Booster" or "Live" or "Benchmark" ? Visibility.Visible : Visibility.Collapsed;
    }
    Visibility Vis(string p) => _page == p ? Visibility.Visible : Visibility.Collapsed;

    // ---------- main loop ----------
    void Tick()
    {
        if (!Auth.IsAuthenticated) { _timer.Stop(); return; }
        _tick++;
        var s = _mon.Read();
        _last = s;
        Push(_cpuHist, s.CpuPercent); Push(_gpuHist, s.GpuPercent ?? 0);
        if (_tick % 3 == 1) UpdateDetection();
        if (_run != null) CollectBench(s);
        if (_overlay != null) UpdateOverlay(s);
        if (_rec != null) RecordSample(s);
        _sessions.Update(_game, s, _fps.Running && _game != null ? _fps.Stats(_game.Pid, 1) : null, _opt.HasChanges);

        switch (_page)
        {
            case "Dashboard": UpdateDashboard(s); break;
            case "Booster": UpdateBeforeAfter(s); break;
            case "Live": UpdateLive(s); break;
            case "Monitor": UpdateMonitor(s); break;
            case "Processes":
                {
                    UpdateHardware(s);
                    var sel = (ProcGrid.SelectedItem as ProcRow)?.Pid;
                    var filter = ProcFilter.Text.Trim();
                    var rows = _procs.Sample().Where(r => filter.Length == 0 || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Cpu).ThenByDescending(r => r.MemMb).Take(60).ToList();
                    ProcGrid.ItemsSource = rows;
                    if (sel != null) ProcGrid.SelectedItem = rows.FirstOrDefault(r => r.Pid == sel);
                    break;
                }
        }
    }

    static void Push(List<double> l, double v) { l.Add(v); if (l.Count > Points) l.RemoveAt(0); }

    static void Draw(Canvas c, System.Windows.Shapes.Polyline line, System.Windows.Shapes.Polygon fill, List<double> h)
    {
        double w = c.ActualWidth, ht = c.ActualHeight;
        if (w < 10 || ht < 10 || h.Count == 0) return;
        var pts = new PointCollection();
        for (int i = 0; i < h.Count; i++)
            pts.Add(new Point(i * w / (Points - 1), ht - Math.Clamp(h[i], 0, 100) / 100.0 * ht));
        line.Points = pts;
        var area = new PointCollection(pts);
        area.Add(new Point((h.Count - 1) * w / (Points - 1), ht));
        area.Add(new Point(0, ht));
        fill.Points = area;
    }

    // ---------- detection and suggestion ----------
    GameInfo? ResolveGame()
    {
        if (GameBox.SelectedItem is GameItem gi && gi.Pid > 0)
        {
            try { using var p = Process.GetProcessById(gi.Pid); if (!p.HasExited) return new GameInfo(gi.Pid, gi.Name); } catch { }
        }
        return ProcessService.DetectGame();
    }

    string SuggestName() =>
        _game != null && _tikTok ? "Gaming + TikTok Live" :
        _game != null ? "Maximum Gaming Performance" :
        _tikTok ? "Streaming Stability" : "Balanced Mode";

    void UpdateDetection()
    {
        _tikTok = ProcessService.IsTikTokRunning();
        _game = ResolveGame();
        GameStatus.Text = _game != null ? $"Detected: {_game.Name}" : "No game detected";
        if (_game == null)
        {
            _autoPid = -1;
            if (_autoApplied)
            {
                _autoApplied = false;
                var rl = _opt.Restore();
                rl.Insert(0, "Auto-boost: the game closed, restoring your previous settings.");
                AppendLog(rl);
                UpdateBadges();
            }
        }
        else if (_set.AutoBoost && _game.Pid != _autoPid && !_opt.HasChanges && _run == null)
        {
            _autoPid = _game.Pid;
            AutoBoost();
        }
        SuggestText.Text = _set.AutoSuggest
            ? $"Suggested profile: {SuggestName()}   ({(_game != null ? "game running" : "no game")}, {(_tikTok ? "TikTok LIVE Studio running" : "TikTok LIVE Studio not running")})"
            : "Profile suggestions are turned off in Settings.";
    }

    void AutoBoost()
    {
        try
        {
            var prof = Profiles.BuiltIns().First(p => p.Name == SuggestName());
            var plan = _opt.BuildPlan(prof, _game);
            if (!plan.Any(p => p.Apply != null)) return;
            var log = _opt.Apply(plan);
            log.Insert(0, $"Auto-boost: {_game!.Name} detected, applied '{prof.Name}'.");
            AppendLog(log);
            UpdateBadges();
            _autoApplied = true;
        }
        catch (Exception ex) { Storage.Log("Auto-boost failed: " + ex.Message); }
    }

    void UseSuggestion_Click(object sender, RoutedEventArgs e) => SelectProfile(SuggestName());

    void RefreshGames()
    {
        _loading = true;
        int? sel = (GameBox.SelectedItem as GameItem)?.Pid;
        GameBox.Items.Clear();
        GameBox.Items.Add(new GameItem { Pid = 0 });
        foreach (var c in ProcessService.WindowedCandidates()) GameBox.Items.Add(new GameItem { Pid = c.pid, Name = c.name, Title = c.title });
        GameBox.SelectedItem = GameBox.Items.Cast<GameItem>().FirstOrDefault(g => g.Pid == (sel ?? 0)) ?? GameBox.Items[0];
        _loading = false;
        UpdateDetection();
    }
    void RefreshGames_Click(object sender, RoutedEventArgs e) => RefreshGames();
    void GameBox_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) UpdateDetection(); }

    // ---------- dashboard ----------
    void UpdateDashboard(Snapshot s)
    {
        CpuText.Text = $"{s.CpuPercent:F0}%";
        CpuSub.Text = _mon.CpuName + (s.CpuTempC is double t ? $"  |  {t:F0} °C" : "  |  Temperature unavailable (try administrator mode)");
        GpuText.Text = s.GpuPercent is double g ? $"{g:F0}%" : "N/A";
        GpuSub.Text = _mon.GpuName + (s.GpuTempC is double gt ? $"  |  {gt:F0} °C" : "");
        RamText.Text = $"RAM {s.RamUsedMb / 1024:F1} / {s.RamTotalMb / 1024:F1} GB  ({s.RamTotalMb - s.RamUsedMb:F0} MB free)";
        VramText.Text = s.VramUsedMb is double vu && s.VramTotalMb is double vt ? $"VRAM {vu / 1024:F1} / {vt / 1024:F1} GB" : "VRAM unavailable";
        Draw(CpuGraph, CpuLine, CpuFill, _cpuHist); Draw(GpuGraph, GpuLine, GpuFill, _gpuHist);
        CpuText.Foreground = s.CpuPercent >= 90 ? Brushes.OrangeRed : (Brush)FindResource("Text");
        GpuText.Foreground = s.GpuPercent >= 95 ? Brushes.OrangeRed : (Brush)FindResource("Text");
        RamBar.Value = s.RamTotalMb > 0 ? s.RamUsedMb / s.RamTotalMb * 100 : 0;
        VramBar.Value = s.VramUsedMb is double vb && s.VramTotalMb is double vtt && vtt > 0 ? vb / vtt * 100 : 0;
        UpdateHealthScore(s);
        TikTokStatus.Text = _tikTok ? "● TikTok LIVE Studio running" : "○ TikTok LIVE Studio not detected";
        TikTokStatus.Foreground = _tikTok ? Brushes.LightGreen : (Brush)FindResource("Muted");
        var alerts = new List<string>();
        if (s.CpuTempC >= 90) alerts.Add($"CPU {s.CpuTempC:F0} °C");
        if (s.GpuTempC >= 85) alerts.Add($"GPU {s.GpuTempC:F0} °C");
        if (s.RamTotalMb - s.RamUsedMb < 1024) alerts.Add("low RAM");
        AlertText.Text = alerts.Count > 0 ? "⚠ High: " + string.Join(", ", alerts) : "";

        if (_fps.Running && _game != null)
        {
            var st = _fps.Stats(_game.Pid, 3);
            if (st != null) { FpsText.Text = $"{st.Avg:F0} FPS"; FpsSub.Text = $"{_game.Name}  |  1% low {st.Low1:F0}  |  frame-time deviation {st.StdMs:F1} ms"; }
            else { FpsText.Text = "N/A"; FpsSub.Text = $"No frames measured from {_game.Name} yet. " + _fps.Status(_game.Pid); }
        }
        else if (!Elevation.IsAdmin) { FpsText.Text = "Unavailable"; FpsSub.Text = "Run LiveBoost as administrator to measure FPS (Settings > Restart as administrator)."; }
        else if (_game == null) { FpsText.Text = "Unavailable"; FpsSub.Text = "Select or launch a game."; }
        else if (!_set.FpsCapture) { FpsText.Text = "Unavailable"; FpsSub.Text = "FPS measuring is turned off. Tick \"Measure game FPS\" in Settings."; }
        else { FpsText.Text = "Unavailable"; FpsSub.Text = _fps.Status(_game.Pid); }
    }

    // ---------- booster ----------
    void LoadProfiles()
    {
        string? keep = (ProfileBox.SelectedItem as Profile)?.Name;
        _loading = true;
        ProfileBox.Items.Clear();
        foreach (var p in Profiles.BuiltIns()) ProfileBox.Items.Add(p);
        foreach (var p in _custom) ProfileBox.Items.Add(p);
        _loading = false;
        SelectProfile(keep ?? Profiles.BuiltIns()[0].Name);
    }

    void SelectProfile(string name)
    {
        var p = ProfileBox.Items.Cast<Profile>().FirstOrDefault(x => x.Name == name) ?? (Profile)ProfileBox.Items[0]!;
        ProfileBox.SelectedItem = p;
        ApplyProfileToControls(p);
    }

    void ApplyProfileToControls(Profile p)
    {
        CbPower.IsChecked = p.SetPowerPlan;
        PowerBox.SelectedIndex = p.PowerScheme == "balanced" ? 1 : 0;
        CbGame.IsChecked = p.BoostGame;
        CbTikTok.IsChecked = p.BoostTikTok;
        CbBg.IsChecked = p.LowerBackground;
        CbGameMode.IsChecked = p.GameModeTweaks;
        CbGpuPref.IsChecked = p.GpuPreference;
        CbTrim.IsChecked = p.TrimMemory;
        ProfileDesc.Text = p.Description.Length > 0 ? p.Description : "Custom profile.";
        DeleteBtn.IsEnabled = !p.BuiltIn;
    }

    void ProfileBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileBox.SelectedItem is not Profile p) return;
        ApplyProfileToControls(p);
    }

    Profile CurrentProfile() => new()
    {
        Name = (ProfileBox.SelectedItem as Profile)?.Name ?? "Custom",
        SetPowerPlan = CbPower.IsChecked == true,
        PowerScheme = PowerBox.SelectedIndex == 1 ? "balanced" : "high",
        BoostGame = CbGame.IsChecked == true,
        BoostTikTok = CbTikTok.IsChecked == true,
        LowerBackground = CbBg.IsChecked == true,
        GameModeTweaks = CbGameMode.IsChecked == true,
        GpuPreference = CbGpuPref.IsChecked == true,
        TrimMemory = CbTrim.IsChecked == true
    };

    void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileName.Text.Trim();
        if (name.Length == 0) { MessageBox.Show("Type a name for the custom profile first."); return; }
        if (Profiles.BuiltIns().Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show("That name is used by a built-in profile. Choose another."); return; }
        var p = CurrentProfile(); p.Name = name; p.BuiltIn = false; p.Description = "Custom profile.";
        _custom.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _custom.Add(p);
        Storage.Save("profiles.json", _custom);
        LoadProfiles(); SelectProfile(name);
        AppendLog(new[] { $"Saved custom profile '{name}'." });
    }

    void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not Profile p || p.BuiltIn) return;
        _custom.RemoveAll(c => c.Name == p.Name);
        Storage.Save("profiles.json", _custom);
        LoadProfiles();
    }

    void Boost_Click(object sender, RoutedEventArgs e) => DoBoost();

    void DoBoost()
    {
        var prof = CurrentProfile();
        var plan = _opt.BuildPlan(prof, _game);
        var real = plan.Count(p => p.Apply != null);
        var listing = string.Join("\n", plan.Select(p => "• " + p.Description));
        if (real == 0)
        {
            MessageBox.Show("Nothing needs to change:\n\n" + listing, "LiveBoost", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var ask = $"Profile: {prof.Name}\n\nLiveBoost will make these changes:\n\n{listing}\n\nEverything can be undone with 'Restore previous settings'. Continue?";
        if (MessageBox.Show(ask, "Confirm optimizations", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _beforeBoost = _last;
        var log = _opt.Apply(plan);
        AppendLog(log);
        UpdateBadges();
        MessageBox.Show(string.Join("\n", log), "LiveBoost", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        var log = _opt.Restore();
        AppendLog(log);
        UpdateBadges();
        MessageBox.Show(string.Join("\n", log), "LiveBoost", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void AppendLog(IEnumerable<string> lines)
    {
        var sb = new StringBuilder(LogBox.Text);
        foreach (var l in lines) { if (sb.Length > 0) sb.AppendLine(); sb.Append($"{DateTime.Now:HH:mm:ss}  {l}"); }
        LogBox.Text = sb.ToString();
        LogBox.ScrollToEnd();
    }

    void UpdateBeforeAfter(Snapshot s)
    {
        if (_beforeBoost == null) { BeforeAfter.Text = "Run a boost to compare system load before and after (for real FPS comparisons use the Benchmark page)."; return; }
        var b = _beforeBoost;
        BeforeAfter.Text = $"Before boost: CPU {b.CpuPercent:F0}%, RAM used {b.RamUsedMb / 1024:F1} GB, GPU {(b.GpuPercent is double bg ? bg.ToString("F0") + "%" : "N/A")}\n" +
                           $"Now:          CPU {s.CpuPercent:F0}%, RAM used {s.RamUsedMb / 1024:F1} GB, GPU {(s.GpuPercent is double ng ? ng.ToString("F0") + "%" : "N/A")}\n" +
                           "Single readings vary with what you are doing; use the Benchmark page for a fair comparison.";
    }

    void RefreshRecs_Click(object sender, RoutedEventArgs e) => RefreshRecs();

    void RefreshRecs()
    {
        var sb = new StringBuilder();
        var s = _last;
        if (s != null)
        {
            double free = s.RamTotalMb - s.RamUsedMb;
            sb.AppendLine($"MEMORY: {free / 1024:F1} GB free of {s.RamTotalMb / 1024:F1} GB.");
            if (free < 2048 || s.RamUsedMb / s.RamTotalMb > 0.85)
            {
                var top = _procs.Sample().Where(r => r.Kind == Importance.Optional).GroupBy(r => r.Name)
                    .Select(g => (Name: g.Key, Mb: g.Sum(x => x.MemMb))).OrderByDescending(x => x.Mb).Take(5).ToList();
                sb.AppendLine("Memory is getting tight. Closing optional apps is the safe fix (LiveBoost never frees RAM by force). Biggest optional users:");
                foreach (var t in top) sb.AppendLine($"  - {t.Name}: {t.Mb:F0} MB");
                if (top.Count == 0) sb.AppendLine("  (none of the known optional apps are using much)");
            }
            else sb.AppendLine("Memory looks fine; no action needed.");
            sb.AppendLine();
        }
        var startup = Optimizer.GetStartupEntries();
        sb.AppendLine($"STARTUP: {startup.Count} startup entries found (registry Run keys): {(startup.Count > 0 ? string.Join(", ", startup.Take(10)) : "none")}.");
        sb.AppendLine("Disable what you don't need via 'Open startup apps' (LiveBoost does not change startup items itself).");
        sb.AppendLine();
        sb.AppendLine("GPU AND GRAPHICS SETTINGS:");
        foreach (var t in Recommender.GpuTips(_mon.GpuName)) sb.AppendLine("  - " + t);
        RecBox.Text = sb.ToString();
    }

    static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
    void OpenGameMode_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:gaming-gamemode");
    void OpenStartup_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:startupapps");
    void OpenLogFolder_Click(object sender, RoutedEventArgs e) => OpenUri(Storage.Dir);

    // ---------- live studio ----------
    void UpdateLive(Snapshot s)
    {
        var rows = _procs.Sample().Where(r => ProcessService.IsTikTok(r.Name)).ToList();
        LiveStatus.Text = rows.Count > 0 ? $"● TikTok LIVE Studio is running ({rows.Count} processes)" : "○ TikTok LIVE Studio is not running";
        LiveStatus.Foreground = rows.Count > 0 ? Brushes.LightGreen : (Brush)FindResource("Muted");
        LiveStats.Text = (rows.Count > 0 ? $"TikTok CPU {rows.Sum(r => r.Cpu):F1}%  |  TikTok RAM {rows.Sum(r => r.MemMb):F0} MB\n" : "") +
            $"System CPU {s.CpuPercent:F0}%  |  GPU {(s.GpuPercent is double g ? g.ToString("F0") + "%" : "N/A")}  |  " +
            $"GPU encoder engine {(s.EncoderPercent is double en ? en.ToString("F0") + "%" : "N/A (not reported by this GPU)")}  |  RAM free {(s.RamTotalMb - s.RamUsedMb) / 1024:F1} GB";
        DiagBox.Text = string.Join("\n\n", Recommender.Diagnose(s, _net).Select(x => "• " + x));
    }

    void RefreshLive_Click(object sender, RoutedEventArgs e) => RefreshLiveRec();

    void RefreshLiveRec()
    {
        double up = 0;
        double.TryParse(UploadBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out up);
        _set.UploadMbps = up; Storage.Save("settings.json", _set);
        bool withGame = RbWithGame.IsChecked == true;
        var r = Recommender.Recommend(_mon.GpuName, Environment.ProcessorCount, _last?.VramTotalMb, up, withGame);
        LiveRec.Text = r.ToText(withGame);
    }

    async void NetTest_Click(object sender, RoutedEventArgs e)
    {
        string host = string.IsNullOrWhiteSpace(_set.PingHost) ? "1.1.1.1" : _set.PingHost.Trim();
        NetBtn.IsEnabled = false;
        NetResult.Text = $"Testing: 20 small pings to {host} ...";
        var rtts = new List<double>(); int lost = 0;
        using (var ping = new Ping())
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    var r = await ping.SendPingAsync(host, 1000);
                    if (r.Status == IPStatus.Success) rtts.Add(r.RoundtripTime); else lost++;
                }
                catch { lost++; }
                await Task.Delay(250);
            }
        }
        if (rtts.Count == 0) { NetResult.Text = $"No replies from {host}. Check your connection or change the test host in Settings."; _net = (100, 0, 0); }
        else
        {
            double avg = rtts.Average();
            double jit = Math.Sqrt(rtts.Sum(x => (x - avg) * (x - avg)) / rtts.Count);
            double loss = lost * 100.0 / 20;
            _net = (loss, avg, jit);
            NetResult.Text = $"Result: {loss:F0}% loss, {avg:F0} ms average, {jit:F0} ms jitter. (This tests latency and stability, not upload speed.)";
        }
        NetBtn.IsEnabled = true;
    }

    void CopyLiveRec_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LiveRec.Text)) RefreshLiveRec();
        try { Clipboard.SetText(LiveRec.Text); MessageBox.Show("Recommended settings copied to the clipboard.", "LiveBoost"); }
        catch (Exception ex) { MessageBox.Show("Could not copy: " + ex.Message); }
    }

    void ApplyLiveProfile_Click(object sender, RoutedEventArgs e)
    {
        SelectProfile(RbWithGame.IsChecked == true ? "Gaming + TikTok Live" : "Streaming Stability");
        DoBoost();
    }

    // ---------- monitor ----------
    void UpdateMonitor(Snapshot s)
    {
        string F(double? v, string suffix, string f = "F0") => v is double d ? d.ToString(f) + suffix : "unavailable";
        MonText.Text =
            $"CPU usage        {s.CpuPercent:F0}%\n" +
            $"CPU temperature  {F(s.CpuTempC, " °C")}\n" +
            $"GPU usage        {F(s.GpuPercent, "%")}\n" +
            $"GPU temperature  {F(s.GpuTempC, " °C")}\n" +
            $"VRAM             {(s.VramUsedMb is double u && s.VramTotalMb is double t ? $"{u:F0} / {t:F0} MB" : "unavailable")}\n" +
            $"GPU encoder      {F(s.EncoderPercent, "%")}\n" +
            $"RAM              {s.RamUsedMb:F0} / {s.RamTotalMb:F0} MB  ({s.RamTotalMb - s.RamUsedMb:F0} MB free)\n" +
            $"TikTok LIVE      {(_tikTok ? "running" : "not running")}\n" +
            $"Game FPS         {(_fps.Running && _game != null && _fps.Stats(_game.Pid, 3) is FpsStats st ? st.Avg.ToString("F0") : "unavailable")}";
        var top = _procs.Sample().OrderByDescending(r => r.Cpu).Take(8);
        TopProcText.Text = string.Join("\n", top.Select(r => $"{r.Name,-28}{r.CpuText,8}{r.MemText,10}   {r.KindText}"));
    }

    // ---------- process manager ----------
    void CloseSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ProcGrid.SelectedItem is not ProcRow row) { ProcMsg.Text = "Select a process first."; return; }
        if (row.Kind != Importance.Optional) { ProcMsg.Text = $"{row.Name} is {row.KindText.ToLower()} and protected."; return; }
        if (MessageBox.Show($"Ask {row.Name} to close? Unsaved work in it may be lost.", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var r = ProcessService.TryClose(row);
        ProcMsg.Text = r.message;
        Storage.Log("Close request: " + r.message);
    }

    // ---------- benchmark ----------
    void RunBench_Click(object sender, RoutedEventArgs e)
    {
        if (_run != null) return;
        int secs = int.Parse(((ComboBoxItem)BenchDuration.SelectedItem).Content.ToString()!);
        if (_game != null && _set.FpsCapture) _fps.Start();
        _run = new BenchRun { Seconds = secs, Game = _game, Label = RbBefore.IsChecked == true ? "Before" : "After" };
        BenchStatus.Text = $"Running {_run.Label} benchmark ({secs}s). Keep the same scene and conditions...";
    }

    void CollectBench(Snapshot s)
    {
        var r = _run!;
        r.Samples.Add(s); r.Elapsed++;
        BenchStatus.Text = $"Running {r.Label} benchmark: {r.Elapsed}/{r.Seconds}s ...";
        if (r.Elapsed >= r.Seconds) FinishBench();
    }

    static double? AvgN(IEnumerable<double?> v) { var l = v.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : l.Average(); }

    void FinishBench()
    {
        var r = _run!; _run = null;
        FpsStats? st = r.Game != null && _fps.Running ? _fps.Stats(r.Game.Pid, r.Seconds) : null;
        var res = new BenchResult
        {
            Label = r.Label, Time = DateTime.Now, Seconds = r.Seconds, Game = r.Game?.Name ?? "(no game)",
            AvgFps = st?.Avg, Low1 = st?.Low1, FrameStdMs = st?.StdMs, Frames = st?.Frames ?? 0,
            CpuAvg = r.Samples.Average(x => x.CpuPercent),
            GpuAvg = AvgN(r.Samples.Select(x => x.GpuPercent)),
            RamUsedMb = r.Samples.Average(x => x.RamUsedMb),
            VramUsedMb = AvgN(r.Samples.Select(x => x.VramUsedMb)),
            EncAvg = AvgN(r.Samples.Select(x => x.EncoderPercent))
        };
        _bench.RemoveAll(b => b.Label == res.Label);
        _bench.Add(res);
        Storage.Save("benchmarks.json", _bench);
        Storage.Log($"Benchmark {res.Label}: game={res.Game}, avgFps={(res.AvgFps?.ToString("F1") ?? "n/a")}, cpu={res.CpuAvg:F0}%");
        string why = st != null ? "" : (!Elevation.IsAdmin ? " FPS unavailable: run as administrator." : r.Game == null ? " FPS unavailable: no game selected." : " FPS unavailable: no DirectX 11/12 frames were captured from the game.");
        BenchStatus.Text = $"{res.Label} run finished.{why}";
        ShowBench();
    }

    static string N(double? v, string f) => v.HasValue ? v.Value.ToString(f, CultureInfo.InvariantCulture) : "n/a";
    static string Delta(double? a, double? b, string f)
    {
        if (a == null || b == null) return "-";
        var d = b.Value - a.Value;
        return (d >= 0 ? "+" : "") + d.ToString(f, CultureInfo.InvariantCulture);
    }

    void ShowBench()
    {
        var b = _bench.FirstOrDefault(x => x.Label == "Before");
        var a = _bench.FirstOrDefault(x => x.Label == "After");
        if (a == null && b == null) { BenchTable.Text = "No results yet. Record a BEFORE run first."; return; }
        var sb = new StringBuilder();
        void Row(string name, double? x, double? y, string f) =>
            sb.AppendLine(name.PadRight(40) + N(x, f).PadLeft(10) + N(y, f).PadLeft(10) + Delta(x, y, f).PadLeft(10));
        sb.AppendLine("Metric".PadRight(40) + "Before".PadLeft(10) + "After".PadLeft(10) + "Change".PadLeft(10));
        sb.AppendLine(new string('-', 70));
        Row("Average FPS", b?.AvgFps, a?.AvgFps, "F1");
        Row("1% low FPS", b?.Low1, a?.Low1, "F1");
        Row("Frame-time deviation (ms, lower = smoother)", b?.FrameStdMs, a?.FrameStdMs, "F2");
        Row("CPU utilization (%)", b?.CpuAvg, a?.CpuAvg, "F0");
        Row("GPU utilization (%)", b?.GpuAvg, a?.GpuAvg, "F0");
        Row("RAM used (MB)", b?.RamUsedMb, a?.RamUsedMb, "F0");
        Row("VRAM used (MB)", b?.VramUsedMb, a?.VramUsedMb, "F0");
        Row("Encoder engine load (%)", b?.EncAvg, a?.EncAvg, "F0");
        sb.AppendLine();
        if (b != null) sb.AppendLine($"Before: {b.Game}, {b.Seconds}s, {b.Time:g}");
        if (a != null) sb.AppendLine($"After:  {a.Game}, {a.Seconds}s, {a.Time:g}");
        if (a == null || b == null) sb.AppendLine("Record the other run under the same conditions to see the comparison.");
        BenchTable.Text = sb.ToString();
    }

    void ExportBench_Click(object sender, RoutedEventArgs e)
    {
        if (_bench.Count == 0) { MessageBox.Show("No benchmark results yet. Record a run first.", "LiveBoost"); return; }
        try
        {
            var path = Path.Combine(Storage.Dir, "benchmark-report.txt");
            File.WriteAllText(path, $"LiveBoost benchmark report - {DateTime.Now:g}{Environment.NewLine}{Environment.NewLine}{BenchTable.Text}");
            OpenUri(path);
        }
        catch (Exception ex) { MessageBox.Show("Could not save the report: " + ex.Message); }
    }

    void ClearBench_Click(object sender, RoutedEventArgs e)
    {
        if (_bench.Count == 0) return;
        if (MessageBox.Show("Delete the saved Before/After results?", "LiveBoost", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _bench.Clear();
        Storage.Save("benchmarks.json", _bench);
        ShowBench();
    }

    // ---------- advanced ----------
    static string PlanName()
    {
        var sch = Optimizer.GetActiveScheme();
        return sch == null ? "unknown" : sch == Optimizer.HighPerf ? "High performance" : sch == Optimizer.Balanced ? "Balanced" :
               sch == Optimizer.Ultimate ? "Ultimate performance" : "Custom plan";
    }

    static string DriveText()
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            return $"{d.AvailableFreeSpace / 1073741824.0:F0} GB of {d.TotalSize / 1073741824.0:F0} GB";
        }
        catch { return "unavailable"; }
    }

    void UpdateAdvStatus()
    {
        var gm = Optimizer.GetDword(Optimizer.GameBarKey, "AutoGameModeEnabled");
        var cap = Optimizer.GetDword(Optimizer.GameDvrKey, "AppCaptureEnabled");
        AdvStatus.Text =
            $"Game Mode               {(gm == 1 ? "on" : gm == 0 ? "off" : "Windows default")}\n" +
            $"Background recording    {(cap == 0 ? "off" : cap == 1 ? "on" : "Windows default")}\n" +
            $"LiveBoost boost active  {(_opt.HasChanges ? "yes (use Restore to undo)" : "no")}\n" +
            $"Power plan              {PlanName()}\n" +
            $"System drive free       {DriveText()}";
    }

    void TrimMem_Click(object sender, RoutedEventArgs e)
    {
        var r = ProcessService.TrimOptional(_game?.Pid);
        AdvMem.Text = r.count == 0 ? "No optional background apps were found." :
            $"Trimmed {r.count} optional app(s), about {r.freedMb:F0} MB returned. Nothing was closed; apps reload what they need.";
        Storage.Log("Memory trim: " + AdvMem.Text);
    }

    static bool TempFolderIsSafe(out string dir)
    {
        dir = Path.GetTempPath();
        return dir.Contains("temp", StringComparison.OrdinalIgnoreCase) && dir.TrimEnd('\\').Length > 12;
    }

    static IEnumerable<FileInfo> OldTempFiles(string dir)
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
            if (f.LastWriteTimeUtc < cutoff) yield return f;
    }

    async void ScanTemp_Click(object sender, RoutedEventArgs e)
    {
        if (!TempFolderIsSafe(out var dir)) { AdvTemp.Text = "The temp folder location looks unusual, so LiveBoost will not touch it."; return; }
        TempScanBtn.IsEnabled = false; TempCleanBtn.IsEnabled = false;
        AdvTemp.Text = "Scanning...";
        var (n, bytes) = await Task.Run(() =>
        {
            int c = 0; long b = 0;
            try { foreach (var f in OldTempFiles(dir)) { try { b += f.Length; c++; } catch { } } } catch { }
            return (c, b);
        });
        AdvTemp.Text = n == 0 ? "No temp files older than 24 hours found." : $"{n} old temp file(s), about {bytes / 1048576.0:F0} MB. Click Clean to delete them.";
        TempScanBtn.IsEnabled = true; TempCleanBtn.IsEnabled = n > 0;
    }

    async void CleanTemp_Click(object sender, RoutedEventArgs e)
    {
        if (!TempFolderIsSafe(out var dir)) return;
        if (MessageBox.Show("Permanently delete temp files older than 24 hours from your user temp folder?\nFiles in use are skipped.", "Clean temp files", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        TempScanBtn.IsEnabled = false; TempCleanBtn.IsEnabled = false;
        AdvTemp.Text = "Cleaning...";
        var (done, bytes, skipped) = await Task.Run(() =>
        {
            int d = 0, s = 0; long b = 0;
            try
            {
                foreach (var f in OldTempFiles(dir).ToList())
                {
                    try { long len = f.Length; f.Delete(); d++; b += len; } catch { s++; }
                }
            }
            catch { }
            return (d, b, s);
        });
        AdvTemp.Text = $"Deleted {done} file(s), freed about {bytes / 1048576.0:F0} MB. {skipped} file(s) were in use or protected and skipped.";
        Storage.Log("Temp clean: " + AdvTemp.Text);
        TempScanBtn.IsEnabled = true;
    }

    async void FlushDns_Click(object sender, RoutedEventArgs e)
    {
        AdvNet.Text = "Flushing...";
        var r = await Task.Run(() => Optimizer.Run("ipconfig", "/flushdns"));
        AdvNet.Text = r.code == 0 ? "DNS resolver cache flushed." : "Could not flush DNS: " + r.output.Trim();
    }
    void OpenGraphics_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:display-advancedgraphics");
    void OpenPower_Click(object sender, RoutedEventArgs e) => OpenUri("ms-settings:powersleep");

    // ---------- logout / password ----------
    void Logout_Click(object sender, RoutedEventArgs e)
    {
        Auth.SignOut();
        Auth.LoggingOut = true;
        Close();                       // App returns to the sign-in screen
    }

    // The account server ended this session (device reset by an admin, or this PC no longer matches the registered device).
    void OnSessionRevoked(string message)
    {
        Auth.LoggingOut = true;        // same path as Log out: the app returns to the sign-in screen
        MessageBox.Show(this, message, "Signed out", MessageBoxButton.OK, MessageBoxImage.Warning);
        Close();
    }

    void ChangePw_Click(object sender, RoutedEventArgs e)
    {
        if (!Auth.IsAuthenticated) return;
        if (NewPw.Password != NewPw2.Password) { PwMsg.Text = "The new passwords do not match."; return; }
        try
        {
            Auth.ChangeOwnPassword(CurPw.Password, NewPw.Password);
            CurPw.Clear(); NewPw.Clear(); NewPw2.Clear();
            PwMsg.Text = "Password changed.";
        }
        catch (Exception ex) { PwMsg.Text = ex.Message; }
    }

    // ---------- accounts (admin only; every handler re-checks the role) ----------
    void OpenAccountsPage()
    {
        if (!Auth.IsAdmin) return;
        RefreshAccounts();
        if (string.IsNullOrEmpty(AccUser.Text)) GenBoth_Click(this, new RoutedEventArgs());
    }

    void RefreshAccounts()
    {
        if (!Auth.IsAdmin) return;
        var sel = (AccGrid.SelectedItem as AccountRow)?.Username;
        var rows = Auth.ListAccounts();
        AccGrid.ItemsSource = rows;
        if (sel != null) AccGrid.SelectedItem = rows.FirstOrDefault(r => r.Username == sel);
    }

    void RefreshAcc_Click(object sender, RoutedEventArgs e) => RefreshAccounts();
    void GenUser_Click(object sender, RoutedEventArgs e) { if (!Auth.IsAdmin) return; AccUser.Text = Auth.GenerateUsername(); CreateAccBtn.IsEnabled = true; }
    void GenPass_Click(object sender, RoutedEventArgs e) { if (!Auth.IsAdmin) return; AccPass.Text = Auth.GeneratePassword(16); CreateAccBtn.IsEnabled = true; }
    void GenBoth_Click(object sender, RoutedEventArgs e) { GenUser_Click(sender, e); GenPass_Click(sender, e); }

    void CopyAcc(string text)
    {
        if (!Auth.IsAdmin || string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); AccStatus.Text = "Copied to the clipboard."; }
        catch (Exception ex) { AccStatus.Text = "Could not copy: " + ex.Message; }
    }
    void CopyUser_Click(object sender, RoutedEventArgs e) => CopyAcc(AccUser.Text);
    void CopyPass_Click(object sender, RoutedEventArgs e) => CopyAcc(AccPass.Text);
    void CopyBoth_Click(object sender, RoutedEventArgs e) => CopyAcc($"Username: {AccUser.Text}{Environment.NewLine}Password: {AccPass.Text}");

    void CreateAcc_Click(object sender, RoutedEventArgs e)
    {
        if (!Auth.IsAdmin) return;
        try
        {
            var name = AccUser.Text.Trim();
            var pass = AccPass.Text;
            var role = (AccRole.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "User";
            CreateAccBtn.IsEnabled = false; AccStatus.Text = "Creating account on the server...";
            try { Auth.CreateUser(name, pass, role); }
            catch (Auth.DuplicateUsernameException)
            {
                AccUser.Text = Auth.GenerateUsername(); CreateAccBtn.IsEnabled = true;
                AccStatus.Text = "That username is already taken, so a new one was generated. Review it and click Create account again.";
                return;
            }
            catch { CreateAccBtn.IsEnabled = true; throw; }
            AccStatus.Text = $"Account created on the server. Credentials (copy them now; the password cannot be shown again) - Username: {name}   Password: {pass}";
            RefreshAccounts();
        }
        catch (Exception ex) { AccStatus.Text = ex.Message; }
    }

    void ToggleAcc_Click(object sender, RoutedEventArgs e)
    {
        if (!Auth.IsAdmin) return;
        if (AccGrid.SelectedItem is not AccountRow row) { AccListMsg.Text = "Select an account first."; return; }
        try
        {
            bool wasDisabled = row.Status == "Disabled";
            Auth.SetDisabled(row.Username, !wasDisabled);
            AccListMsg.Text = wasDisabled ? $"{row.Username} re-enabled." : $"{row.Username} disabled; they can no longer sign in.";
            RefreshAccounts();
        }
        catch (Exception ex) { AccListMsg.Text = ex.Message; }
    }

    void ResetAcc_Click(object sender, RoutedEventArgs e)
    {
        if (!Auth.IsAdmin) return;
        if (AccGrid.SelectedItem is not AccountRow row) { AccListMsg.Text = "Select an account first."; return; }
        if (MessageBox.Show($"Generate a new password for '{row.Username}'? Their old password stops working immediately.", "Reset password", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var pw = Auth.GeneratePassword(16);
            Auth.ResetPassword(row.Username, pw);
            AccUser.Text = row.Username; AccPass.Text = pw; CreateAccBtn.IsEnabled = false;
            AccStatus.Text = $"New password set for '{row.Username}'. Copy it now (above); it cannot be shown again.";
            AccListMsg.Text = "Password reset.";
            RefreshAccounts();
        }
        catch (Exception ex) { AccListMsg.Text = ex.Message; }
    }

    void ResetDevice_Click(object sender, RoutedEventArgs e)
    {
        if (!Auth.IsAdmin) return;
        if (AccGrid.SelectedItem is not AccountRow row) { AccListMsg.Text = "Select an account first."; return; }
        if (string.Equals(row.Username, Auth.Current!.Username, StringComparison.OrdinalIgnoreCase))
        {
            AccListMsg.Text = "You can't reset your own device here: it would sign you out. Ask another admin, or delete your record under accounts in the Firebase console.";
            return;
        }
        if (MessageBox.Show($"Reset the registered device for '{row.Username}'? Their next sign-in will register whichever PC they use then. Only do this if they replaced or upgraded their PC.", "Reset device", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            Auth.ResetDevice(row.Username);
            AccListMsg.Text = $"Device reset for {row.Username}. Their next sign-in registers a new PC.";
            RefreshAccounts();
        }
        catch (Exception ex) { AccListMsg.Text = ex.Message; }
    }

    static string DeviceEventText(string result) => result switch
    {
        "registered" => "Device registered",
        "ok" => "Signed in",
        "blocked" => "BLOCKED (other device)",
        "reset" => "Device reset",
        _ => result
    };

    void History_Click(object sender, RoutedEventArgs e)
    {
        if (!Auth.IsAdmin) return;
        if (AccGrid.SelectedItem is not AccountRow row) { AccListMsg.Text = "Select an account first."; return; }
        try
        {
            var d = Auth.GetDevice(row.Username);
            var sb = new StringBuilder();
            if (d.Registered)
            {
                sb.AppendLine($"Registered device: {d.Name} (ID {d.Fingerprint})");
                sb.AppendLine($"Registered: {d.RegisteredOn}");
                sb.AppendLine($"Last seen: {d.LastSeen}");
            }
            else sb.AppendLine("No device registered yet. The next sign-in registers one.");
            sb.AppendLine();
            var entries = Auth.GetLoginHistory(row.Username);
            if (entries.Count == 0) sb.AppendLine("No sign-in history yet.");
            foreach (var en in entries.Take(20))
                sb.AppendLine($"{en.When}   {DeviceEventText(en.Result)}   {en.Device}" +
                              (en.Country.Length > 0 ? $" ({en.Country})" : "") +
                              (en.Note.Length > 0 ? $" - {en.Note}" : ""));
            MessageBox.Show(sb.ToString(), $"Device and sign-in history: {row.Username}", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { AccListMsg.Text = ex.Message; }
    }

    void DeleteAcc_Click(object sender, RoutedEventArgs e)
    {
        if (!Auth.IsAdmin) return;
        if (AccGrid.SelectedItem is not AccountRow row) { AccListMsg.Text = "Select an account first."; return; }
        if (MessageBox.Show($"Permanently delete the account '{row.Username}'? This cannot be undone.", "Delete account", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { Auth.DeleteUser(row.Username); AccListMsg.Text = $"{row.Username} deleted."; RefreshAccounts(); }
        catch (Exception ex) { AccListMsg.Text = ex.Message; }
    }

    // ---------- overlay ----------
    void Overlay_Click(object sender, RoutedEventArgs e) => ToggleOverlay();

    void ToggleOverlay()
    {
        if (_overlay != null) { _overlay.Close(); return; }
        var ov = new OverlayWindow { Owner = this };
        if (!double.IsNaN(_set.OverlayL) && !double.IsNaN(_set.OverlayT) &&
            _set.OverlayL >= SystemParameters.VirtualScreenLeft && _set.OverlayT >= SystemParameters.VirtualScreenTop &&
            _set.OverlayL + 60 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            _set.OverlayT + 40 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            ov.WindowStartupLocation = WindowStartupLocation.Manual; ov.Left = _set.OverlayL; ov.Top = _set.OverlayT;
        }
        ov.Closed += (_, _) =>
        {
            _set.OverlayL = ov.Left; _set.OverlayT = ov.Top; Storage.Save("settings.json", _set);
            _overlay = null; OverlayBtn.Content = "Overlay: off";
        };
        _overlay = ov;
        OverlayBtn.Content = "Overlay: on";
        ov.Show();
        if (_last != null) UpdateOverlay(_last);
    }

    void UpdateOverlay(Snapshot s)
    {
        var sb = new StringBuilder();
        if (_fps.Running && _game != null && _fps.Stats(_game.Pid, 1) is FpsStats fs) sb.AppendLine($"FPS {fs.Avg:F0}   1% low {fs.Low1:F0}");
        else sb.AppendLine(!_set.FpsCapture ? "FPS off (Settings)" : !Elevation.IsAdmin ? "FPS needs admin" : _game == null ? "FPS: no game" : "FPS n/a");
        sb.AppendLine($"CPU {s.CpuPercent:F0}%" + (s.CpuTempC is double ct ? $"  {ct:F0}°C" : ""));
        sb.AppendLine($"GPU {(s.GpuPercent is double gp ? gp.ToString("F0") + "%" : "n/a")}" + (s.GpuTempC is double gt ? $"  {gt:F0}°C" : ""));
        sb.Append($"RAM {s.RamUsedMb / 1024:F1}/{s.RamTotalMb / 1024:F1} GB");
        _overlay?.SetText(sb.ToString());
    }

    void OnKeys(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        switch (e.Key)
        {
            case Key.B: e.Handled = true; DoBoost(); break;
            case Key.R: e.Handled = true; Restore_Click(this, new RoutedEventArgs()); break;
            case Key.O: e.Handled = true; ToggleOverlay(); break;
        }
    }

    // ---------- session recorder ----------
    double? CurrentFps(double window) => _fps.Running && _game != null ? _fps.Stats(_game.Pid, window)?.Avg : null;

    static string F1(double? v) => v.HasValue ? v.Value.ToString("F1", CultureInfo.InvariantCulture) : "";

    void RecToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_rec == null)
        {
            _rec = new();
            RecBtn.Content = "Stop and save recording";
            RecStatus.Text = "Recording... 0 s";
        }
        else StopRecording(true);
    }

    void RecordSample(Snapshot s)
    {
        _rec!.Add((DateTime.Now, s, CurrentFps(1), _game?.Name ?? ""));
        RecStatus.Text = $"Recording... {_rec.Count} s";
        if (_rec.Count >= 14400) StopRecording(true);
    }

    void StopRecording(bool announce)
    {
        var r = _rec; _rec = null;
        RecBtn.Content = "Start recording";
        if (r == null || r.Count == 0) return;
        try
        {
            var dir = Path.Combine(Storage.Dir, "sessions");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"session-{r[0].t:yyyyMMdd-HHmmss}.csv");
            var sb = new StringBuilder("time,game,cpu_pct,cpu_temp_c,gpu_pct,gpu_temp_c,ram_used_mb,vram_used_mb,fps" + Environment.NewLine);
            foreach (var x in r)
                sb.AppendLine(string.Join(",", x.t.ToString("HH:mm:ss", CultureInfo.InvariantCulture), x.game.Replace(',', ' '),
                    F1(x.s.CpuPercent), F1(x.s.CpuTempC), F1(x.s.GpuPercent), F1(x.s.GpuTempC), F1(x.s.RamUsedMb), F1(x.s.VramUsedMb), F1(x.fps)));
            File.WriteAllText(path, sb.ToString());
            RecStatus.Text = $"Saved {r.Count} samples to {path}. Average CPU {r.Average(x => x.s.CpuPercent):F0}%, GPU {N(AvgN(r.Select(x => x.s.GpuPercent)), "F0")}%, FPS {N(AvgN(r.Select(x => x.fps)), "F0")}.";
            Storage.Log("Session recorded: " + path);
            if (announce) OpenUri(dir);
        }
        catch (Exception ex) { RecStatus.Text = "Could not save the recording: " + ex.Message; }
    }

    // ---------- manual priority ----------
    void SetPrio_Click(object sender, RoutedEventArgs e)
    {
        if (ProcGrid.SelectedItem is not ProcRow row) { ProcMsg.Text = "Select a process first."; return; }
        if (row.Kind == Importance.Essential) { ProcMsg.Text = $"{row.Name} is essential and protected."; return; }
        var to = ProcPrio.SelectedIndex switch { 0 => System.Diagnostics.ProcessPriorityClass.BelowNormal, 2 => System.Diagnostics.ProcessPriorityClass.AboveNormal, _ => System.Diagnostics.ProcessPriorityClass.Normal };
        ProcMsg.Text = _opt.SetPriorityTracked(row.Pid, to);
        UpdateBadges();
    }

    // ---------- start with Windows ----------
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static bool StartupEnabled()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath); return k?.GetValue("LiveBoost") != null; }
        catch { return false; }
    }

    void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var exe = Environment.ProcessPath ?? "";
        if (CbStartup.IsChecked == true && Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show("Run the built LiveBoost.exe (not 'dotnet run') to use this setting."); CbStartup.IsChecked = false; return; }
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (CbStartup.IsChecked == true) k.SetValue("LiveBoost", "\"" + exe + "\"");
            else k.DeleteValue("LiveBoost", false);
        }
        catch (Exception ex) { MessageBox.Show("Could not change the startup entry: " + ex.Message); CbStartup.IsChecked = StartupEnabled(); }
    }

    // ---------- EARL TWEAKS ----------
    void LaunchEarl_Click(object sender, RoutedEventArgs e)
    {
        var ask = "EARL TWEAKS will open in a separate window as administrator and change the registry, services, power plans and other Windows settings.\n\n" +
                  "LiveBoost's Restore button cannot undo these changes (use the restore point the tool creates).\n\nLaunch it now?";
        if (MessageBox.Show(ask, "Launch EARL TWEAKS", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var dir = Path.Combine(Storage.Dir, "tools");
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, "EARL_TWEAKS.exe");
            using (var rs = typeof(MainWindow).Assembly.GetManifestResourceStream("EARL_TWEAKS.exe"))
            {
                if (rs == null) throw new FileNotFoundException("EARL_TWEAKS.exe is not embedded in this build.");
                using var fs = File.Create(exe);
                rs.CopyTo(fs);
            }
            Storage.Log("Launched EARL TWEAKS (separate tool; not covered by LiveBoost restore)");
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true, Verb = "runas", WorkingDirectory = dir });
            EarlStatus.Text = "Launched. Follow the prompts in the EARL TWEAKS window.";
        }
        catch (Win32Exception) { EarlStatus.Text = "The administrator request was cancelled."; }
        catch (Exception ex) { EarlStatus.Text = "Could not launch: " + ex.Message; }
    }

    // ---------- settings ----------
    void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _set.AutoSuggest = CbAutoSuggest.IsChecked == true;
        _set.FpsCapture = CbFps.IsChecked == true;
        _set.RestoreOnExit = CbRestoreExit.IsChecked == true;
        _set.AutoBoost = CbAutoBoost.IsChecked == true;
        _set.PingHost = PingHostBox.Text.Trim();
        Storage.Save("settings.json", _set);
        if (_set.FpsCapture) _fps.Start(); else _fps.Dispose();
        UpdateDetection();
    }

    void UpdateBadges()
    {
        AdminBadge.Text = Elevation.IsAdmin ? "● Administrator" : "○ Standard user";
        AdminBadge.Foreground = Elevation.IsAdmin ? Brushes.LightGreen : (Brush)FindResource("Muted");
        ActiveBadge.Text = _opt.HasChanges ? "Boost active (restorable)" : "No boost active";
    }

    void UpdateAdminText()
    {
        UpdateBadges();
        AdminText.Text = Elevation.IsAdmin
            ? "Running as administrator: CPU temperature and frame-time (FPS) measuring are available."
            : "Running without administrator rights. Admin is only needed for two things: reading CPU temperature (hardware sensor driver) and measuring game FPS (Windows event tracing). Everything else works without it.";
    }

    void RestartAdmin_Click(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath ?? "";
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show("You are running through 'dotnet run'. Right-click the built LiveBoost.exe and choose Run as administrator instead."); return; }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true, Verb = "runas", Arguments = "--restarted" });
            Application.Current.Shutdown();
        }
        catch (Win32Exception) { MessageBox.Show("The administrator request was cancelled."); }
    }

    protected override void OnClosed(EventArgs e)
    {
        Auth.SessionRevoked -= OnSessionRevoked;
        _timer.Stop();
        StopRecording(false);
        _sessions.End();
        if (WindowState == WindowState.Normal)
        { _set.WinW = Width; _set.WinH = Height; _set.WinL = Left; _set.WinT = Top; Storage.Save("settings.json", _set); }
        if (_set.RestoreOnExit && _opt.HasChanges) { try { _opt.Restore(); } catch { } }
        _fps.Dispose(); _mon.Dispose();
        base.OnClosed(e);
    }
}
