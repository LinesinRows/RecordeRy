using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using System.Security.Cryptography;
using RecordeRy.Core.Capture;
using RecordeRy.Core.Recording;

namespace RecordeRy.Core.Replay;

public sealed class ReplayBuffer : IAsyncDisposable
{
    private readonly string _ffmpegPath;
    private readonly string _bufferDirectory;

    private WgcCapture? _capture;

    private Process? _process;
    private CancellationTokenSource? _cts;

    private Task? _captureTask;
    private Task? _cleanupTask;

    private Stream? _ffmpegInput;

    private readonly List<AudioPipeSource> _audioSources = new();

    private int _fps;
    private bool _timerRaised;
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
        int fps = 60,
        bool captureSystemAudio = true,
        bool captureMicrophone = true)
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

        if (fps is < 1 or > 360)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fps),
                "FPS 1 ile 360 arasında olmalıdır.");
        }

        _durationMinutes = durationMinutes;
        _fps = fps;

        Directory.CreateDirectory(
            _bufferDirectory);

        CleanupAllSegments();

        _capture =
            new WgcCapture();

        _capture.Initialize();

        DesktopDuplicationCapture.BeginHighResolutionTimer();
        _timerRaised = true;

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

        PrepareAudioSources(
            captureSystemAudio,
            captureMicrophone);

        var audioInputs = string.Concat(
            _audioSources.Select(a =>
                $"-thread_queue_size 1024 " +
                $"-probesize 32 -analyzeduration 0 {a.InputFormatArgs} " +
                $"-i \"{a.PipePath}\" "));

        var audioArgs = string.Empty;

        if (_audioSources.Count > 0)
        {
            var chains = string.Join(
                ";",
                _audioSources.Select((_, i) =>
                    $"[{i + 1}:a]aresample=48000," +
                    $"aformat=channel_layouts=stereo[a{i}]"));

            var mixed = _audioSources.Count > 1
                ? $";{string.Concat(_audioSources.Select((_, i) => $"[a{i}]"))}" +
                  $"amix=inputs={_audioSources.Count}:duration=longest:normalize=0[aout]"
                : string.Empty;

            var map = _audioSources.Count > 1 ? "[aout]" : "[a0]";

            audioArgs =
                $"-filter_complex \"{chains}{mixed}\" " +
                $"-map 0:v -map \"{map}\" " +
                $"-c:a aac -b:a 160k ";
        }
        else
        {
            audioArgs = "-an ";
        }

        /*
         * Ham video, StandardInput yerine büyük tamponlu bir named
         * pipe ile gönderilir. StandardInput'un ~4 KB'lık tamponu
         * yüzünden her 8 MB'lık 1080p kare binlerce küçük parçaya
         * bölünüyor ve 144 FPS'te aktarım tek başına darboğaz oluyordu.
         */
        var frameBytes = width * height * 3 / 2;

        var videoPipeName =
            $"RecordeRy_video_{Guid.NewGuid():N}";

        var videoPipePath = $@"\\.\pipe\{videoPipeName}";

        var videoPipe =
            new NamedPipeServerStream(
                videoPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                frameBytes * 2);

        var arguments =
            $"-hide_banner " +
            $"-nostdin " +
            $"-loglevel warning " +
            // Girdi formatları açıkça verildiği için FFmpeg'in
            // açılıştaki analizine gerek yok; bu analiz video
            // pipe'ını saniyelerce okumadan bekletiyordu.
            $"-probesize 32 " +
            $"-analyzeduration 0 " +
            $"-thread_queue_size 64 " +
            $"-f rawvideo " +
            // Kareler GPU'da NV12'ye çevrilmiş geliyor (WgcCapture);
            // tüm encoder'lar NV12'yi doğrudan kabul ediyor.
            $"-pixel_format nv12 " +
            $"-video_size {width}x{height} " +
            $"-framerate {fps} " +
            $"-i \"{videoPipePath}\" " +
            audioInputs +
            audioArgs +
            $"-c:v {encoder.Name} " +
            $"{encoder.EncodeArgs} " +
            // Renk bilgisi (BT.709, sınırlı aralık) yalnızca bitstream'e
            // yazılır. -color_range/-colorspace çıkış seçenekleri FFmpeg'e
            // CPU'da dönüşüm ekletip hızı ~420'den ~70 FPS'e düşürüyordu.
            $"-bsf:v h264_metadata=video_full_range_flag=0:" +
            $"colour_primaries=1:transfer_characteristics=1:" +
            $"matrix_coefficients=1 " +
            $"-force_key_frames \"expr:gte(t,n_forced*2)\" " +
            $"-f segment " +
            $"-segment_time 2 " +
            $"-reset_timestamps 1 " +
            // Parçalar fragmented MP4: dizin bilgisi (moov) başta, veri
            // ~0,25 sn'lik fragment'larla eklenir. Normal MP4'te moov dosya
            // kapanınca yazıldığından yazılmakta olan son parça okunamıyor,
            // replay'in son 0–2 saniyesi kayboluyordu.
            $"-segment_format_options " +
            $"movflags=+frag_keyframe+empty_moov+default_base_moof:" +
            $"frag_duration=250000 " +
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

                DiagnosticsLog.Write($"ffmpeg: {e.Data}");
            }
        };

        if (!process.Start())
        {
            process.Dispose();
            await videoPipe.DisposeAsync();

            await DisposeAudioSourcesAsync();

            _capture.Dispose();
            _capture = null;
            ReleaseTimer();

            throw new InvalidOperationException(
                "FFmpeg başlatılamadı.");
        }

        _process = process;

        ChildProcessTracker.Track(process);

        /*
         * İşlemci oyun tarafından tamamen doldurulduğunda (4 çekirdekte
         * %100) normal öncelikli ffmpeg geç zamanlanıyor, video kuyruğu
         * doluyor ve kayıt takılıyordu. ffmpeg az CPU kullandığı için
         * önceliğini yükseltmek oyunu belirgin etkilemez (OBS de böyle).
         */
        try
        {
            process.PriorityClass = ProcessPriorityClass.AboveNormal;

            // Yakalama geri çağrıları ve kayıt thread'leri için.
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"ffmpeg önceliği ayarlanamadı: {ex.Message}");
        }

        process.BeginErrorReadLine();

        foreach (var source in _audioSources)
        {
            source.BeginConnect();
        }

        using (var connectTimeout =
                   new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            try
            {
                await videoPipe.WaitForConnectionAsync(
                    connectTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                await videoPipe.DisposeAsync();

                await StopAsync();

                throw new InvalidOperationException(
                    "FFmpeg video girişine bağlanmadı.");
            }
        }

        _ffmpegInput = videoPipe;

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


    private void PrepareAudioSources(
        bool captureSystemAudio,
        bool captureMicrophone)
    {
        void TryAdd(bool loopback)
        {
            var source = new AudioPipeSource(loopback);

            try
            {
                source.Prepare();
                _audioSources.Add(source);
            }
            catch (Exception ex)
            {
                // Cihaz yoksa/açılamıyorsa video yine de kaydedilsin.
                Debug.WriteLine(
                    $"[ReplayBuffer] {(loopback ? "Sistem sesi" : "Mikrofon")} " +
                    $"kullanılamadı: {ex.Message}");

                _ = source.DisposeAsync();
            }
        }

        if (captureSystemAudio)
            TryAdd(loopback: true);

        if (captureMicrophone)
            TryAdd(loopback: false);
    }

    private async Task DisposeAudioSourcesAsync()
    {
        foreach (var source in _audioSources)
        {
            try
            {
                await source.DisposeAsync();
            }
            catch
            {
            }
        }

        _audioSources.Clear();
    }

    /*
     * Kayıt iki bağımsız parçadan oluşur:
     *
     *  - Zamanlayıcı (ayrı thread): tam 1/fps aralıklarla o anki son
     *    ekran görüntüsünü kopyalayıp kuyruğa koyar. Böylece her
     *    karenin içeriği kendi zamanında alınır.
     *
     *  - Yazıcı: kuyruğu FFmpeg'e boşaltır. FFmpeg video okumasını ses
     *    akışıyla senkron yürüttüğü için pipe yazmaları ara ara onlarca
     *    ms bloklanır. Eskiden bu bekleme zamanlamayı da durduruyor,
     *    kaçırılan kareler aynı görüntüyle art arda yazılıyordu (donma +
     *    sıçrama). Kuyruk bu dalgalanmayı emer.
     */
    private const int FrameQueueCapacity = 48;

    private long _writtenFrames;

    private async Task CaptureLoopAsync(
        CancellationToken cancellationToken)
    {
        if (_capture == null ||
            _ffmpegInput == null)
        {
            return;
        }

        var queue = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(FrameQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        var pool = new ConcurrentQueue<byte[]>();

        Interlocked.Exchange(ref _writtenFrames, 0);

        var writerTask = Task.Factory.StartNew(
            () =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                WriterLoop(queue, pool, cancellationToken);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var timingTask = Task.Factory.StartNew(
            () =>
            {
                // CPU doluyken kare zamanlaması kaymasın.
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                TimingLoop(queue, pool, cancellationToken);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            await timingTask;
        }
        catch
        {
        }

        queue.Writer.TryComplete();

        try
        {
            await writerTask;
        }
        catch
        {
        }
    }

    // Kendi (yüksek öncelikli) thread'inde senkron çalışır; thread
    // pool'da normal öncelikte kalınca CPU doluyken geç kalıyordu.
    private void WriterLoop(
        Channel<byte[]> queue,
        ConcurrentQueue<byte[]> pool,
        CancellationToken cancellationToken)
    {
        try
        {
            var reader = queue.Reader;

            while (reader
                   .WaitToReadAsync(cancellationToken)
                   .AsTask()
                   .GetAwaiter()
                   .GetResult())
            {
                while (reader.TryRead(out var frame))
                {
                    _ffmpegInput!.Write(frame);

                    Interlocked.Increment(ref _writtenFrames);

                    pool.Enqueue(frame);
                }
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or
                IOException or
                ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"Yazıcı hatası: {ex.Message}");
        }
        finally
        {
            // Zamanlayıcı kuyruk doluyken bekliyorsa serbest kalsın.
            queue.Writer.TryComplete();
        }
    }

    private void TimingLoop(
        Channel<byte[]> queue,
        ConcurrentQueue<byte[]> pool,
        CancellationToken cancellationToken)
    {
        var capture = _capture!;

        var frameInterval =
            TimeSpan.FromSeconds(1.0 / _fps);

        var stopwatch = Stopwatch.StartNew();
        var nextFrameTime = TimeSpan.Zero;
        var started = false;

        byte[]? latest = null;

        // Teşhis (2 sn'lik pencere).
        var statsTimer = Stopwatch.StartNew();
        long consumed = 0;
        long queuedFrames = 0;
        long lateTicks = 0;
        long queueFullStalls = 0;
        var maxDue = 0;
        long lastWritten = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_process == null ||
                    _process.HasExited)
                {
                    break;
                }

                var now = stopwatch.Elapsed;

                if (started && now < nextFrameTime)
                {
                    // Sıradaki kare zamanına kadar yeni ekran karesi bekle.
                    var waitMs = (int)Math.Ceiling(
                        (nextFrameTime - now).TotalMilliseconds);

                    if (capture.TryCaptureFrame(
                            out var waited, out _, out _, waitMs) &&
                        waited != null)
                    {
                        latest = waited;
                        consumed++;
                    }

                    continue;
                }

                if (capture.TryCaptureFrame(
                        out var pixels, out _, out _) &&
                    pixels != null)
                {
                    latest = pixels;
                    consumed++;
                }

                if (latest == null)
                {
                    // Henüz hiç kare gelmedi.
                    Thread.Sleep(1);
                    continue;
                }

                if (!started)
                {
                    started = true;
                    nextFrameTime = stopwatch.Elapsed;
                }

                now = stopwatch.Elapsed;

                /*
                 * FFmpeg'e CFR (-framerate {fps}) veriyoruz; geçen gerçek
                 * süre kadar kare yazılmalı ki video ile ses aynı hızda
                 * aksın. Zamanlayıcı artık pipe'ta beklemediği için
                 * normalde due = 1'dir.
                 */
                var due =
                    (int)((now - nextFrameTime) / frameInterval) + 1;

                if (due > 1)
                    lateTicks++;

                var maxCatchUpFrames = _fps * 5;

                if (due > maxCatchUpFrames)
                {
                    due = maxCatchUpFrames;
                    nextFrameTime = now - frameInterval * (due - 1);
                }

                maxDue = Math.Max(maxDue, due);

                for (var i = 0; i < due; i++)
                {
                    if (!pool.TryDequeue(out var buffer) ||
                        buffer.Length != latest.Length)
                    {
                        buffer = new byte[latest.Length];
                    }

                    Buffer.BlockCopy(latest, 0, buffer, 0, latest.Length);

                    if (!queue.Writer.TryWrite(buffer))
                    {
                        queueFullStalls++;

                        queue.Writer
                            .WriteAsync(buffer, cancellationToken)
                            .AsTask()
                            .GetAwaiter()
                            .GetResult();
                    }

                    queuedFrames++;
                }

                nextFrameTime += frameInterval * due;

                if (statsTimer.Elapsed >= TimeSpan.FromSeconds(2))
                {
                    var window = statsTimer.Elapsed.TotalSeconds;

                    var (arrived, handlerMs) = capture.TakeStats();

                    var written = Interlocked.Read(ref _writtenFrames);

                    DiagnosticsLog.Write(
                        $"wgcArrived/s={arrived / window:F1} " +
                        $"wgcAvgMs={(arrived > 0 ? handlerMs / arrived : 0):F2} " +
                        $"capFps={consumed / window:F1} " +
                        $"queuedFps={queuedFrames / window:F1} " +
                        $"writeFps={(written - lastWritten) / window:F1} " +
                        $"queue={queue.Reader.Count} " +
                        $"late={lateTicks} maxDue={maxDue} " +
                        $"fullStalls={queueFullStalls}");

                    consumed = 0;
                    queuedFrames = 0;
                    lateTicks = 0;
                    queueFullStalls = 0;
                    maxDue = 0;
                    lastWritten = written;

                    statsTimer.Restart();
                }
            }
            catch (Exception ex) when (
                ex is OperationCanceledException or
                    ChannelClosedException or
                    ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Write($"Zamanlayıcı hatası: {ex.Message}");

                Thread.Sleep(50);
            }
        }
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

        await DisposeAudioSourcesAsync();

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
        ReleaseTimer();
    }

    private void ReleaseTimer()
    {
        if (!_timerRaised)
            return;

        DesktopDuplicationCapture.EndHighResolutionTimer();
        _timerRaised = false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
