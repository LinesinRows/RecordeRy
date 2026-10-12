using RecordeRy.Core.Recording;

namespace RecordeRy.Core;

/// <summary>
/// ffmpeg.exe'yi bulur. İki sürüm birlikte dağıtılır:
///  - ffmpeg/        : güncel FFmpeg (yeni sürücülerle en iyi sonuç)
///  - ffmpeg-legacy/ : FFmpeg 7.1.x (eski NVIDIA sürücüleri için)
/// Güncel FFmpeg'in NVENC'i yeni bir sürücü istiyor (ör. 9.x → 610+);
/// Maxwell/Pascal gibi kartlar bu sürücüyü alamadığından donanım encoder'ı
/// açılmaz ve CPU'ya düşülür. Bu yüzden hangi sürümün bu sistemde donanım
/// encoder'ını açabildiği denenerek seçilir.
/// </summary>
public static class FfmpegLocator
{
    private const int MaxAncestorLevels = 8;

    private const string CurrentFolder = "ffmpeg";
    private const string LegacyFolder = "ffmpeg-legacy";

    public static async Task<string> ResolveAsync(string baseDirectory)
    {
        var current = Find(baseDirectory, CurrentFolder);
        var legacy = Find(baseDirectory, LegacyFolder);

        if (current == null && legacy == null)
        {
            throw new FileNotFoundException(
                "ffmpeg.exe bulunamadı. Uygulamanın yanında " +
                $"'{CurrentFolder}' veya '{LegacyFolder}' klasörü olmalı.");
        }

        if (current == null)
            return legacy!;

        if (legacy == null)
            return current;

        // İki sürüm paralel denenir (sonuçlar önbelleğe alınır).
        var currentProbe = EncoderSelector.HasHardwareEncoderAsync(current);
        var legacyProbe = EncoderSelector.HasHardwareEncoderAsync(legacy);

        await Task.WhenAll(currentProbe, legacyProbe);

        if (currentProbe.Result)
        {
            DiagnosticsLog.Write($"FFmpeg: güncel sürüm seçildi ({current})");
            return current;
        }

        if (legacyProbe.Result)
        {
            DiagnosticsLog.Write(
                $"FFmpeg: güncel sürümde donanım encoder'ı açılamadı, " +
                $"legacy sürüm seçildi ({legacy})");
            return legacy;
        }

        // İkisinde de donanım encoder'ı yok; CPU encoder'ı için güncel sürüm.
        DiagnosticsLog.Write("FFmpeg: donanım encoder'ı yok, güncel sürüm (CPU)");
        return current;
    }

    private static string? Find(string baseDirectory, string folder)
    {
        var candidates = new List<string>
        {
            Path.Combine(baseDirectory, folder, "ffmpeg.exe")
        };

        var directory = new DirectoryInfo(baseDirectory);

        for (var i = 0; i < MaxAncestorLevels && directory != null; i++)
        {
            candidates.Add(
                Path.Combine(directory.FullName, "tools", folder, "ffmpeg.exe"));

            directory = directory.Parent;
        }

        return candidates
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .FirstOrDefault();
    }
}
