using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RecordeRy.Core.Recording;

/// <summary>
/// WASAPI'den (sistem sesi = loopback, ya da mikrofon) ham PCM yakalar ve
/// bir named pipe üzerinden ffmpeg'e aktarır. Cihaz sessizken veri üretmez
/// (özellikle loopback); bu yüzden gerçek saate göre eksik kalan kısım
/// sessizlikle doldurulur, böylece ses/video senkronu kaymaz.
/// </summary>
public sealed class AudioPipeSource : IAsyncDisposable
{
    private readonly bool _loopback;
    private readonly string _pipeName;

    private WasapiCapture? _capture;
    private NamedPipeServerStream? _pipe;
    private Channel<byte[]>? _queue;
    private CancellationTokenSource? _cts;
    private Task? _connectTask;
    private Task? _writeTask;

    public AudioPipeSource(bool loopback)
    {
        _loopback = loopback;
        _pipeName = $"RecordeRy_{(loopback ? "sys" : "mic")}_{Guid.NewGuid():N}";
    }

    /// <summary>ffmpeg -i parametresine verilecek pipe yolu.</summary>
    public string PipePath => $@"\\.\pipe\{_pipeName}";

    /// <summary>ffmpeg'e verilecek ham girdi formatı (ör. "-f f32le -ar 48000 -ac 2").</summary>
    public string InputFormatArgs { get; private set; } = string.Empty;

    /// <summary>Cihazı açar, formatı belirler ve pipe'ı hazırlar (ffmpeg bağlanana kadar bekler).</summary>
    public void Prepare()
    {
        _capture = _loopback
            ? new WasapiLoopbackCapture()
            : new WasapiCapture();

        var format = _capture.WaveFormat;

        var isFloat =
            format.Encoding == WaveFormatEncoding.IeeeFloat ||
            (format is WaveFormatExtensible ext &&
             ext.ToStandardWaveFormat().Encoding == WaveFormatEncoding.IeeeFloat);

        var sampleFormat = isFloat && format.BitsPerSample == 32
            ? "f32le"
            : format.BitsPerSample switch
            {
                16 => "s16le",
                24 => "s24le",
                32 => "s32le",
                _ => throw new NotSupportedException(
                    $"Desteklenmeyen ses formatı: {format}")
            };

        InputFormatArgs =
            $"-f {sampleFormat} -ar {format.SampleRate} -ac {format.Channels}";

        _pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        _cts = new CancellationTokenSource();

        _queue = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });

        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0)
            {
                var copy = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
                _queue?.Writer.TryWrite(copy);
            }
        };
    }

    /// <summary>ffmpeg başlatıldıktan sonra çağrılır; bağlantı gelince yakalama başlar.</summary>
    public void BeginConnect()
    {
        var token = _cts!.Token;

        _connectTask = Task.Run(async () =>
        {
            try
            {
                await _pipe!.WaitForConnectionAsync(token);

                var format = _capture!.WaveFormat;
                var byteRate = (long)format.AverageBytesPerSecond;
                var blockAlign = format.BlockAlign;

                _writeTask = WriteLoopAsync(byteRate, blockAlign, token);

                _capture.StartRecording();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioPipeSource] {ex}");
            }
        });
    }

    private async Task WriteLoopAsync(
        long byteRate,
        int blockAlign,
        CancellationToken token)
    {
        var reader = _queue!.Reader;
        var clock = Stopwatch.StartNew();
        long written = 0;

        // Bu kadar geride kalırsak sessizlikle doldur (cihaz veri üretmiyor).
        var toleranceBytes = byteRate / 10;

        try
        {
            while (!token.IsCancellationRequested)
            {
                using var timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(token);

                timeout.CancelAfter(20);

                try
                {
                    while (await reader.WaitToReadAsync(timeout.Token))
                    {
                        while (reader.TryRead(out var chunk))
                        {
                            await _pipe!.WriteAsync(chunk, token);
                            written += chunk.Length;
                        }
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // 20 ms içinde veri gelmedi; aşağıda saat kontrolü yapılacak.
                }

                var expected = (long)(clock.Elapsed.TotalSeconds * byteRate);
                var missing = expected - written;

                if (missing > toleranceBytes)
                {
                    var size = (int)(missing / blockAlign * blockAlign);

                    if (size > 0)
                    {
                        // Sessizlik: tüm formatlarda sıfır byte (float dahil).
                        await _pipe!.WriteAsync(new byte[size], token);
                        written += size;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _capture?.StopRecording();
        }
        catch
        {
        }

        _capture?.Dispose();
        _capture = null;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        foreach (var task in new[] { _connectTask, _writeTask })
        {
            if (task == null)
                continue;

            try
            {
                await task;
            }
            catch
            {
            }
        }

        try
        {
            _pipe?.Dispose();
        }
        catch
        {
        }

        _pipe = null;
        _cts?.Dispose();
        _cts = null;
    }
}
