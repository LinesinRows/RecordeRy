using System.Diagnostics;

namespace RecordeRy.Core.Capture;

public static class DesktopDuplicationTest
{
    public static void Run()
    {
        using var capture =
            new DesktopDuplicationCapture();

        capture.Initialize();

        Console.WriteLine(
            $"Capture başladı: {capture.Width}x{capture.Height}");

        var stopwatch =
            Stopwatch.StartNew();

        var frames = 0;

        while (stopwatch.Elapsed <
               TimeSpan.FromSeconds(5))
        {
            if (capture.TryCaptureFrame(
                    out var pixels,
                    out var width,
                    out var height))
            {
                frames++;

                if (frames == 1)
                {
                    Console.WriteLine(
                        $"İlk frame alındı: {width}x{height}, " +
                        $"{pixels!.Length:N0} byte");
                }
            }
        }

        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine(
            $"Toplam frame: {frames}");

        Console.WriteLine(
            $"Gerçek FPS: " +
            $"{frames / stopwatch.Elapsed.TotalSeconds:F2}");
    }
}