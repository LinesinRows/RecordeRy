namespace RecordeRy.Core.Settings;

public sealed class AppSettings
{
    public int BufferDurationMinutes { get; set; } = 1;

    public int Fps { get; set; } = 60;

    public string HotkeyModifiers { get; set; } = "Control, Shift";

    public string HotkeyKey { get; set; } = "S";
}
