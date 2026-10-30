using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace LiveBoost;

public class FpsStats { public double Avg, Low1, StdMs; public int Frames; }

/// <summary>
/// Measures real frame cadence from the Windows ETW provider Microsoft-Windows-DXGI (Present calls, event 42).
/// Primary source: DXGI Present events (DirectX 11/12). Fallback source (turned on only when DXGI shows nothing for the game):
/// the Windows graphics kernel (Microsoft-Windows-DxgKrnl) Present events, which every graphics API (DX9, Vulkan, OpenGL) goes through.
/// Needs administrator rights.
/// If nothing is captured, the UI shows "N/A" - values are never estimated.
/// </summary>
public sealed class FpsCapture : IDisposable
{
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    const int PresentInfoId = 184;          // DxgKrnl Present_Info
    const string SessionName = "LiveBoostFpsSession";
    TraceEventSession? _session;
    readonly object _lock = new();
    readonly Dictionary<int, List<double>> _times = new();
    readonly Dictionary<int, long> _seen = new();
    readonly Dictionary<int, List<double>> _ktimes = new();
    readonly Dictionary<int, long> _kseen = new();
    readonly Dictionary<long, int> _idCount = new();   // (pid, DxgKrnl event id) -> count, for diagnostics
    bool _fallback;
    long _dxgiAll, _dxgiPresent;     // diagnostics: DXGI events of any kind / Present events
    public bool FallbackActive => _fallback;
    public string? FallbackError { get; private set; }

    public bool Running => _session != null;
    public string? Error { get; private set; }

    public bool Start()
    {
        if (_session != null) return true;
        if (!Elevation.IsAdmin) { Error = "Administrator rights are required to read frame timing (ETW)."; return false; }
        try
        {
            try { TraceEventSession.GetActiveSession(SessionName)?.Stop(); } catch { }
            var s = new TraceEventSession(SessionName);
            s.EnableProvider(Dxgi);
            s.Source.AllEvents += OnEvent;
            _session = s;
            Task.Run(() => { try { s.Source.Process(); } catch { } });
            Error = null;
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            try { _session?.Dispose(); } catch { }
            _session = null;
            return false;
        }
    }

    void Add(Dictionary<int, List<double>> times, Dictionary<int, long> seen, int pid, double t)
    {
        lock (_lock)
        {
            if (!times.TryGetValue(pid, out var l)) times[pid] = l = new List<double>();
            l.Add(t);
            if (l.Count > 30000) l.RemoveRange(0, 10000);
            seen[pid] = Stopwatch.GetTimestamp();
        }
    }

    void OnEvent(TraceEvent e)
    {
        if (e.ProviderGuid == Dxgi)
        {
            Interlocked.Increment(ref _dxgiAll);
            if ((int)e.ID == 42) Interlocked.Increment(ref _dxgiPresent);
            if ((int)e.ID == 42) Add(_times, _seen, e.ProcessID, e.TimeStampRelativeMSec);
            return;
        }
        if (e.ProviderGuid == DxgKrnl)
        {
            int pid = e.ProcessID, id = (int)e.ID;
            if (pid <= 0) return;
            lock (_lock) { long k = ((long)pid << 16) | (uint)(id & 0xFFFF); _idCount[k] = _idCount.GetValueOrDefault(k) + 1; }
            if (id == PresentInfoId) Add(_ktimes, _kseen, pid, e.TimeStampRelativeMSec);
        }
    }

    void EnableFallback()
    {
        if (_fallback || _session == null) return;
        _fallback = true;
        try { _session.EnableProvider(DxgKrnl, TraceEventLevel.Verbose, ulong.MaxValue); }
        catch (Exception ex) { FallbackError = ex.Message; }
    }

    /// <summary>Real frame stats for a process. Tries DXGI first; if it sees nothing, uses the graphics-kernel Present events.</summary>
    public FpsStats? Stats(int pid, double windowSec)
    {
        var r = Calc(_times, _seen, pid, windowSec);
        if (r != null) return r;
        if (!_fallback) EnableFallback();
        return Calc(_ktimes, _kseen, pid, windowSec);
    }

    /// <summary>One-line state of the measurement, shown when no FPS is available.</summary>
    public string Status(int pid)
    {
        if (_session == null) return Error != null ? "Frame timing could not start: " + Error : "Frame timing is not running.";
        int mine; lock (_lock) mine = _times.TryGetValue(pid, out var l) ? l.Count : 0;
        return $"Frame timing is running. DirectX events seen: {Interlocked.Read(ref _dxgiAll)} (present events {Interlocked.Read(ref _dxgiPresent)}, from this game {mine}). " + Diagnostic(pid);
    }

    /// <summary>Which graphics-kernel event ids this process produced (helps verify the fallback). Empty until the fallback is on.</summary>
    public string Diagnostic(int pid)
    {
        if (!_fallback) return "";
        if (FallbackError != null) return "Fallback failed to start: " + FallbackError;
        List<string> top;
        lock (_lock)
            top = _idCount.Where(kv => (int)(kv.Key >> 16) == pid).OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{(int)(kv.Key & 0xFFFF)}x{kv.Value}").ToList();
        return top.Count == 0 ? "Graphics-kernel events: none seen yet from this game."
                              : $"Graphics-kernel events seen (id x count): {string.Join(", ", top)}. Present events are id {PresentInfoId}.";
    }

    FpsStats? Calc(Dictionary<int, List<double>> times, Dictionary<int, long> seen, int pid, double windowSec)
    {
        List<double> w;
        lock (_lock)
        {
            if (!times.TryGetValue(pid, out var l) || l.Count < 10 || !seen.TryGetValue(pid, out var last)) return null;
            if (Stopwatch.GetElapsedTime(last).TotalSeconds > 2) return null;
            double end = l[l.Count - 1];
            w = l.Where(t => t >= end - windowSec * 1000).ToList();
        }
        if (w.Count < 10) return null;
        var dt = new List<double>();
        for (int i = 1; i < w.Count; i++) { var d = w[i] - w[i - 1]; if (d > 0 && d < 2000) dt.Add(d); }
        if (dt.Count < 5) return null;
        double mean = dt.Average();
        double sd = Math.Sqrt(dt.Sum(x => (x - mean) * (x - mean)) / dt.Count);
        double worst = dt.OrderByDescending(x => x).Take(Math.Max(1, (int)Math.Ceiling(dt.Count * 0.01))).Average();
        return new FpsStats { Avg = 1000.0 / mean, Low1 = 1000.0 / worst, StdMs = sd, Frames = dt.Count + 1 };
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
    }
}
