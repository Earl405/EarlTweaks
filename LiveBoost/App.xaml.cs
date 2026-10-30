using System.Threading;
using System.Windows;
namespace LiveBoost;

public partial class App : Application
{
    Mutex? _single;
    bool _errShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        // "--restarted" is passed by Settings > Restart as administrator: wait for the old instance to exit.
        bool restarted = e.Args.Contains("--restarted");
        _single = new Mutex(false, @"Local\LiveBoost.SingleInstance");
        bool owned;
        try { owned = _single.WaitOne(restarted ? 6000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            MessageBox.Show("LiveBoost is already running. Check the taskbar.", "LiveBoost", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, a) =>
        {
            Storage.Log("Unhandled error: " + a.Exception);
            a.Handled = true;
            if (_errShown) return;
            _errShown = true;
            MessageBox.Show("LiveBoost hit an unexpected error but is still running:\n\n" + a.Exception.Message +
                            "\n\nDetails are in the log (Settings > Open log folder).", "LiveBoost", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        try { ShowLoginThenMain(); }
        catch (Exception ex)
        {
            // never leave a hidden process behind: it would keep the single-instance lock
            Storage.Log("Startup failed: " + ex);
            MessageBox.Show("LiveBoost could not start:\n\n" + ex.Message + "\n\nDetails are in %LocalAppData%\\LiveBoost\\log.txt", "LiveBoost", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>Sign-in always comes first. The main window is only created after a successful sign-in; logging out closes it and returns here.</summary>
    void ShowLoginThenMain()
    {
        Auth.LoggingOut = false;
        var login = new LoginWindow();
        if (login.ShowDialog() != true || !Auth.IsAuthenticated) { Shutdown(); return; }
        var main = new MainWindow();
        MainWindow = main;
        main.Closed += (_, _) => { if (Auth.LoggingOut) ShowLoginThenMain(); else Shutdown(); };
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.ReleaseMutex(); } catch { }
        _single?.Dispose();
        base.OnExit(e);
    }
}
