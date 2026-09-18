using System.Diagnostics;

namespace RecordeRy.Core.Recording;

/// <summary>
/// Ffmpeg'e verilen sistemde hangi donanım encoder'ının gerçekten
/// çalıştığını probe ederek bulur (GPU üreticisine bağlı NVENC/QSV/AMF),
/// hiçbiri çalışmıyorsa yazılım encoder'ına (libx264) düşer.
/// </summary>
public static class EncoderSelector
{
    private static readonly EncoderProfile[] Candidates =
    [
        new("h264_nvenc", "-preset p1 -cq 28"),
        new("h264_qsv", "-preset veryfast -global_quality 28"),
        new("h264_amf", "-quality speed -rc cqp -qp_i 28 -qp_p 28"),
    ];

    private static readonly EncoderProfile Fallback =
        new("libx264", "-preset ultrafast -crf 28");

    public static async Task<EncoderProfile> SelectAsync(
        string ffmpegPath,
        CancellationToken cancellationToken = default)
    {
        foreach (var candidate in Candidates)
        {
            if (await ProbeAsync(ffmpegPath, candidate, cancellationToken))
                return candidate;
        }

        return Fallback;
    }

    private static async Task<bool> ProbeAsync(
        string ffmpegPath,
        EncoderProfile candidate,
        CancellationToken cancellationToken)
    {
        var arguments =
            "-hide_banner -loglevel error -y " +
            "-f lavfi -i nullsrc=s=256x256:d=0.1 " +
            $"-c:v {candidate.Name} {candidate.EncodeArgs} " +
            "-frames:v 2 -f null -";

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,

            UseShellExecute = false,
            CreateNoWindow = true,

            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };

            if (!process.Start())
                return false;

            using var timeoutCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(5));

            using var linkedCts = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
