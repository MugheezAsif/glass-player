using System;
using System.Drawing;
using System.IO;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace GlassPlayer;

// The little icon by the clock: left-click shows/hides the player, right-click opens the menu.
sealed class Tray : IDisposable
{
    readonly WinForms.NotifyIcon _icon;

    public Tray(MainWindow w)
    {
        var show = new WinForms.ToolStripMenuItem("Show player");
        var top = new WinForms.ToolStripMenuItem("Always on top");
        var spin = new WinForms.ToolStripMenuItem("Spinning record");
        spin.Click += (_, _) => w.ToggleSpin();
        var startup = new WinForms.ToolStripMenuItem("Start with Windows");
        var quit = new WinForms.ToolStripMenuItem("Quit Glass Player");
        show.Font = new Font(show.Font, FontStyle.Bold);
        show.Click += (_, _) => w.ToggleFromTray();
        top.Click += (_, _) => w.ToggleTopmost();
        startup.Click += (_, _) => Startup.Enabled = !Startup.Enabled;
        quit.Click += (_, _) => w.Quit();

        var menu = new WinForms.ContextMenuStrip
        {
            Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = true },
            BackColor = Color.FromArgb(32, 32, 36), ForeColor = Color.White, ShowImageMargin = false,
            Font = new Font("Segoe UI Variable Text", 9.5f), Padding = new WinForms.Padding(4),
        };
        menu.Items.AddRange(new WinForms.ToolStripItem[] { show, new WinForms.ToolStripSeparator(), top, spin, startup, new WinForms.ToolStripSeparator(), quit });
        foreach (WinForms.ToolStripItem i in menu.Items) { i.ForeColor = Color.White; i.Padding = new WinForms.Padding(6, 4, 6, 4); }
        menu.Opening += (_, _) =>
        {
            show.Text = w.IsShown ? "Hide player" : "Show player";
            top.Checked = w.Topmost;
            spin.Checked = w.SpinEnabled;
            startup.Checked = Startup.Enabled;
        };

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(), Text = "Glass Player", ContextMenuStrip = menu, Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) w.ToggleFromTray(); };
    }

    static Icon LoadIcon()
    {
        using var s = typeof(Tray).Assembly.GetManifestResourceStream("app.ico");
        return s == null ? SystemIcons.Application : new Icon(s, WinForms.SystemInformation.SmallIconSize);
    }

    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }

    sealed class DarkColors : WinForms.ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(32, 32, 36), Hover = Color.FromArgb(58, 58, 66), Line = Color.FromArgb(60, 60, 68);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Bg;
        public override Color CheckBackground => Color.FromArgb(50, 110, 240);
        public override Color CheckSelectedBackground => Color.FromArgb(70, 130, 255);
        public override Color CheckPressedBackground => Color.FromArgb(70, 130, 255);
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
    }
}

// "Start with Windows" = a value under the current user's Run key.
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "GlassPlayer";
    static string Exe => $"\"{Environment.ProcessPath}\"";

    public static bool Enabled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(Name) != null; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(Name, Exe); else k.DeleteValue(Name, false);
        }
    }

    // Earlier versions used a Startup-folder shortcut; move that choice over to the Run key once.
    public static void MigrateOldShortcut()
    {
        try
        {
            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Glass Player.lnk");
            if (!File.Exists(lnk)) return;
            File.Delete(lnk);
            Enabled = true;
        }
        catch { }
    }
}
