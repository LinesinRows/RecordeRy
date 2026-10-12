using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using Device = Vortice.Direct3D11.ID3D11Device;
using DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;

namespace RecordeRy.Core.Capture;

public sealed class DesktopDuplicationCapture : IDisposable
{
    private Device? _device;
    private DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;

    private byte[]? _frameBuffer;

    private int _width;
    private int _height;

    private bool _initialized;

    public int Width => _width;
    public int Height => _height;
    public bool IsInitialized => _initialized;

    private const int EnumCurrentSettings = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(
        string? deviceName,
        int modeNum,
        ref DevMode devMode);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    /// <summary>
    /// Ana monitörün güncel yenileme hızını (Hz) döndürür.
    /// Okunamazsa 60 döner.
    /// </summary>
    public static int GetPrimaryRefreshRate()
    {
        try
        {
            var mode = new DevMode
            {
                dmSize = (ushort)Marshal.SizeOf<DevMode>()
            };

            if (EnumDisplaySettingsW(null, EnumCurrentSettings, ref mode) &&
                mode.dmDisplayFrequency > 1)
            {
                return (int)mode.dmDisplayFrequency;
            }
        }
        catch
        {
        }

        return 60;
    }

    /// <summary>
    /// Windows zamanlayıcı çözünürlüğünü 1 ms'e çeker; yoksa
    /// Task.Delay ~15 ms adımlarla uyanır ve yüksek FPS'te frame atlar.
    /// </summary>
    public static void BeginHighResolutionTimer() => timeBeginPeriod(1);

    public static void EndHighResolutionTimer() => timeEndPeriod(1);

    public void Initialize(int outputIndex = 0)
    {
        if (_initialized)
            return;

        CreateDevice();

        using var dxgiDevice =
            _device!.QueryInterface<IDXGIDevice>();

        using var adapter =
            dxgiDevice.GetAdapter();

        IDXGIOutput output;

        var enumResult =
            adapter.EnumOutputs(
                (uint)outputIndex,
                out output);

        enumResult.CheckError();

        using (output)
        {
            using var output1 =
                output.QueryInterface<IDXGIOutput1>();

            var description =
                output.Description;

            _width =
                description.DesktopCoordinates.Right -
                description.DesktopCoordinates.Left;

            _height =
                description.DesktopCoordinates.Bottom -
                description.DesktopCoordinates.Top;

            _duplication =
                output1.DuplicateOutput(
                    _device);
        }

        var textureDescription =
            new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,

                MipLevels = 1,
                ArraySize = 1,

                Format =
                    Format.B8G8R8A8_UNorm,

                SampleDescription =
                    new SampleDescription(
                        1,
                        0),

                Usage =
                    ResourceUsage.Staging,

                BindFlags =
                    BindFlags.None,

                CPUAccessFlags =
                    CpuAccessFlags.Read,

                MiscFlags =
                    ResourceOptionFlags.None
            };

        _stagingTexture =
            _device.CreateTexture2D(
                textureDescription);

        _initialized = true;
    }

    private void CreateDevice()
    {
        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null,
            out _device,
            out _context);

        result.CheckError();

        if (_device == null || _context == null)
        {
            throw new InvalidOperationException(
                "Direct3D11 cihazı oluşturulamadı. " +
                "Ekran kartı sürücüsü desktop duplication'ı desteklemiyor olabilir.");
        }
    }

    public bool TryCaptureFrame(
        out byte[]? pixels,
        out int width,
        out int height,
        int timeoutMs = 0)
    {
        pixels = null;

        width = _width;
        height = _height;

        if (!_initialized ||
            _duplication == null ||
            _stagingTexture == null ||
            _context == null)
        {
            return false;
        }

        IDXGIResource? desktopResource = null;

        try
        {
            /*
             * Yeni desktop frame var mı diye anlık yokla.
             *
             * 0 ms timeout: burada bloklamıyoruz çünkü
             * çağıran taraf (ReplayBuffer.CaptureLoopAsync)
             * zaten kendi hassas frame pacing zamanlamasını
             * yönetiyor. Burada 100ms gibi bir bekleme
             * koyarsak, frame gelmediği anlarda pacing
             * döngüsü saniyede birden çok kez 100ms'e kadar
             * bloklanır, gerçek zamanın büyük kısmı hiç frame
             * üretmeden geçer ve çıktı videosu gerçek süreden
             * daha kısa/hızlı oynatılmış gibi görünür.
             */
            var result =
                _duplication.AcquireNextFrame(
                    (uint)Math.Max(0, timeoutMs),
                    out _,
                    out desktopResource);

            if (result ==
                Vortice.DXGI.ResultCode.WaitTimeout)
            {
                return false;
            }

            result.CheckError();

            using (desktopResource)
            {
                using var acquiredTexture =
                    desktopResource.QueryInterface<
                        ID3D11Texture2D>();

                _context.CopyResource(
                    _stagingTexture,
                    acquiredTexture);

                var mapped =
                    _context.Map(
                        _stagingTexture,
                        0,
                        MapMode.Read,
                        Vortice.Direct3D11.MapFlags.None);

                try
                {
                    var rowPitch =
                        mapped.RowPitch;

                    const int bytesPerPixel = 4;

                    var rowSize =
                        _width * bytesPerPixel;

                    /*
                     * Her frame için yeni (LOH'ta ~8 MB) dizi ayırmak
                     * sürekli Gen2 GC duraklamalarına yol açıyordu;
                     * tek bir buffer'ı yeniden kullanıyoruz. Çağıran
                     * taraf frame'i bir sonraki TryCaptureFrame
                     * çağrısından önce işlemeyi bitirmelidir.
                     */
                    var totalSize = rowSize * _height;

                    if (_frameBuffer == null ||
                        _frameBuffer.Length != totalSize)
                    {
                        _frameBuffer = new byte[totalSize];
                    }

                    var output = _frameBuffer;

                    if ((int)rowPitch == rowSize)
                    {
                        Marshal.Copy(
                            mapped.DataPointer,
                            output,
                            0,
                            totalSize);
                    }
                    else
                    {
                        for (var y = 0;
                             y < _height;
                             y++)
                        {
                            Marshal.Copy(
                                IntPtr.Add(
                                    mapped.DataPointer,
                                    y * (int)rowPitch),
                                output,
                                y * rowSize,
                                rowSize);
                        }
                    }

                    pixels = output;
                }
                finally
                {
                    _context.Unmap(
                        _stagingTexture,
                        0);
                }
            }

            return true;
        }
        catch (SharpGen.Runtime.SharpGenException ex)
            when (ex.ResultCode ==
                  Vortice.DXGI.ResultCode.WaitTimeout)
        {
            return false;
        }
        catch (SharpGen.Runtime.SharpGenException ex)
            when (ex.ResultCode ==
                  Vortice.DXGI.ResultCode.AccessLost ||
                  ex.ResultCode ==
                  Vortice.DXGI.ResultCode.AccessDenied)
        {
            Reinitialize();

            return false;
        }
        finally
        {
            if (desktopResource != null)
            {
                try
                {
                    _duplication.ReleaseFrame();
                }
                catch
                {
                }

                desktopResource.Dispose();
            }
        }
    }

    private void Reinitialize()
    {
        DisposeResources();

        _initialized = false;

        Initialize();
    }

    private void DisposeResources()
    {
        _stagingTexture?.Dispose();
        _stagingTexture = null;

        _duplication?.Dispose();
        _duplication = null;

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        DisposeResources();

        _initialized = false;
    }
}