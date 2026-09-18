using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using RecordeRy.App.Hotkeys;
using RecordeRy.App.Tray;
using RecordeRy.Core;
using RecordeRy.Core.Replay;
using RecordeRy.Core.Settings;

namespace RecordeRy.App;

public partial class MainWindow : Window
{
    private static readonly int[] BufferDurationOptions = [1, 2, 3, 5, 10, 15, 20, 30];
    private static readonly int[] FpsOptions = [24, 25, 30, 60];

    private static readonly SolidColorBrush ActiveBrush =
        new(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));

    private static readonly SolidColorBrush ErrorBrush =
        new(System.Windows.Media.Color.FromRgb(0xE3, 0x3A, 0x3A));

    private ReplayBuffer? _replayBuffer;
    private ReplayExporter? _replayExporter;
    private GlobalHotkeyService? _hotkeyService;
    private TrayIconService? _trayIcon;

    private AppSettings _settings = new();
    private string _ffmpegPath = string.Empty;
    private string _bufferPath = string.Empty;
    private string _videoPath = string.Empty;

    private bool _isExiting;
    private bool _isApplyingSettings;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        InitializeTrayIcon();

        BufferDurationCombo.ItemsSource = BufferDurationOptions;
        FpsCombo.ItemsSource = FpsOptions;

        try
        {
            _settings = SettingsStore.Load();

            _ffmpegPath = FfmpegLocator.Resolve(
                AppContext.BaseDirectory);

            _bufferPath = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "RecordeRy",
                "Buffer");

            _videoPath = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.MyVideos),
                "RecordeRy");

            _replayBuffer = new ReplayBuffer(
                _ffmpegPath,
                _bufferPath);

            _replayExporter = new ReplayExporter(
                _ffmpegPath,
                _bufferPath);

            Console.WriteLine("[APP] ReplayBuffer başlatılıyor...");

            await _replayBuffer.StartAsync(
                durationMinutes: _settings.BufferDurationMinutes,
                fps: _settings.Fps);

            Title = "RecordeRy - Replay Buffer Active";

            UpdateStatusUi(active: true);

            BufferDurationCombo.SelectedItem = _settings.BufferDurationMinutes;
            FpsCombo.SelectedItem = _settings.Fps;

            _hotkeyService = new GlobalHotkeyService(this);

            var (modifiers, key) = ParseHotkey(_settings);

            var registered = _hotkeyService.Register(modifiers, key);

            if (!registered)
            {
                System.Windows.MessageBox.Show(
                    $"{DescribeHotkey(modifiers, key)} hotkey'i kaydedilemedi.",
                    "RecordeRy",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            UpdateHotkeyLabel();

            _hotkeyService.HotkeyPressed +=
                HotkeyService_HotkeyPressed;
        }
        catch (Exception ex)
        {
            UpdateStatusUi(active: false);

            System.Windows.MessageBox.Show(
                $"RecordeRy başlatılamadı:\n\n{ex.Message}",
                "RecordeRy",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static (ModifierKeys Modifiers, Key Key) ParseHotkey(
        AppSettings settings)
    {
        var modifiers =
            Enum.TryParse<ModifierKeys>(settings.HotkeyModifiers, out var m)
                ? m
                : ModifierKeys.Control | ModifierKeys.Shift;

        var key =
            Enum.TryParse<Key>(settings.HotkeyKey, out var k)
                ? k
                : Key.S;

        return (modifiers, key);
    }

    private static string DescribeHotkey(
        ModifierKeys modifiers,
        Key key)
    {
        var parts = new System.Collections.Generic.List<string>();

        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        parts.Add(key.ToString());

        return string.Join(" + ", parts);
    }

    private void UpdateHotkeyLabel()
    {
        if (_hotkeyService == null)
            return;

        HotkeyText.Text = DescribeHotkey(
            _hotkeyService.CurrentModifiers,
            _hotkeyService.CurrentKey);
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new TrayIconService();

        _trayIcon.ShowWindowRequested += (_, _) => ShowMainWindow();
        _trayIcon.SaveReplayRequested += async (_, _) => await SaveReplayAsync();
        _trayIcon.ExitRequested += async (_, _) => await ExitApplicationAsync();
    }

    private void UpdateStatusUi(bool active)
    {
        if (active)
        {
            StatusDot.Fill = ActiveBrush;
            StatusText.Text = "Replay Buffer Aktif";

            EncoderText.Text =
                $"Encoder: {_replayBuffer?.EncoderName ?? "—"}";

            ResolutionText.Text =
                $"Çözünürlük: {_replayBuffer?.Width}x{_replayBuffer?.Height}";

            BufferText.Text =
                $"Arabellek: {_settings.BufferDurationMinutes} dk · {_settings.Fps} FPS";
        }
        else
        {
            StatusDot.Fill = ErrorBrush;
            StatusText.Text = "Başlatılamadı";
        }
    }

    private async void ApplySettings_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_replayBuffer == null || _isApplyingSettings)
            return;

        var duration = BufferDurationCombo.SelectedItem is int d
            ? d
            : _settings.BufferDurationMinutes;

        var fps = FpsCombo.SelectedItem is int f
            ? f
            : _settings.Fps;

        if (duration == _settings.BufferDurationMinutes &&
            fps == _settings.Fps)
        {
            return;
        }

        _isApplyingSettings = true;
        ApplySettingsButton.IsEnabled = false;
        SettingsHintText.Text = "Arabellek yeniden başlatılıyor...";

        try
        {
            await _replayBuffer.StopAsync();

            await _replayBuffer.StartAsync(
                durationMinutes: duration,
                fps: fps);

            _settings.BufferDurationMinutes = duration;
            _settings.Fps = fps;
            SettingsStore.Save(_settings);

            UpdateStatusUi(active: true);

            SettingsHintText.Text = "Değişiklikler arabelleği yeniden başlatır.";
        }
        catch (Exception ex)
        {
            UpdateStatusUi(active: false);

            SettingsHintText.Text = "Değişiklikler arabelleği yeniden başlatır.";

            System.Windows.MessageBox.Show(
                $"Ayarlar uygulanamadı:\n\n{ex.Message}",
                "RecordeRy",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isApplyingSettings = false;
            ApplySettingsButton.IsEnabled = true;
        }
    }

    private void ChangeHotkeyButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        ChangeHotkeyButton.IsEnabled = false;
        HotkeyText.Text = "Tuş kombinasyonuna basın...";

        PreviewKeyDown += CaptureHotkey_PreviewKeyDown;
    }

    private void CaptureHotkey_PreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System
            ? e.SystemKey
            : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or
                    Key.LeftShift or Key.RightShift or
                    Key.LeftAlt or Key.RightAlt or
                    Key.LWin or Key.RWin or
                    Key.System)
        {
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;

        if (modifiers == ModifierKeys.None)
        {
            /*
             * Sadece harf/tuş ile global kısayol tanımlamak riskli
             * (her yerde tetiklenir); en az bir modifier tuşu
             * (Ctrl/Shift/Alt/Win) zorunlu tutuyoruz.
             */
            e.Handled = true;
            return;
        }

        PreviewKeyDown -= CaptureHotkey_PreviewKeyDown;
        ChangeHotkeyButton.IsEnabled = true;

        ApplyNewHotkey(modifiers, key);

        e.Handled = true;
    }

    private void ApplyNewHotkey(
        ModifierKeys modifiers,
        Key key)
    {
        var registered = _hotkeyService?.Register(modifiers, key) ?? false;

        if (!registered)
        {
            System.Windows.MessageBox.Show(
                $"{DescribeHotkey(modifiers, key)} kaydedilemedi " +
                "(başka bir uygulama tarafından kullanılıyor olabilir). " +
                "Önceki kısayol korundu.",
                "RecordeRy",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            UpdateHotkeyLabel();

            return;
        }

        _settings.HotkeyModifiers = modifiers.ToString();
        _settings.HotkeyKey = key.ToString();
        SettingsStore.Save(_settings);

        UpdateHotkeyLabel();
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
            return;

        _isExiting = true;

        _hotkeyService?.Dispose();

        if (_replayBuffer != null)
        {
            await _replayBuffer.StopAsync();
            _replayBuffer.CleanupAllSegments();
        }

        Close();
    }

    private async void HotkeyService_HotkeyPressed(
        object? sender,
        EventArgs e)
    {
        await SaveReplayAsync();
    }

    private async Task SaveReplayAsync()
    {
        if (_replayExporter == null)
            return;

        try
        {
            var output = await _replayExporter
                .SaveReplayAsync(_videoPath);

            _trayIcon?.ShowBalloon(
                "Replay kaydedildi",
                Path.GetFileName(output));
        }
        catch (Exception ex)
        {
            _trayIcon?.ShowBalloon(
                "Replay kaydedilemedi",
                ex.Message,
                TrayNotificationKind.Error);
        }
    }

    private async void SaveReplay_Click(
        object sender,
        RoutedEventArgs e)
    {
        await SaveReplayAsync();
    }

    private void MainWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (_isExiting)
            return;

        e.Cancel = true;
        Hide();

        _trayIcon?.ShowBalloon(
            "RecordeRy",
            "Uygulama system tray'de çalışmaya devam ediyor.");
    }

    private async void MainWindow_Closed(
        object? sender,
        EventArgs e)
    {
        _hotkeyService?.Dispose();

        if (_replayBuffer != null)
        {
            await _replayBuffer.StopAsync();
            _replayBuffer.CleanupAllSegments();
        }

        _trayIcon?.Dispose();
    }
}
