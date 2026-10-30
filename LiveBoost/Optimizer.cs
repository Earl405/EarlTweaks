using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LiveBoost;

public class PlannedChange
{
    public string Description { get; set; } = "";
    /// <summary>Null means this line is only an informational note (e.g. "skipped").</summary>
    public Action? Apply { get; set; }
}

/// <summary>
/// Safe, reversible optimizations only: Windows power plan (powercfg), process priority classes and a few per-user
/// Windows gaming settings (Game Mode, background capture, per-game GPU preference). No process killing, no file changes. Original values are saved to state.json
/// so they can be restored even after LiveBoost is restarted.
/// </summary>
public class Optimizer
{
    public const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    public SavedState State { get; private set; } = Storage.Load("state.json", new SavedState());
    public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    readonly List<string> _notes = new();
    public bool HasChanges => State.OriginalScheme != null || State.CreatedScheme != null || State.Priorities.Count > 0
        || State.GameModeSaved || State.CaptureSaved || State.GpuPrefAdded.Count > 0;

    public const string GameBarKey = @"Software\Microsoft\GameBar";
    public const string GameDvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    public const string GpuPrefKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public static int? GetDword(string sub, string name)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(sub); return k?.GetValue(name) is int i ? (int?)i : null; }
        catch { return null; }
    }
    static void SetDword(string sub, string name, int v) { using var k = Registry.CurrentUser.CreateSubKey(sub); k.SetValue(name, v, RegistryValueKind.DWord); }
    static string? GetString(string sub, string name)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(sub); return k?.GetValue(name) as string; } catch { return null; }
    }
    static void SetString(string sub, string name, string v) { using var k = Registry.CurrentUser.CreateSubKey(sub); k.SetValue(name, v, RegistryValueKind.String); }
    static void DelValue(string sub, string name)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(sub, true); k?.DeleteValue(name, false); } catch { }
    }

    // Some PCs (many laptops, Windows 11 "modern standby") hide the High performance plan. If it can't be
    // activated directly, create it from Windows' built-in template with powercfg, then activate that copy.
    bool ActivatePlan(string target)
    {
        if (Run("powercfg", "/setactive " + target).code == 0) return true;
        var templates = target == HighPerf ? new[] { HighPerf, Ultimate } : new[] { target };
        foreach (var template in templates)
        {
            var r = Run("powercfg", "/duplicatescheme " + template);
            var m = Regex.Match(r.output, @"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
            if (r.code != 0 || !m.Success) continue;
            var g = m.Value.ToLowerInvariant();
            if (Run("powercfg", "/setactive " + g).code == 0) { State.CreatedScheme = g; return true; }
            Run("powercfg", "/delete " + g);
        }
        return false;
    }

    internal static (int code, string output) Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    public static string? GetActiveScheme()
    {
        var r = Run("powercfg", "/getactivescheme");
        var m = Regex.Match(r.output, @"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
        return r.code == 0 && m.Success ? m.Value.ToLowerInvariant() : null;
    }

    bool SetPrio(Process p, ProcessPriorityClass to)
    {
        try
        {
            var orig = p.PriorityClass;
            if (orig == to) return false;
            long ticks = p.StartTime.Ticks;
            p.PriorityClass = to;
            if (!State.Priorities.Any(x => x.Pid == p.Id && x.StartTicks == ticks))
                State.Priorities.Add(new PriorityChange { Pid = p.Id, Name = p.ProcessName, Original = orig.ToString(), StartTicks = ticks });
            return true;
        }
        catch { return false; }
    }

    /// <summary>Manual priority change from the Process Manager. The original priority is saved, so Restore undoes it.</summary>
    public string SetPriorityTracked(int pid, ProcessPriorityClass to)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!SetPrio(p, to)) return "Priority not changed (already that value, or access was denied).";
            Storage.Save("state.json", State);
            Storage.Log($"Manual priority: {p.ProcessName} -> {to}");
            return $"{p.ProcessName} set to {to}. Use Restore previous settings to undo.";
        }
        catch (Exception ex) { return ex.Message; }
    }

    static ProcessPriorityClass? GetPrio(Process p) { try { return p.PriorityClass; } catch { return null; } }

    public List<PlannedChange> BuildPlan(Profile prof, GameInfo? game)
    {
        var plan = new List<PlannedChange>();

        if (prof.SetPowerPlan)
        {
            string target = prof.PowerScheme == "balanced" ? Balanced : HighPerf;
            string label = prof.PowerScheme == "balanced" ? "Balanced" : "High performance";
            string? cur = GetActiveScheme();
            if (cur != target)
            {
                plan.Add(new PlannedChange
                {
                    Description = $"Switch the Windows power plan to {label}",
                    Apply = () =>
                    {
                        bool firstChange = State.OriginalScheme == null;
                        if (firstChange) State.OriginalScheme = cur;
                        if (!ActivatePlan(target))
                        {
                            if (firstChange) State.OriginalScheme = null;
                            _notes.Add($"Skipped: this PC does not support the {label} power plan, so the power plan was left unchanged. Other changes still apply.");
                        }
                    }
                });
            }
            else plan.Add(new PlannedChange { Description = $"(already active) Power plan is already {label}" });
        }

        if (prof.BoostGame)
        {
            if (game == null) plan.Add(new PlannedChange { Description = "(skipped) No game selected or detected, so game priority is unchanged" });
            else
            {
                var g = game;
                plan.Add(new PlannedChange
                {
                    Description = $"Raise {g.Name} priority to Above Normal (never High or Realtime)",
                    Apply = () => { using var p = Process.GetProcessById(g.Pid); SetPrio(p, ProcessPriorityClass.AboveNormal); }
                });
            }
        }

        if (prof.BoostTikTok)
        {
            var ids = new List<int>();
            foreach (var p in Process.GetProcesses())
            {
                try { if (ProcessService.IsTikTok(p.ProcessName)) ids.Add(p.Id); } catch { } finally { p.Dispose(); }
            }
            if (ids.Count == 0) plan.Add(new PlannedChange { Description = "(skipped) TikTok LIVE Studio is not running, so its priority is unchanged" });
            else
                plan.Add(new PlannedChange
                {
                    Description = $"Raise TikTok LIVE Studio priority to Above Normal ({ids.Count} process(es); it is never closed)",
                    Apply = () => { foreach (var id in ids) { try { using var p = Process.GetProcessById(id); SetPrio(p, ProcessPriorityClass.AboveNormal); } catch { } } }
                });
        }

        if (prof.LowerBackground)
        {
            var targets = new List<(int pid, string name)>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (game != null && p.Id == game.Pid) continue;
                    if (ProcessService.Classify(p.ProcessName) != Importance.Optional) continue;
                    if (GetPrio(p) == ProcessPriorityClass.Normal) targets.Add((p.Id, p.ProcessName));
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (targets.Count > 0)
            {
                var names = string.Join(", ", targets.Select(t => t.name).Distinct().Take(6));
                plan.Add(new PlannedChange
                {
                    Description = $"Lower priority to Below Normal for {targets.Count} optional background process(es): {names} (nothing is closed)",
                    Apply = () => { foreach (var t in targets) { try { using var p = Process.GetProcessById(t.pid); SetPrio(p, ProcessPriorityClass.BelowNormal); } catch { } } }
                });
            }
            else plan.Add(new PlannedChange { Description = "(skipped) No optional background apps at Normal priority were found" });
        }
        if (prof.GameModeTweaks)
        {
            int? gm = GetDword(GameBarKey, "AutoGameModeEnabled");
            int? cap = GetDword(GameDvrKey, "AppCaptureEnabled");
            bool needGm = gm != 1, needCap = cap != 0;
            if (needGm || needCap)
            {
                var what = new List<string>();
                if (needGm) what.Add("turn Game Mode on");
                if (needCap) what.Add("turn background game recording off");
                plan.Add(new PlannedChange
                {
                    Description = "Windows gaming tweaks (per-user settings): " + string.Join(" and ", what),
                    Apply = () =>
                    {
                        if (needGm) { if (!State.GameModeSaved) { State.GameModeSaved = true; State.GameModeOrig = gm; } SetDword(GameBarKey, "AutoGameModeEnabled", 1); }
                        if (needCap) { if (!State.CaptureSaved) { State.CaptureSaved = true; State.CaptureOrig = cap; } SetDword(GameDvrKey, "AppCaptureEnabled", 0); }
                    }
                });
            }
            else plan.Add(new PlannedChange { Description = "(already set) Game Mode is on and background recording is off" });
        }

        if (prof.GpuPreference)
        {
            if (game == null) plan.Add(new PlannedChange { Description = "(skipped) No game selected, so the GPU preference is unchanged" });
            else
            {
                string? path = null;
                try { using var gp = Process.GetProcessById(game.Pid); path = gp.MainModule?.FileName; } catch { }
                if (string.IsNullOrEmpty(path)) plan.Add(new PlannedChange { Description = "(skipped) Could not read the game's file path (protected process), so the GPU preference is unchanged" });
                else
                {
                    const string target = "GpuPreference=2;";
                    string? cur = GetString(GpuPrefKey, path);
                    if (cur == target) plan.Add(new PlannedChange { Description = $"(already set) {game.Name} already uses the high-performance GPU" });
                    else
                        plan.Add(new PlannedChange
                        {
                            Description = $"Set {game.Name} to use the high-performance GPU (takes effect the next time the game starts)",
                            Apply = () =>
                            {
                                if (!State.GpuPrefOld.ContainsKey(path)) State.GpuPrefOld[path] = cur ?? "";
                                if (!State.GpuPrefAdded.Contains(path)) State.GpuPrefAdded.Add(path);
                                SetString(GpuPrefKey, path, target);
                            }
                        });
                }
            }
        }

        if (prof.TrimMemory)
        {
            int? keep = game?.Pid;
            plan.Add(new PlannedChange
            {
                Description = "Trim idle memory of optional background apps (browsers, launchers; nothing is closed, nothing to restore)",
                Apply = () =>
                {
                    var r = ProcessService.TrimOptional(keep);
                    _notes.Add(r.count == 0 ? "Memory trim: no optional background apps found." : $"Memory trim: {r.count} optional app(s), about {r.freedMb:F0} MB returned to the system.");
                }
            });
        }
        return plan;
    }

    /// <summary>Applies the plan. If any real change throws, everything done so far is rolled back automatically.</summary>
    public List<string> Apply(List<PlannedChange> plan)
    {
        var log = new List<string>();
        try
        {
            foreach (var c in plan.Where(x => x.Apply != null))
            {
                c.Apply!();
                Storage.Save("state.json", State);
                if (_notes.Count > 0) { log.AddRange(_notes); foreach (var n in _notes) Storage.Log(n); _notes.Clear(); }
                else { log.Add("Applied: " + c.Description); Storage.Log("Applied: " + c.Description); }
            }
        }
        catch (Exception ex)
        {
            log.Add("FAILED: " + ex.Message + " - rolling back all changes.");
            Storage.Log("FAILED: " + ex.Message + " - rolling back");
            log.AddRange(Restore());
        }
        return log;
    }

    public List<string> Restore()
    {
        var log = new List<string>();
        if (!HasChanges) { log.Add("Nothing to restore: LiveBoost has no active changes."); return log; }
        if (State.Priorities.Count > 0)
        {
            int n = 0;
            foreach (var c in State.Priorities)
            {
                try
                {
                    using var p = Process.GetProcessById(c.Pid);
                    if (p.StartTime.Ticks != c.StartTicks) continue;
                    p.PriorityClass = Enum.Parse<ProcessPriorityClass>(c.Original);
                    n++;
                }
                catch { /* process already exited */ }
            }
            log.Add($"Restored original priority for {n} of {State.Priorities.Count} process(es) (the rest had exited).");
            State.Priorities.Clear();
        }
        if (State.GameModeSaved)
        {
            try
            {
                if (State.GameModeOrig is int g) SetDword(GameBarKey, "AutoGameModeEnabled", g); else DelValue(GameBarKey, "AutoGameModeEnabled");
                State.GameModeSaved = false; State.GameModeOrig = null;
                log.Add("Restored the original Game Mode setting.");
            }
            catch (Exception ex) { log.Add("Could not restore Game Mode: " + ex.Message + " (kept so you can retry)."); }
        }
        if (State.CaptureSaved)
        {
            try
            {
                if (State.CaptureOrig is int c) SetDword(GameDvrKey, "AppCaptureEnabled", c); else DelValue(GameDvrKey, "AppCaptureEnabled");
                State.CaptureSaved = false; State.CaptureOrig = null;
                log.Add("Restored the original background game-recording setting.");
            }
            catch (Exception ex) { log.Add("Could not restore background recording: " + ex.Message + " (kept so you can retry)."); }
        }
        if (State.GpuPrefAdded.Count > 0)
        {
            int n = 0;
            foreach (var gpath in State.GpuPrefAdded.ToList())
            {
                try
                {
                    State.GpuPrefOld.TryGetValue(gpath, out var old);
                    if (string.IsNullOrEmpty(old)) DelValue(GpuPrefKey, gpath); else SetString(GpuPrefKey, gpath, old);
                    n++;
                }
                catch { }
            }
            State.GpuPrefAdded.Clear(); State.GpuPrefOld.Clear();
            log.Add($"Restored the GPU preference for {n} game(s).");
        }
        if (State.OriginalScheme != null)
        {
            var r = Run("powercfg", "/setactive " + State.OriginalScheme);
            if (r.code == 0)
            {
                log.Add("Restored the original Windows power plan.");
                State.OriginalScheme = null;
                if (State.CreatedScheme != null) { Run("powercfg", "/delete " + State.CreatedScheme); State.CreatedScheme = null; }
            }
            else log.Add("Could not restore the power plan: " + r.output.Trim() + " (it stays saved so you can retry).");
        }
        Storage.Save("state.json", State);
        foreach (var l in log) Storage.Log(l);
        return log;
    }

    public static List<string> GetStartupEntries()
    {
        var l = new List<string>();
        foreach (var (root, path) in new[] { (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"), (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run") })
        {
            try { using var k = root.OpenSubKey(path); if (k != null) l.AddRange(k.GetValueNames().Where(n => n.Length > 0)); } catch { }
        }
        return l.Distinct().OrderBy(x => x).ToList();
    }
}
