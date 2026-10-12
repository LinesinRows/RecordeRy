using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

using Device = Vortice.Direct3D11.ID3D11Device;
using DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;

namespace RecordeRy.Core.Capture;

/// <summary>
/// Windows.Graphics.Capture tabanlı ekran yakalama (OBS'in kullandığı API).
/// Desktop Duplication'a göre DWM'nin sunum zamanına daha düzgün bağlanır.
/// </summary>
public sealed class WgcCapture : IDisposable
{
    private static readonly Guid GraphicsCaptureItemGuid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private static readonly Guid DxgiInterfaceAccessGuid =
        new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    private readonly object _sync = new();
    private readonly object _gpuSync = new();
    private readonly ManualResetEventSlim _signal = new(false);

    private Device? _device;
    private DeviceContext? _context;
    private ID3D11Texture2D? _staging;
    private ID3D11Texture2D? _bgraCopy;
    private ID3D11Texture2D? _nv12;

    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private ID3D11VideoProcessorEnumerator? _vpEnumerator;
    private ID3D11VideoProcessor? _videoProcessor;
    private ID3D11VideoProcessorInputView? _vpInputView;
    private ID3D11VideoProcessorOutputView? _vpOutputView;

    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;

    private byte[]? _front;
    private byte[]? _back;
    private byte[]? _work;
    private bool _hasNew;

    private bool _initialized;

    // Teşhis sayaçları: gelen frame sayısı ve işleme süresi (ms toplamı).
    private long _arrived;
    private double _handlerMsTotal;

    public (long Arrived, double HandlerMsTotal) TakeStats()
    {
        lock (_sync)
        {
            var stats = (_arrived, _handlerMsTotal);
            _arrived = 0;
            _handlerMsTotal = 0;
            return stats;
        }
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsInitialized => _initialized;

    public static bool IsSupported =>
        GraphicsCaptureSession.IsSupported();

    public void Initialize(int outputIndex = 0)
    {
        if (_initialized)
            return;

        var result = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
            null,
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null,
            out _device,
            out _context);

        result.CheckError();

        IntPtr monitor;

        using (var dxgiDevice = _device!.QueryInterface<IDXGIDevice>())
        {
            using var adapter = dxgiDevice.GetAdapter();

            adapter.EnumOutputs((uint)outputIndex, out var output)
                .CheckError();

            using (output)
            {
                monitor = output.Description.Monitor;
            }

            Marshal.ThrowExceptionForHR(
                CreateDirect3D11DeviceFromDXGIDevice(
                    dxgiDevice.NativePointer,
                    out var winrtDevicePtr));

            try
            {
                _winrtDevice =
                    MarshalInterface<IDirect3DDevice>.FromAbi(
                        winrtDevicePtr);
            }
            finally
            {
                Marshal.Release(winrtDevicePtr);
            }
        }

        _item = CreateItemForMonitor(monitor);

        Width = _item.Size.Width;
        Height = _item.Size.Height;

        CreateNv12Converter();

        var size = new Windows.Graphics.SizeInt32
        {
            Width = Width,
            Height = Height
        };

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice!,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            3,
            size);

        _pool.FrameArrived += OnFrameArrived;

        _session = _pool.CreateCaptureSession(_item);

        try { _session.IsCursorCaptureEnabled = true; } catch { }
        try { _session.IsBorderRequired = false; } catch { }
        try
        {
            // Win11 24H2+: varsayılan güncelleme sınırını kaldır; aksi
            // halde yüksek FPS içerikte kareler birleştirilip kaybolur.
            _session.MinUpdateInterval = TimeSpan.FromMilliseconds(1);

            DiagnosticsLog.Write(
                $"WGC MinUpdateInterval={_session.MinUpdateInterval.TotalMilliseconds} ms");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write(
                $"WGC MinUpdateInterval ayarlanamadı: {ex.Message}");
        }

        _session.StartCapture();

        _initialized = true;
    }

    /*
     * BGRA → NV12 dönüşümü GPU'daki video işlemcisinde yapılır.
     * NV12 kare başına w*h*1.5 byte'tır (BGRA'nın 3/8'i); GPU'dan
     * okuma, pipe aktarımı ve FFmpeg tarafındaki kopyalar bu oranda
     * azalır. 1080p'de 8.3 MB → 3.1 MB.
     */
    private void CreateNv12Converter()
    {
        var device = _device!;

        _bgraCopy = device.CreateTexture2D(
            new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
            });

        _nv12 = device.CreateTexture2D(
            new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget
            });

        _staging = device.CreateTexture2D(
            new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read
            });

        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context!.QueryInterface<ID3D11VideoContext>();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)Width,
            InputHeight = (uint)Height,
            OutputWidth = (uint)Width,
            OutputHeight = (uint)Height,
            InputFrameRate = new Rational(60, 1),
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.PlaybackNormal
        };

        _vpEnumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        _videoProcessor = _videoDevice.CreateVideoProcessor(_vpEnumerator, 0);

        _vpInputView = _videoDevice.CreateVideoProcessorInputView(
            _bgraCopy,
            _vpEnumerator,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView()
            });

        _vpOutputView = _videoDevice.CreateVideoProcessorOutputView(
            _nv12,
            _vpEnumerator,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView()
            });

        // Giriş: tam aralık RGB. Çıkış: BT.709, sınırlı (16-235) aralık
        // — FFmpeg tarafında da bu şekilde etiketleniyor.
        _videoContext.VideoProcessorSetStreamColorSpace(
            _videoProcessor,
            0,
            new VideoProcessorColorSpace
            {
                RGB_Range = 0,
                YCbCr_Matrix = 1
            });

        _videoContext.VideoProcessorSetOutputColorSpace(
            _videoProcessor,
            new VideoProcessorColorSpace
            {
                RGB_Range = 0,
                YCbCr_Matrix = 1,
                Nominal_Range = 1
            });

        _videoContext.VideoProcessorSetStreamAutoProcessingMode(
            _videoProcessor, 0, false);
    }

    private static GraphicsCaptureItem CreateItemForMonitor(
        IntPtr monitor)
    {
        const string className =
            "Windows.Graphics.Capture.GraphicsCaptureItem";

        Marshal.ThrowExceptionForHR(
            WindowsCreateString(
                className,
                className.Length,
                out var hstring));

        IntPtr factory;

        try
        {
            var interopIid =
                typeof(IGraphicsCaptureItemInterop).GUID;

            Marshal.ThrowExceptionForHR(
                RoGetActivationFactory(
                    hstring,
                    ref interopIid,
                    out factory));
        }
        finally
        {
            WindowsDeleteString(hstring);
        }

        try
        {
            var interop =
                (IGraphicsCaptureItemInterop)
                Marshal.GetObjectForIUnknown(factory);

            var iid = GraphicsCaptureItemGuid;

            var itemPtr = interop.CreateForMonitor(monitor, ref iid);

            try
            {
                return MarshalInspectable<GraphicsCaptureItem>
                    .FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private void OnFrameArrived(
        Direct3D11CaptureFramePool sender,
        object args)
    {
        // GPU kaynakları Dispose ile aynı anda kullanılmasın.
        lock (_gpuSync)
        {
            if (!_initialized)
                return;

            ProcessFrame(sender);
        }
    }

    private void ProcessFrame(Direct3D11CaptureFramePool sender)
    {
        var handlerStart = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            using var frame = sender.TryGetNextFrame();

            if (frame == null ||
                _context == null ||
                _staging == null)
            {
                return;
            }

            var surfacePtr =
                MarshalInterface<IDirect3DSurface>
                    .FromManaged(frame.Surface);

            IntPtr accessPtr;

            try
            {
                var accessIid = DxgiInterfaceAccessGuid;

                Marshal.ThrowExceptionForHR(
                    Marshal.QueryInterface(
                        surfacePtr,
                        ref accessIid,
                        out accessPtr));
            }
            finally
            {
                Marshal.Release(surfacePtr);
            }

            IntPtr texturePtr;

            try
            {
                var access =
                    (IDirect3DDxgiInterfaceAccess)
                    Marshal.GetObjectForIUnknown(accessPtr);

                var textureIid =
                    typeof(ID3D11Texture2D).GUID;

                texturePtr = access.GetInterface(ref textureIid);
            }
            finally
            {
                Marshal.Release(accessPtr);
            }

            using var texture = new ID3D11Texture2D(texturePtr);

            var desc = texture.Description;

            if (desc.Width != (uint)Width ||
                desc.Height != (uint)Height)
            {
                // Çözünürlük değişti; bu frame'i atla.
                return;
            }

            // GPU: kopyala → video işlemcisiyle NV12'ye çevir → staging.
            _context.CopyResource(_bgraCopy!, texture);

            _videoContext!.VideoProcessorBlt(
                _videoProcessor!,
                _vpOutputView!,
                0,
                1,
                new[]
                {
                    new VideoProcessorStream
                    {
                        Enable = true,
                        InputSurface = _vpInputView!
                    }
                });

            _context.CopyResource(_staging, _nv12!);

            /*
             * Okuma kilit dışında, handler'a özel _work buffer'ına
             * yapılır; tüketici (kayıt döngüsü) bu sırada beklemez.
             * Bitince kilit altında _work ile _back yer değiştirir.
             */
            var mapped = _context.Map(
                _staging,
                0,
                MapMode.Read,
                Vortice.Direct3D11.MapFlags.None);

            try
            {
                var total = Width * Height * 3 / 2;

                if (_work == null || _work.Length != total)
                    _work = new byte[total];

                var pitch = (int)mapped.RowPitch;

                // NV12 staging: Y düzlemi (Height satır), hemen ardından
                // pitch * Height ofsetinde UV düzlemi (Height / 2 satır).
                if (pitch == Width)
                {
                    Marshal.Copy(mapped.DataPointer, _work, 0, total);
                }
                else
                {
                    var rows = Height * 3 / 2;

                    for (var y = 0; y < rows; y++)
                    {
                        Marshal.Copy(
                            IntPtr.Add(mapped.DataPointer, y * pitch),
                            _work,
                            y * Width,
                            Width);
                    }
                }
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }

            lock (_sync)
            {
                (_work, _back) = (_back, _work);

                _hasNew = true;
                _arrived++;
                _handlerMsTotal += System.Diagnostics.Stopwatch
                    .GetElapsedTime(handlerStart).TotalMilliseconds;
            }

            _signal.Set();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WgcCapture] Frame hatası: {ex.Message}");
        }
    }

    /// <summary>
    /// Yeni frame varsa döndürür. Dönen dizi bir sonraki çağrıya
    /// kadar geçerlidir. <paramref name="timeoutMs"/> kadar yeni
    /// frame beklenir.
    /// </summary>
    public bool TryCaptureFrame(
        out byte[]? pixels,
        out int width,
        out int height,
        int timeoutMs = 0)
    {
        pixels = null;
        width = Width;
        height = Height;

        if (!_initialized)
            return false;

        if (timeoutMs > 0 && !_hasNew)
            _signal.Wait(timeoutMs);

        lock (_sync)
        {
            if (!_hasNew)
                return false;

            (_front, _back) = (_back, _front);

            _hasNew = false;
            _signal.Reset();

            pixels = _front;
        }

        return pixels != null;
    }

    public void Dispose()
    {
        _initialized = false;

        if (_pool != null)
            _pool.FrameArrived -= OnFrameArrived;

        _session?.Dispose();
        _pool?.Dispose();

        lock (_gpuSync)
        {
            _vpInputView?.Dispose();
            _vpOutputView?.Dispose();
            _videoProcessor?.Dispose();
            _vpEnumerator?.Dispose();
            _videoContext?.Dispose();
            _videoDevice?.Dispose();
            _nv12?.Dispose();
            _bgraCopy?.Dispose();
            _staging?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
        }

        _vpInputView = null;
        _vpOutputView = null;
        _videoProcessor = null;
        _vpEnumerator = null;
        _videoContext = null;
        _videoDevice = null;
        _nv12 = null;
        _bgraCopy = null;
        _session = null;
        _pool = null;
        _item = null;
        _staging = null;
        _context = null;
        _device = null;

        _signal.Dispose();
    }
}
