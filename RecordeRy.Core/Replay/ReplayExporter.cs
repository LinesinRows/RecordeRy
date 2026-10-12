using System.Diagnostics;

namespace RecordeRy.Core.Replay;

public sealed class ReplayExporter
{
    private readonly string _ffmpegPath;
    private readonly string _bufferDirectory;

    public ReplayExporter(
        string ffmpegPath,
        string bufferDirectory)
    {
        _ffmpegPath = ffmpegPath;
        _bufferDirectory = bufferDirectory;
    }

    public async Task<string> SaveReplayAsync(
        string outputDirectory)
    {
        var segments = new SegmentManager(
            _bufferDirectory).GetSegments();

        if (segments.Count == 0)
        {
            throw new InvalidOperationException(
                "Replay buffer'da henüz video yok.");
        }

        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(
            outputDirectory,
            $"replay_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

        var concatFile = Path.Combine(
            _bufferDirectory,
            "concat.txt");

        try
        {
            try
            {
                return await ConcatAsync(concatFile, outputPath, segments);
            }
            catch (InvalidOperationException) when (segments.Count > 1)
            {
                /*
                 * Son segment hâlâ yazılıyor; yeni açılmış ve henüz
                 * başlığı yazılmamışsa okunamaz. Onsuz tekrar dene.
                 */
                return await ConcatAsync(
                    concatFile,
                    outputPath,
                    segments.Take(segments.Count - 1).ToList());
            }
        }
        finally
        {
            try
            {
                if (File.Exists(concatFile))
                {
                    File.Delete(concatFile);
                }
            }
            catch
            {
                // Temizlik başarısız olsa da replay dosyasını etkilemesin.
            }
        }
    }

    private async Task<string> ConcatAsync(
        string concatFile,
        string outputPath,
        IReadOnlyList<string> segments)
    {
        {
            // Önceki başarısız denemeden kalmış yarım dosya varsa sil.
            if (File.Exists(outputPath))
                File.Delete(outputPath);

            await CreateConcatFileAsync(
                concatFile,
                segments);

            var arguments =
            $"-hide_banner " +
            $"-loglevel warning " +
            $"-f concat " +
            $"-safe 0 " +
            $"-i \"{concatFile}\" " +
            $"-c copy " +
            $"-movflags +faststart " +
            $"\"{outputPath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,

                UseShellExecute = false,
                CreateNoWindow = true,

                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new Process
            {
                StartInfo = startInfo
            };

            var errorOutput = new List<string>();

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    errorOutput.Add(e.Data);
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "FFmpeg başlatılamadı.");
            }

            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = string.Join(
                    Environment.NewLine,
                    errorOutput);

                throw new InvalidOperationException(
                    $"Replay oluşturulamadı.\n\n{error}");
            }

            if (!File.Exists(outputPath))
            {
                throw new InvalidOperationException(
                    "FFmpeg tamamlandı ancak çıktı dosyası bulunamadı.");
            }

            return outputPath;
        }
    }

    private static async Task CreateConcatFileAsync(
        string path,
        IReadOnlyList<string> segments)
    {
        await using var writer = new StreamWriter(path);

        foreach (var segment in segments)
        {
            var escapedPath = segment
                .Replace("\\", "/")
                .Replace("'", "'\\''");

            await writer.WriteLineAsync(
                $"file '{escapedPath}'");
        }
    }
}