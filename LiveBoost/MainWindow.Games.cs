using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LiveBoost;

// My Games + Game Performance History pages. Built in code from the real saved sessions (game_sessions.json).
public partial class MainWindow
{
    string? _histGame;                 // null = all games
    GameSession? _histSel;
    string _metric = "FPS";
    Canvas? _graph; TextBlock? _graphNote;

    Brush Res(string k) => (Brush)FindResource(k);
    static string Dur(int s) => $"{s / 3600}h {s % 3600 / 60:D2}m";
    static string N(double? v, string f = "F0", string u = "") => v is double d ? d.ToString(f) + u : "N/A";

    TextBlock Txt(string t, double size = 13, Brush? fg = null, FontWeight? w = null, Thickness? m = null) =>
        new() { Text = t, FontSize = size, Foreground = fg ?? Res("Text"), FontWeight = w ?? FontWeights.Normal, Margin = m ?? new Thickness(0), TextWrapping = TextWrapping.Wrap };

    Border StatBox(string label, string value) => new()
    {
        Background = Res("PanelHi"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8,12, 8), Margin = new Thickness(0, 0, 8, 8), MinWidth = 104,
        Child = new StackPanel { Children = { Txt(label, 11, Res("Muted")), Txt(value, 17, null, FontWeights.SemiBold) } }
    };

    // ================= MY GAMES =================
    void BuildGames()
    {
        GamesHost.Children.Clear();
        GamesHost.Children.Add(Txt("My Games", 26, null, FontWeights.Bold, new Thickness(6, 0, 0, 4)));
        var all = _sessions.Sessions;
        if (all.Count == 0)
        {
            GamesHost.Children.Add(Txt("No sessions yet. Launch a game with LiveBoost open: each play session of 1 minute or more is saved here automatically. " +
                "FPS needs administrator mode and a DirectX 11/12 game.", 13, Res("Muted"), null, new Thickness(6, 8, 0, 0)));
            return;
        }
        var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var grp in all.GroupBy(x => x.Game).OrderByDescending(q => q.Max(x => x.End)))
        {
            var list = grp.ToList();
            var fpsAvgs = list.Where(x => x.AvgFps != null).ToList();
            double? wAvg = fpsAvgs.Count == 0 ? null : fpsAvgs.Sum(x => x.AvgFps!.Value * x.DurationSec) / fpsAvgs.Sum(x => x.DurationSec);
            Border cover;
            var art = GameIcons.LoadCover(grp.Key);       // your own cover image (optional)
            var icon = art == null ? GameIcons.LoadIcon(grp.Key) : null;   // the game's own program icon
            if (art != null)
                cover = new Border { Height = 86, CornerRadius = new CornerRadius(10), Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill } };
            else if (icon != null)
            {
                var img = new Image { Source = icon, Width = 56, Height = 56, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                cover = new Border { Height = 86, CornerRadius = new CornerRadius(10), Background = (Brush)FindResource("PanelHi"), Child = img };
            }
            else
                cover = new Border
                {
                    Height = 86, CornerRadius = new CornerRadius(10), Background = (Brush)FindResource("AccentGrad"),
                    Child = new TextBlock { Text = grp.Key.Substring(0, 1).ToUpperInvariant(), FontSize = 40, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
            var info = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            info.Children.Add(Txt(grp.Key, 15, null, FontWeights.SemiBold));
            info.Children.Add(Txt($"Playtime {Dur(list.Sum(x => x.DurationSec))}  ·  {list.Count} session{(list.Count == 1 ? "" : "s")}", 12, Res("Muted"), null, new Thickness(0, 2, 0, 6)));
            info.Children.Add(Txt($"Last played {list.Max(x => x.End):dd MMM yyyy HH:mm}", 12, Res("Muted")));
            var g = new UniformGrid { Columns = 2, Margin = new Thickness(0, 8, 0, 0) };
            void S(string a, string b) { g.Children.Add(Txt(a, 11, Res("Muted"))); g.Children.Add(Txt(b, 12, null, FontWeights.SemiBold)); }
            S("Avg FPS", N(wAvg)); S("Max FPS", N(list.Max(x => x.MaxFps)));
            S("Min FPS", N(list.Min(x => x.MinFps))); S("1% low", N(list.Min(x => x.Low1Fps)));
            S("Frametime", N(list.Where(x => x.AvgFrameMs != null).Select(x => x.AvgFrameMs).DefaultIfEmpty(null).Average(), "F1", " ms"));
            S("CPU / GPU", $"{N(list.Average(x => x.AvgCpu), "F0", "%")} / {N(list.Where(x => x.AvgGpu != null).Select(x => x.AvgGpu).DefaultIfEmpty(null).Average(), "F0", "%")}");
            info.Children.Add(g);
            var btn = new Button { Content = "View history", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 4,10, 4), Tag = grp.Key };
            btn.Click += (s, _) => { _histGame = (string)((Button)s).Tag; _histSel = null; GoPage("History"); };
            info.Children.Add(btn);
            var card = new Border { Style = (Style)FindResource("Card"), Width = 250, Child = new StackPanel { Children = { cover, info } } };
            wrap.Children.Add(card);
        }
        GamesHost.Children.Add(wrap);
    }

    void GoPage(string tag)
    {
        foreach (var b in NavPanel.Children.OfType<Button>())
            if ((string)b.Tag == tag) { Nav_Click(b, new RoutedEventArgs()); return; }
    }

    // ================= GAME PERFORMANCE HISTORY =================
    void BuildHistory()
    {
        GamesHost2.Children.Clear();
        GamesHost2.Children.Add(Txt("Game Performance History", 26, null, FontWeights.Bold, new Thickness(6, 0, 0, 4)));
        var all = _sessions.Sessions;
        if (all.Count == 0)
        {
            GamesHost2.Children.Add(Txt("No saved sessions yet. They appear here after you play a game for 1 minute or more.", 13, Res("Muted"), null, new Thickness(6, 8, 0, 0)));
            return;
        }
        // filter
        var games = all.Select(x => x.Game).Distinct().OrderBy(x => x).ToList();
        var filter = new ComboBox { Width = 240, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 4, 0, 8) };
        filter.Items.Add("All games"); foreach (var g in games) filter.Items.Add(g);
        filter.SelectedItem = _histGame != null && games.Contains(_histGame) ? _histGame : "All games";
        filter.SelectionChanged += (_, _) => { _histGame = filter.SelectedIndex <= 0 ? null : (string)filter.SelectedItem; _histSel = null; BuildHistory(); };
        GamesHost2.Children.Add(filter);

        var sess = all.Where(x => _histGame == null || x.Game == _histGame).OrderByDescending(x => x.Start).ToList();
        if (_histSel == null || !sess.Contains(_histSel)) _histSel = sess.FirstOrDefault();

        // session list
        var lb = new ListBox { Height = 150, Background = Res("Panel"), BorderBrush = Res("Line"), Foreground = Res("Text"), Margin = new Thickness(6, 0, 6, 8) };
        foreach (var x in sess) lb.Items.Add(new ListBoxItem { Content = SessionLabel(x), Tag = x, IsSelected = x == _histSel, Foreground = Res("Text") });
        lb.SelectionChanged += (_, _) => { if (lb.SelectedItem is ListBoxItem li && li.Tag is GameSession gs && gs != _histSel) { _histSel = gs; BuildHistoryDetail(); } };
        GamesHost2.Children.Add(lb);

        _detailHost = new StackPanel();
        GamesHost2.Children.Add(_detailHost);
        BuildHistoryDetail();

        // before / after
        var same = sess.Select(x => x).ToList();
        if (same.Count >= 2) GamesHost2.Children.Add(BuildCompare(same));
        else GamesHost2.Children.Add(Txt("Play this game at least twice (for example before and after optimizing) to compare sessions.", 12, Res("Muted"), null, new Thickness(6, 4, 0, 0)));
    }
    StackPanel? _detailHost;

    static string SessionLabel(GameSession x) =>
        $"{x.Start:dd MMM yyyy HH:mm}   {x.Game}   {Dur(x.DurationSec)}   avg {N(x.AvgFps)} FPS   {(x.Optimized ? "optimized" : "not optimized")}";

    void BuildHistoryDetail()
    {
        if (_detailHost == null) return;
        _detailHost.Children.Clear();
        var x = _histSel; if (x == null) return;
        var card = new Border { Style = (Style)FindResource("Card") };
        var sp = new StackPanel();
        sp.Children.Add(Txt($"{x.Game}  ·  {x.Start:dd MMM yyyy HH:mm} → {x.End:HH:mm}", 15, null, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        var stats = new WrapPanel();
        stats.Children.Add(StatBox("Duration", Dur(x.DurationSec)));
        stats.Children.Add(StatBox("Avg FPS", N(x.AvgFps)));
        stats.Children.Add(StatBox("Max FPS", N(x.MaxFps)));
        stats.Children.Add(StatBox("Min FPS", N(x.MinFps)));
        stats.Children.Add(StatBox("1% low", N(x.Low1Fps)));
        stats.Children.Add(StatBox("Frametime", N(x.AvgFrameMs, "F1", " ms")));
        stats.Children.Add(StatBox("CPU usage", N(x.AvgCpu, "F0", "%")));
        stats.Children.Add(StatBox("GPU usage", N(x.AvgGpu, "F0", "%")));
        stats.Children.Add(StatBox("Max CPU temp", N(x.MaxCpuTemp, "F0", " °C")));
        stats.Children.Add(StatBox("Max GPU temp", N(x.MaxGpuTemp, "F0", " °C")));
        sp.Children.Add(stats);

        var metric = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 6) };
        foreach (var m in new[] { "FPS", "Frametime", "CPU usage", "GPU usage", "CPU temp", "GPU temp" }) metric.Items.Add(m);
        metric.SelectedItem = _metric;
        metric.SelectionChanged += (_, _) => { _metric = (string)metric.SelectedItem; DrawGraph(); };
        sp.Children.Add(metric);
        _graph = new Canvas { Height = 190, Background = Res("Bg"), ClipToBounds = true };
        _graph.SizeChanged += (_, _) => DrawGraph();
        sp.Children.Add(new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = _graph, ClipToBounds = true });
        _graphNote = Txt("", 11, Res("Muted"), null, new Thickness(0, 4, 0, 0));
        sp.Children.Add(_graphNote);
        card.Child = sp;
        _detailHost.Children.Add(card);
        DrawGraph();
    }

    void DrawGraph()
    {
        if (_graph == null || _histSel == null) return;
        _graph.Children.Clear();
        double W = _graph.ActualWidth, H = _graph.ActualHeight;
        if (W < 20 || H < 20) return;
        Func<SessionPoint, double?> sel = _metric switch
        {
            "FPS" => p => p.Fps, "Frametime" => p => p.FrameMs, "CPU usage" => p => p.Cpu, "GPU usage" => p => p.Gpu,
            "CPU temp" => p => p.CpuTemp, _ => p => p.GpuTemp
        };
        var pts = _histSel.Points.Where(p => sel(p) != null).Select(p => (t: (double)p.T, v: sel(p)!.Value)).ToList();
        if (pts.Count < 2) { _graphNote!.Text = $"{_metric} was not measured in this session (no data to plot)."; return; }
        double max = pts.Max(p => p.v), min = Math.Min(0, pts.Min(p => p.v)); if (max <= min) max = min + 1;
        double tMax = Math.Max(1, pts.Max(p => p.t));
        const double padL = 40, padB = 4, padT = 8;
        for (int i = 0; i <= 3; i++)
        {
            double y = padT + (H - padT - padB) * i / 3.0;
            _graph.Children.Add(new Line { X1 = padL, X2 = W, Y1 = y, Y2 = y, Stroke = Res("Line"), StrokeThickness = 1 });
            var lab = Txt((max - (max - min) * i / 3.0).ToString("F0"), 10, Res("Muted")); Canvas.SetLeft(lab, 4); Canvas.SetTop(lab, y - 8); _graph.Children.Add(lab);
        }
        var poly = new Polyline { Stroke = Res("Accent"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        foreach (var p in pts)
            poly.Points.Add(new Point(padL + (W - padL - 6) * p.t / tMax, padT + (H - padT - padB) * (1 - (p.v - min) / (max - min))));
        _graph.Children.Add(poly);
        _graphNote!.Text = $"{_metric} over the session ({pts.Count} samples, one every 5 s). Average {pts.Average(p => p.v):F1}, peak {pts.Max(p => p.v):F1}, lowest {pts.Min(p => p.v):F1}.";
    }

    // ---- before / after comparison between two saved sessions ----
    Border BuildCompare(List<GameSession> sess)
    {
        var card = new Border { Style = (Style)FindResource("Card") };
        var sp = new StackPanel();
        sp.Children.Add(Txt("Before / after comparison", 15, null, FontWeights.SemiBold, new Thickness(0, 0, 0, 6)));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var a = new ComboBox { Width = 330, Margin = new Thickness(0, 0, 8, 0) };
        var b = new ComboBox { Width = 330 };
        foreach (var x in sess) { a.Items.Add(new ComboBoxItem { Content = SessionLabel(x), Tag = x }); b.Items.Add(new ComboBoxItem { Content = SessionLabel(x), Tag = x }); }
        a.SelectedIndex = Math.Min(1, sess.Count - 1); b.SelectedIndex = 0;     // A = older, B = latest
        row.Children.Add(Txt("Before:", 12, Res("Muted"), null, new Thickness(0, 4, 6, 0))); row.Children.Add(a);
        row.Children.Add(Txt("After:", 12, Res("Muted"), null, new Thickness(8, 4, 6, 0))); row.Children.Add(b);
        sp.Children.Add(row);
        var table = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        sp.Children.Add(table);
        void Fill()
        {
            table.Children.Clear(); table.ColumnDefinitions.Clear(); table.RowDefinitions.Clear();
            for (int i = 0; i < 4; i++) table.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(110) });
            if (a.SelectedItem is not ComboBoxItem ia || b.SelectedItem is not ComboBoxItem ib) return;
            var A = (GameSession)ia.Tag; var B = (GameSession)ib.Tag;
            var rows = new (string name, double? va, double? vb, string f, string unit, bool higherBetter)[]
            {
                ("Average FPS", A.AvgFps, B.AvgFps, "F0", "", true), ("1% low FPS", A.Low1Fps, B.Low1Fps, "F0", "", true),
                ("Min FPS", A.MinFps, B.MinFps, "F0", "", true), ("Frametime", A.AvgFrameMs, B.AvgFrameMs, "F1", " ms", false),
                ("CPU usage", A.AvgCpu, B.AvgCpu, "F0", "%", false), ("GPU usage", A.AvgGpu, B.AvgGpu, "F0", "%", false),
                ("Max CPU temp", A.MaxCpuTemp, B.MaxCpuTemp, "F0", " °C", false), ("Max GPU temp", A.MaxGpuTemp, B.MaxGpuTemp, "F0", " °C", false),
            };
            int r = 0;
            void Cell(string t, int c, Brush? fg = null, bool bold = false) { var tb = Txt(t, 12, fg ?? (c == 0 ? Res("Muted") : Res("Text")), bold ? FontWeights.SemiBold : null, new Thickness(0, 3, 0, 3)); Grid.SetRow(tb, r); Grid.SetColumn(tb, c); table.Children.Add(tb); }
            table.RowDefinitions.Add(new RowDefinition()); Cell("Metric", 0); Cell("Before", 1); Cell("After", 2); Cell("Change", 3); r++;
            foreach (var x in rows)
            {
                table.RowDefinitions.Add(new RowDefinition());
                Cell(x.name, 0); Cell(N(x.va, x.f, x.unit), 1); Cell(N(x.vb, x.f, x.unit), 2, null, true);
                if (x.va is double p && x.vb is double q && p != 0)
                {
                    double pct = (q - p) / p * 100; bool good = (pct > 0) == x.higherBetter;
                    Cell(Math.Abs(pct) < 0.5 ? "no change" : $"{pct:+0.#;-0.#}%", 3, Math.Abs(pct) < 0.5 ? Res("Muted") : new SolidColorBrush(good ? Color.FromRgb(0x3D, 0xDC, 0x84) : Color.FromRgb(0xFF, 0x6B, 0x6B)), true);
                }
                else Cell("N/A", 3, Res("Muted"));
                r++;
            }
            if (A.Game != B.Game) { table.RowDefinitions.Add(new RowDefinition()); Cell("Note: different games selected.", 0, Res("Muted")); }
        }
        a.SelectionChanged += (_, _) => Fill(); b.SelectionChanged += (_, _) => Fill();
        Fill();
        card.Child = sp;
        return card;
    }
}
