using System.Windows.Media.Imaging;
using System.Windows.Media;
using DextopServer.Configurations;
using System.IO;
using System.Diagnostics;
using DextopCommon;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using System.Reflection;
using TurboJpegWrapper;
using System.Buffers;

namespace DextopServer.Services;

public class RemoteDesktopManager : IDisposable
{
    private readonly RemoteDesktopService rdService;
    private readonly CancellationTokenSource cancellationTokenSource;
    private bool disposed;
    private readonly RemoteDesktopUIManager? rdUIManager;
    private WriteableBitmap? writeableBitmap;
    private int bitmapWidth;
    private int bitmapHeight;
    private readonly object bitmapLock = new object();
    private TJDecompressor? tjDecompressor;
    private readonly bool useTurboJpeg;
    private bool turboJpegAvailable;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddDllDirectory(string NewDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    public WriteableBitmap? WriteableBitmap => writeableBitmap;

    public RemoteDesktopManager(AppConfiguration config, RemoteDesktopUIManager? rdUIManager = null)
    {
        this.rdUIManager = rdUIManager;
        this.useTurboJpeg = config.UseTurboJpeg;
        rdService = new RemoteDesktopService(config.JpegQuality);
        cancellationTokenSource = new CancellationTokenSource();
        InitializeTurboJpeg();
        _ = Task.Run(() => DecodeFramesAsync(), cancellationTokenSource.Token);
    }

    private void InitializeTurboJpeg()
    {
        if (!useTurboJpeg)
        {
            turboJpegAvailable = false;
            Console.WriteLine("TurboJPEG disabled by configuration, using managed decoder");
            return;
        }

        try
        {
            SetupDllSearchPaths();
            VerifyTurboJpegAvailability();
            tjDecompressor = new TJDecompressor();
            turboJpegAvailable = true;
            Console.WriteLine("TurboJPEG decoder initialized successfully");
        }
        catch (Exception ex)
        {
            turboJpegAvailable = false;
            Console.WriteLine($"TurboJPEG initialization failed, falling back to managed decoder: {ex.Message}");
        }
    }

    private void VerifyTurboJpegAvailability()
    {
        try
        {
            string? assemblyLocation = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(assemblyLocation))
            {
                Console.WriteLine("Warning: Could not determine assembly location for verification");
                return;
            }

            string assemblyDir = Path.GetDirectoryName(assemblyLocation)!;
            string platformDir = Environment.Is64BitProcess ? "x64" : "x86";
            string dllPath = Path.Combine(assemblyDir, platformDir);
            string turboJpegPath = Path.Combine(dllPath, "turbojpeg.dll");

            Console.WriteLine($"Process architecture: {(Environment.Is64BitProcess ? "x64" : "x86")}");
            Console.WriteLine($"Looking for TurboJPEG DLL at: {turboJpegPath}");
            
            if (File.Exists(turboJpegPath))
            {
                var fileInfo = new FileInfo(turboJpegPath);
                Console.WriteLine($"TurboJPEG DLL found: {fileInfo.Length} bytes, modified: {fileInfo.LastWriteTime}");
            }
            else
            {
                Console.WriteLine($"ERROR: TurboJPEG DLL not found at {turboJpegPath}");
                
                // List all files in the directory to help with debugging
                if (Directory.Exists(dllPath))
                {
                    var files = Directory.GetFiles(dllPath);
                    Console.WriteLine($"Files in {dllPath}:");
                    foreach (var file in files)
                    {
                        Console.WriteLine($"  - {Path.GetFileName(file)}");
                    }
                }
                else
                {
                    Console.WriteLine($"Directory {dllPath} does not exist");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during TurboJPEG verification: {ex.Message}");
        }
    }

    private void SetupDllSearchPaths()
    {
        try
        {
            string? assemblyLocation = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(assemblyLocation))
            {
                Console.WriteLine("Warning: Could not determine assembly location");
                return;
            }

            string assemblyDir = Path.GetDirectoryName(assemblyLocation)!;
            string platformDir = Environment.Is64BitProcess ? "x64" : "x86";
            string dllPath = Path.Combine(assemblyDir, platformDir);

            if (Directory.Exists(dllPath))
            {
                Console.WriteLine($"Adding DLL search path: {dllPath}");
                
                // Add the platform-specific directory to DLL search paths
                int error;
                if (!AddDllDirectory(dllPath))
                {
                    error = (int)Marshal.GetLastWin32Error();
                    Console.WriteLine($"Warning: AddDllDirectory failed with error code: {error}");
                    
                    // Fallback: try SetDllDirectory (older method)
                    if (!SetDllDirectory(dllPath))
                    {
                        error = (int)Marshal.GetLastWin32Error();
                        Console.WriteLine($"Warning: SetDllDirectory failed with error code: {error}");
                    }
                    else
                    {
                        Console.WriteLine("SetDllDirectory succeeded as fallback");
                    }
                }
                else
                {
                    Console.WriteLine("AddDllDirectory succeeded");
                }

                // Try to pre-load the DLL to verify it works
                string turboJpegPath = Path.Combine(dllPath, "turbojpeg.dll");
                if (File.Exists(turboJpegPath))
                {
                    IntPtr handle = LoadLibrary(turboJpegPath);
                    if (handle != IntPtr.Zero)
                    {
                        Console.WriteLine("TurboJPEG DLL pre-loaded successfully");
                    }
                    else
                    {
                        error = (int)Marshal.GetLastWin32Error();
                        Console.WriteLine($"Warning: Failed to pre-load turbojpeg.dll, error code: {error}");
                    }
                }
                else
                {
                    Console.WriteLine($"Warning: turbojpeg.dll not found at {turboJpegPath}");
                }
            }
            else
            {
                Console.WriteLine($"Warning: Platform-specific DLL directory not found: {dllPath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to setup DLL search paths: {ex.Message}");
        }
    }

    private async Task DecodeFramesAsync()
    {
        ChannelReader<(ScreenshotProtocol.FrameMetadata Metadata, PooledBuffer FrameData)> reader = rdService.FrameChannel.Reader;
        
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                var (metadata, buffer) = await reader.ReadAsync(cancellationTokenSource.Token).ConfigureAwait(false);
                
                try
                {
                    Stopwatch decodeStopwatch = Stopwatch.StartNew();
                    ProcessFrame(metadata, buffer);
                    decodeStopwatch.Stop();
                    rdUIManager?.RecordDecodeTime(decodeStopwatch.ElapsedMilliseconds);
                    rdUIManager?.RecordBytesReceived(buffer.Length);
                    
                    // Update UI with current quality and resolution
                    if (metadata.BaseWidth > 0 && metadata.BaseHeight > 0)
                    {
                        rdUIManager?.UpdateCurrentQuality(metadata.Quality);
                        rdUIManager?.UpdateCurrentResolution(metadata.BaseWidth, metadata.BaseHeight);
                    }
                }
                finally
                {
                    buffer.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error decoding frame: {ex.Message}");
            }
        }
    }

    private void ProcessFrame(ScreenshotProtocol.FrameMetadata metadata, PooledBuffer buffer)
    {
        BitmapSource patchBitmap = DecodeJpeg(buffer.Memory);
        int patchWidth = patchBitmap.PixelWidth;
        int patchHeight = patchBitmap.PixelHeight;
        int bitsPerPixel = patchBitmap.Format.BitsPerPixel;
        int patchStride = (patchWidth * bitsPerPixel + 7) / 8;
        int patchByteCount = patchStride * patchHeight;
        byte[] patchPixels = ArrayPool<byte>.Shared.Rent(patchByteCount);
        patchBitmap.CopyPixels(patchPixels, patchStride, 0);

        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                lock (bitmapLock)
                {
                    EnsureWriteableBitmap(metadata.BaseWidth, metadata.BaseHeight);
                    if (writeableBitmap == null || bitmapWidth <= 0 || bitmapHeight <= 0)
                    {
                        return;
                    }

                    int targetWidth = Math.Min(metadata.RegionWidth, patchWidth);
                    int targetHeight = Math.Min(metadata.RegionHeight, patchHeight);
                    if (targetWidth <= 0 || targetHeight <= 0)
                    {
                        return;
                    }

                    int regionX = Math.Clamp(metadata.RegionX, 0, Math.Max(0, bitmapWidth - targetWidth));
                    int regionY = Math.Clamp(metadata.RegionY, 0, Math.Max(0, bitmapHeight - targetHeight));
                    int baseStride = (bitmapWidth * bitsPerPixel + 7) / 8;
                    int bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
                    int bytesToCopy = Math.Min(patchStride, targetWidth * bytesPerPixel);
                    int rowsToCopy = Math.Min(patchHeight, targetHeight);
                    bool locked = false;

                    try
                    {
                        writeableBitmap.Lock();
                        locked = true;
                        long baseAddress = writeableBitmap.BackBuffer.ToInt64();
                        for (int row = 0; row < rowsToCopy; row++)
                        {
                            long rowAddress = baseAddress + ((regionY + row) * baseStride) + (regionX * bytesPerPixel);
                            IntPtr rowPtr = new IntPtr(rowAddress);
                            Marshal.Copy(patchPixels, row * patchStride, rowPtr, bytesToCopy);
                        }

                        writeableBitmap.AddDirtyRect(new System.Windows.Int32Rect(regionX, regionY, targetWidth, targetHeight));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error writing pixels: {ex.Message}");
                    }
                    finally
                    {
                        if (locked)
                        {
                            writeableBitmap.Unlock();
                        }
                    }
                }

                rdUIManager?.RecordFrame();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(patchPixels);
            }
        });

        if (metadata.BaseWidth > 0 && metadata.BaseHeight > 0)
        {
            rdUIManager?.UpdateCurrentQuality(metadata.Quality);
            rdUIManager?.UpdateCurrentResolution(metadata.BaseWidth, metadata.BaseHeight);
        }
    }

    private void EnsureWriteableBitmap(int baseWidth, int baseHeight)
    {
        if (baseWidth <= 0 || baseHeight <= 0)
            return;

        if (writeableBitmap == null || bitmapWidth != baseWidth || bitmapHeight != baseHeight)
        {
            bitmapWidth = baseWidth;
            bitmapHeight = baseHeight;
            writeableBitmap = new WriteableBitmap(
                bitmapWidth,
                bitmapHeight,
                96,
                96,
                PixelFormats.Bgr24,
                null);
        }
    }

    private BitmapSource DecodeJpeg(Memory<byte> buffer)
    {
        if (turboJpegAvailable && tjDecompressor != null)
        {
            try
            {
                // Decompress using TurboJPEG
                byte[] decompressedData = tjDecompressor.Decompress(
                    buffer.ToArray(),
                    TurboJpegWrapper.TJPixelFormats.TJPF_BGR,
                    TurboJpegWrapper.TJFlags.BOTTOMUP,
                    out int width,
                    out int height,
                    out int stride);

                // Create BitmapSource from decompressed data
                var bitmap = BitmapSource.Create(
                    width,
                    height,
                    96,
                    96,
                    PixelFormats.Bgr24,
                    null,
                    decompressedData,
                    stride);
                
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TurboJPEG decode error, falling back to managed decoder: {ex.Message}");
                turboJpegAvailable = false;
            }
        }

        // Fallback to managed decoder
        using var ms = new MemoryStream(buffer.ToArray());
        var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    public void UpdateQuality(int newQuality) => rdService.UpdateQuality(newQuality);

    public void Dispose()
    {
        if (!disposed)
        {
            cancellationTokenSource.Cancel();
            rdService.Dispose();
            cancellationTokenSource.Dispose();
            tjDecompressor?.Dispose();
            disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
