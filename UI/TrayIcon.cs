using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DynamicIsland.UI;

/// <summary>System tray icon. Right-click it for Settings and Exit.
/// The island itself does not expose a context menu.</summary>
public class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _miSettings;
    private readonly ToolStripMenuItem _miExit;

    public TrayIcon(Action openSettings, Action exit)
    {
        _miSettings = new ToolStripMenuItem();
        _miSettings.Click += (_, _) => openSettings();
        _miExit = new ToolStripMenuItem();
        _miExit.Click += (_, _) => exit();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_miSettings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miExit);

        _icon = new NotifyIcon
        {
            Icon = BuildIcon(),
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => openSettings();

        ApplyTexts();
        App.Loc.Changed += ApplyTexts;
    }

    private void ApplyTexts()
    {
        _icon.Text = App.Loc.T("app.name");
        _miSettings.Text = App.Loc.T("tray.settings");
        _miExit.Text = App.Loc.T("tray.exit");
    }

    /// <summary>Draws a small island icon at runtime; no asset file is required.</summary>
    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var path = new GraphicsPath();
            var rect = new Rectangle(2, 9, 28, 14);
            const int r = 14;
            path.AddArc(rect.X, rect.Y, r, r, 90, 180);
            path.AddArc(rect.Right - r, rect.Y, r, r, 270, 180);
            path.CloseFigure();

            using var bg = new SolidBrush(Color.FromArgb(255, 20, 20, 24));
            g.FillPath(bg, path);

            using var dot = new SolidBrush(Color.FromArgb(255, 52, 199, 89));
            g.FillEllipse(dot, 21, 13, 6, 6);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
