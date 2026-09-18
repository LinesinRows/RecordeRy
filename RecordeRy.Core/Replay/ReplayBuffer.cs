using System.Diagnostics;
using System.Security.Cryptography;
using RecordeRy.Core.Capture;
using RecordeRy.Core.Recording;

namespace RecordeRy.Core.Replay;

public sealed class ReplayBuffer : IAsyncDisposable
{
    private readonly string _ffmpegPath;
    private readonly string _bufferDirectory;

    private DesktopDuplicationCapture? _capture;

    private Process? _process;
    private CancellationTokenSource? _cts;

    private Task? _captureTask;
    private Task? _cleanupTask;

    private Stream? _ffmpegInput;

    private int _fps;
    private int _durationMinutes;

    public bool IsRunning =>
        _process is { HasExited: false };

    public string? EncoderName { get; private set; }

    public int Width => _capture?.Width ?? 0;

    public int Height => _capture?.Height ?? 0;

    public ReplayBuffer(
        string ffmpegPath,
        string bufferDirectory)
    {
        _ffmpegPath = ffmpegPath;
        _bufferDirectory = bufferDirectory;
    }

    public async Task StartAsync(
        int durationMinutes = 1,
        int fps = 60)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException(
                "Replay Buffer zaten çalışıyor.");
        }

        if (durationMinutes < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationMinutes));
        }

        if (fps is not (24 or 25 or 30 or 60))
        {
            throw new ArgumentOutOfRangeException(
                nameof(fps),
                "FPS 24, 25, 30 veya 60 olmalıdır.");
        }

        _durationMinutes = durationMinutes;
        _fps = fps;

        Directory.CreateDirectory(
            _bufferDirectory);

        CleanupAllSegments();

        _capture =
            new DesktopDuplicationCapture();

        _capture.Initialize();

        var width = _capture.Width;
        var height = _capture.Height;

        Debug.WriteLine(
            $"[ReplayBuffer] Capture başladı: {width}x{height}");

        Debug.WriteLine(
            $"[ReplayBuffer] Hedef FPS: {_fps}");

        var outputPattern =
            Path.Combine(
                _bufferDirectory,
                "segment_%06d.mp4");

        var encoder =
            await EncoderSelector.SelectAsync(_ffmpegPath);

        EncoderName = encoder.Name;

        Debug.WriteLine(
            $"[ReplayBuffer] Seçilen encoder: {encoder.Name}");

        Console.WriteLine(
            $"[ReplayBuffer] Seçilen encoder: {encoder.Name}");

        var arguments =
            $"-hide_banner " +
            $"-loglevel warning " +
            $"-f rawvideo " +
            $"-pixel_format bgra " +
            $"-video_size {width}x{height} " +
            $"-framerate {fps} " +
            $"-i pipe:0 " +
            $"-an " +
            $"-c:v {encoder.Name} " +
            $"{encoder.EncodeArgs} " +
            $"-pix_fmt yuv420p " +
            $"-force_key_frames \"expr:gte(t,n_forced*2)\" " +
            $"-f segment " +
            $"-segment_time 2 " +
            $"-reset_timestamps 1 " +
            $"\"{outputPattern}\"";

        var startInfo =
            new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,

                UseShellExecute = false,
                CreateNoWindow = true,

                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

        var process =
            new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Debug.WriteLine(
                    $"[ReplayBuffer] {e.Data}");
            }
        };

        if (!process.Start())
        {
            process.Dispose();

            _capture.Dispose();
            _capture = null;

            throw new InvalidOperationException(
                "FFmpeg başlatılamadı.");
        }

        _process = process;

        process.BeginErrorReadLine();

        _ffmpegInput =
            process.StandardInput.BaseStream;

        _cts =
            new CancellationTokenSource();

        _captureTask =
            CaptureLoopAsync(
                _cts.Token);

        _cleanupTask =
            CleanupLoopAsync(
                TimeSpan.FromMinutes(
                    durationMinutes),
                _cts.Token);

        await Task.CompletedTask;
    }


    private async Task CaptureLoopAsync(
    CancellationToken cancellationToken)
    {
        Console.WriteLine("[ReplayBuffer] CaptureLoopAsync BAŞLADI");
        
        if (_capture == null ||
            _ffmpegInput == null)
        {
            return;
        }

        var frameInterval =
            TimeSpan.FromSeconds(1.0 / _fps);

        var stopwatch =
            Stopwatch.StartNew();

        var nextFrameTime =
            stopwatch.Elapsed;

        long capturedFrames = 0;
        long writtenFrames = 0;
        long duplicateFrames = 0;
        long uniqueFrames = 0;
        long reusedFrames = 0;

        var lastHash = 0UL;
        byte[]? lastFrame = null;

        var statsTimer =
            Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_process == null ||
                    _process.HasExited)
                {
                    break;
                }

                var now =
                    stopwatch.Elapsed;

                if (now < nextFrameTime)
                {
                    var delay =
                        nextFrameTime - now;

                    if (delay >
                        TimeSpan.FromMilliseconds(1))
                    {
                        await Task.Delay(
                            delay,
                            cancellationToken);
                    }

                    continue;
                }

                var gotFrame =
                    _capture.TryCaptureFrame(
                        out var pixels,
                        out _,
                        out _);

                if (gotFrame && pixels != null)
                {
                    capturedFrames++;

                    /*
                     * Frame'in içeriğinden basit bir hash oluştur.
                     * Aynı görüntünün tekrar tekrar gelip gelmediğini
                     * anlamak için kullanıyoruz.
                     */
                    ulong hash = 14695981039346656037UL;

                    // Her byte'ı kontrol etmek yerine örnekleme yapıyoruz.
                    // Böylece CPU'yu gereksiz yere yormuyoruz.
                    const int sampleStep = 4096;

                    for (
                        var i = 0;
                        i < pixels.Length;
                        i += sampleStep)
                    {
                        hash ^= pixels[i];
                        hash *= 1099511628211UL;
                    }

                    if (capturedFrames > 1)
                    {
                        if (hash == lastHash)
                        {
                            duplicateFrames++;
                        }
                        else
                        {
                            uniqueFrames++;
                        }
                    }
                    else
                    {
                        uniqueFrames = 1;
                    }

                    lastHash = hash;
                    lastFrame = pixels;
                }

                if (lastFrame == null)
                {
                    /*
                     * Henüz hiç frame yakalanmadı; başlangıç
                     * gecikmesinin zaman çizelgesini kaydırmasını
                     * önlemek için saati şimdiye sabitliyoruz.
                     */
                    nextFrameTime = stopwatch.Elapsed;

                    await Task.Delay(
                        1,
                        cancellationToken);

                    continue;
                }

                /*
                 * Bu noktaya kadar geçen gerçek süre, tek bir
                 * frame aralığından fazla olabilir (ör. capture
                 * veya encode pipeline'ı bir anlık yavaşladıysa).
                 * FFmpeg'e "-framerate {fps}" ile CFR (sabit frame
                 * rate) varsayımıyla veri gönderdiğimiz için, her
                 * loop turunda sadece 1 frame yazıp zaman çizelgesini
                 * yalnızca 1 frameInterval ilerletirsek, gerçek geçen
                 * süre ile video süresi arasında kayma birikir ve
                 * video gerçek süreden kısa/hızlı oynatılmış gibi
                 * görünür. Bunun yerine, gerçek zamanda kaç frame
                 * "vadesi geldiyse" o kadarını (gerekirse aynı
                 * frame'i tekrar ederek) yazıp zaman çizelgesini
                 * tam olarak o kadar ilerletiyoruz.
                 */
                now = stopwatch.Elapsed;

                var due =
                    (int)((now - nextFrameTime) / frameInterval) + 1;

                var maxCatchUpFrames = _fps * 5;

                if (due > maxCatchUpFrames)
                {
                    due = maxCatchUpFrames;

                    Console.WriteLine(
                        $"[ReplayBuffer] Büyük gecikme, " +
                        $"{due} frame ile yetişiliyor ve " +
                        "zaman çizelgesi yeniden senkronize ediliyor.");
                }

                for (var i = 0; i < due; i++)
                {
                    await _ffmpegInput.WriteAsync(
                        lastFrame,
                        cancellationToken);

                    writtenFrames++;
                }

                reusedFrames +=
                    gotFrame
                        ? due - 1
                        : due;

                nextFrameTime += due * frameInterval;

                if (now - nextFrameTime > TimeSpan.FromSeconds(5))
                {
                    nextFrameTime = stopwatch.Elapsed;
                }

                /*
                 * Her 2 saniyede bir gerçek istatistikleri yaz.
                 */
                if (statsTimer.Elapsed >=
                    TimeSpan.FromSeconds(2))
                {
                    var elapsed =
                        stopwatch.Elapsed.TotalSeconds;

                    var captureFps =
                        elapsed > 0
                            ? capturedFrames / elapsed
                            : 0;

                    var uniquePercent =
                        capturedFrames > 0
                            ? (uniqueFrames * 100.0) /
                              capturedFrames
                            : 0;

                    Console.WriteLine(
                        $"Captured: {capturedFrames}");

                    Console.WriteLine(
                        $"Written: {writtenFrames}");

                    Console.WriteLine(
                        $"Reused (no change): {reusedFrames}");

                    Console.WriteLine(
                        $"Duplicates: {duplicateFrames}");

                    Console.WriteLine(
                        $"Unique: {uniqueFrames}");

                    Console.WriteLine(
                        $"Capture FPS: {captureFps:F2}");

                    Console.WriteLine(
                        $"Unique %: {uniquePercent:F1}%");

                    Console.WriteLine(
                        "------------------------------");

                    statsTimer.Restart();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[ReplayBuffer] Capture error: {ex}");

                await Task.Delay(
                    50,
                    cancellationToken);
            }
        }

        /*
         * Son istatistikleri mutlaka yaz.
         */
        var totalSeconds =
            stopwatch.Elapsed.TotalSeconds;

        var finalCaptureFps =
            totalSeconds > 0
                ? capturedFrames / totalSeconds
                : 0;

        var finalUniquePercent =
            capturedFrames > 0
                ? (uniqueFrames * 100.0) /
                  capturedFrames
                : 0;

        Console.WriteLine();
        Console.WriteLine(
            "========== FINAL CAPTURE STATS ==========");

        Console.WriteLine(
            $"Captured: {capturedFrames}");

        Console.WriteLine(
            $"Written: {writtenFrames}");

        Console.WriteLine(
            $"Reused (no change): {reusedFrames}");

        Console.WriteLine(
            $"Duplicates: {duplicateFrames}");

        Console.WriteLine(
            $"Unique: {uniqueFrames}");

        Console.WriteLine(
            $"Capture FPS: {finalCaptureFps:F2}");

        Console.WriteLine(
            $"Unique %: {finalUniquePercent:F1}%");

        Console.WriteLine(
            "==========================================");
    }


    private async Task CleanupLoopAsync(
        TimeSpan bufferDuration,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                CleanupOldSegments(
                    bufferDuration);

                await Task.Delay(
                    TimeSpan.FromSeconds(2),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[ReplayBuffer] Cleanup error: {ex}");
            }
        }
    }

    private void CleanupOldSegments(
        TimeSpan bufferDuration)
    {
        if (!Directory.Exists(
                _bufferDirectory))
        {
            return;
        }

        var cutoff =
            DateTime.UtcNow -
            bufferDuration;

        var files =
            Directory
                .GetFiles(
                    _bufferDirectory,
                    "segment_*.mp4")
                .OrderBy(
                    File.GetLastWriteTimeUtc)
                .ToList();

        foreach (var file in files)
        {
            try
            {
                var lastWrite =
                    File.GetLastWriteTimeUtc(file);

                if (lastWrite < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch
            {
            }
        }
    }

    public void CleanupAllSegments()
    {
        if (!Directory.Exists(
                _bufferDirectory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(
                     _bufferDirectory,
                     "segment_*.mp4"))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
            }
        }
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        _cts = null;

        if (cts != null)
        {
            try
            {
                await cts.CancelAsync();
            }
            catch
            {
            }
        }

        if (_captureTask != null)
        {
            try
            {
                await _captureTask;
            }
            catch
            {
            }

            _captureTask = null;
        }

        try
        {
            if (_ffmpegInput != null)
            {
                await _ffmpegInput.DisposeAsync();
            }
        }
        catch
        {
        }

        _ffmpegInput = null;

        if (_cleanupTask != null)
        {
            try
            {
                await _cleanupTask;
            }
            catch
            {
            }

            _cleanupTask = null;
        }

        cts?.Dispose();

        var process = _process;
        _process = null;

        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    var exited =
                        await Task.Run(
                            () => process.WaitForExit(3000));

                    if (!exited)
                    {
                        try
                        {
                            process.Kill(
                                entireProcessTree: true);
                        }
                        catch
                        {
                        }

                        try
                        {
                            await process.WaitForExitAsync();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        _capture?.Dispose();
        _capture = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}