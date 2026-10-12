namespace RecordeRy.Core;

/// <summary>
/// %LOCALAPPDATA%\RecordeRy\capture.log dosyasına teşhis satırları yazar.
/// WinExe'de Console çıktısı görünmediği için kayıt performansını
/// incelemek için kullanılır.
/// </summary>
public static class DiagnosticsLog
{
    private static readonly object Sync = new();

    public static string Path { get; } =
        System.IO.Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "RecordeRy",
            "capture.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                // Her 2 sn'de satır yazıldığı için dosyayı sınırlı tut.
                var info = new FileInfo(Path);

                if (info.Exists && info.Length > 2 * 1024 * 1024)
                    info.Delete();

                File.AppendAllText(
                    Path,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
