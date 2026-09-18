using System.Drawing;
using System.Windows.Forms;

namespace RecordeRy.App.Tray;

public enum TrayNotificationKind
{
    Info,
    Warning,
    Error
}

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public event EventHandler? ShowWindowRequested;
    public event EventHandler? SaveReplayRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        var menu = new ContextMenuStrip();

        var showItem = menu.Items.Add("Pencereyi Göster");
        showItem.Click += (_, _) =>
            ShowWindowRequested?.Invoke(this, EventArgs.Empty);

        var saveItem = menu.Items.Add("Replay Kaydet  (Ctrl+Shift+S)");
        saveItem.Click += (_, _) =>
            SaveReplayRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = menu.Items.Add("Çıkış");
        exitItem.Click += (_, _) =>
            ExitRequested?.Invoke(this, EventArgs.Empty);

        _notifyIcon = new NotifyIcon
        {
            Icon = ExtractApplicationIcon(),
            Text = "RecordeRy",
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) =>
            ShowWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateTooltip(string text)
    {
        _notifyIcon.Text = text.Length > 63
            ? text[..63]
            : text;
    }

    public void ShowBalloon(
        string title,
        string text,
        TrayNotificationKind kind = TrayNotificationKind.Info)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;

        _notifyIcon.BalloonTipIcon = kind switch
        {
            TrayNotificationKind.Warning => ToolTipIcon.Warning,
            TrayNotificationKind.Error => ToolTipIcon.Error,
            _ => ToolTipIcon.Info
        };

        _notifyIcon.ShowBalloonTip(3000);
    }

    private static Icon ExtractApplicationIcon()
    {
        var exePath = Environment.ProcessPath;

        if (!string.IsNullOrEmpty(exePath))
        {
            var extracted = Icon.ExtractAssociatedIcon(exePath);

            if (extracted != null)
                return extracted;
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
