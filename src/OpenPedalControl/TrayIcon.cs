using System;
using System.Reflection;
using Forms = System.Windows.Forms;

namespace OpenPedalControl;

/// <summary>Notification area icon, so the virtual output can keep running without a window.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _startWithWindows;

    public TrayIcon(Action open, Action exit, Func<bool> getStartWithWindows, Action<bool> setStartWithWindows)
    {
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Open OpenPedalControl", null, (_, _) => open());
        openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
        _startWithWindows = new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) => setStartWithWindows(!getStartWithWindows()));
        menu.Items.Add(openItem);
        menu.Items.Add(_startWithWindows);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => exit()));
        menu.Opening += (_, _) => _startWithWindows.Checked = getStartWithWindows();

        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location),
            Text = "OpenPedalControl",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) open();
        };
    }

    public void ShowMessage(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
