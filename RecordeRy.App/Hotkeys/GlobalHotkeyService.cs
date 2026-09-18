using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace RecordeRy.App.Hotkeys;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    private const int HOTKEY_ID = 9001;

    private readonly HwndSource _source;

    private bool _registered;

    private ModifierKeys _registeredModifiers;
    private Key _registeredKey;

    public event EventHandler? HotkeyPressed;

    public ModifierKeys CurrentModifiers => _registeredModifiers;

    public Key CurrentKey => _registeredKey;

    public GlobalHotkeyService(Window window)
    {
        var helper = new WindowInteropHelper(window);

        if (helper.Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Window handle oluşturulamadı.");
        }

        _source = HwndSource.FromHwnd(helper.Handle)
            ?? throw new InvalidOperationException(
                "HwndSource oluşturulamadı.");

        _source.AddHook(WndProc);
    }

    public bool Register(
        ModifierKeys modifiers,
        Key key)
    {
        var hadPrevious = _registered;
        var previousModifiers = _registeredModifiers;
        var previousKey = _registeredKey;

        if (_registered)
        {
            Unregister();
        }

        if (TryRegisterNative(modifiers, key))
        {
            _registered = true;
            _registeredModifiers = modifiers;
            _registeredKey = key;

            return true;
        }

        /*
         * Yeni kısayol kaydedilemediyse (ör. başka bir uygulama
         * tarafından kullanılıyorsa), kullanıcıyı kısayolsuz
         * bırakmamak için önceki çalışan kısayolu geri yüklüyoruz.
         */
        if (hadPrevious &&
            TryRegisterNative(previousModifiers, previousKey))
        {
            _registered = true;
            _registeredModifiers = previousModifiers;
            _registeredKey = previousKey;
        }

        return false;
    }

    private bool TryRegisterNative(
        ModifierKeys modifiers,
        Key key)
    {
        var virtualKey =
            KeyInterop.VirtualKeyFromKey(key);

        uint modifierValue = 0;

        if (modifiers.HasFlag(
                ModifierKeys.Control))
        {
            modifierValue |= MOD_CONTROL;
        }

        if (modifiers.HasFlag(
                ModifierKeys.Shift))
        {
            modifierValue |= MOD_SHIFT;
        }

        if (modifiers.HasFlag(
                ModifierKeys.Alt))
        {
            modifierValue |= MOD_ALT;
        }

        if (modifiers.HasFlag(
                ModifierKeys.Windows))
        {
            modifierValue |= MOD_WIN;
        }

        return RegisterHotKey(
            _source.Handle,
            HOTKEY_ID,
            modifierValue,
            (uint)virtualKey);
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        UnregisterHotKey(
            _source.Handle,
            HOTKEY_ID);

        _registered = false;
    }

    private IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WM_HOTKEY &&
            wParam.ToInt32() == HOTKEY_ID)
        {
            HotkeyPressed?.Invoke(
                this,
                EventArgs.Empty);

            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();

        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(
        IntPtr hWnd,
        int id);
}