using System.Threading;
using System.Windows;

namespace GlassPlayer;

public partial class App : Application
{
    static Mutex? _single;
    static EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Showcase mode renders marketing images and exits; it runs alongside a normal instance.
        if (System.Environment.GetEnvironmentVariable("GLASSPLAYER_SHOWCASE") is string dir)
        {
            base.OnStartup(e);
            var s = new MainWindow();
            MainWindow = s;
            s.RunShowcase(dir);
            return;
        }

        _single = new Mutex(true, "GlassPlayer.SingleInstance", out bool first);
        if (!first)
        {
            // Already running (maybe hidden): opening the app again just brings the player back.
            try { EventWaitHandle.OpenExisting("GlassPlayer.Show").Set(); } catch { }
            Shutdown();
            return;
        }
        base.OnStartup(e);

        // Runs quietly in the background (tray icon by the clock); the widget appears when Spotify opens.
        var w = new MainWindow();
        MainWindow = w;
        w.Start();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "GlassPlayer.Show");
        new Thread(() =>
        {
            while (_showSignal.WaitOne()) Dispatcher.Invoke(w.ShowFromTray);
        }) { IsBackground = true }.Start();
    }
}
