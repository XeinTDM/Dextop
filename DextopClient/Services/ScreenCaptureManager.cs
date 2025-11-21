using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.IO;
using System.Net.Sockets;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Reflection;
using DextopCommon;
using TurboJpegWrapper;

namespace DextopClient.Services;

public class ScreenCaptureManager : IDisposable
{
    private readonly MemoryStream memoryStream = new();
    private static ImageCodecInfo? jpegEncoder;
    private EncoderParameters? encoderParams;
    private Bitmap? persistentScreenshot;
    private Graphics? persistentGraphics;
    private Bitmap? scaledBitmap;
    private Graphics? scaledGraphics;
    private int currentQuality = 35;
    private Rectangle screenBounds;
    private int selectedMonitorIndex;
    private readonly object captureLock = new();
    private readonly MetricsCollector metricsCollector = new();
    private readonly AdaptiveBitrateController adaptiveController = new();
    private readonly SemaphoreSlim sendSemaphore = new(3, 3); // Limit concurrent sends
    private TJCompressor? tjCompressor;
    private readonly bool useTurboJpeg;
    private bool turboJpegAvailable;
    private DesktopDuplicator? desktopDuplicator;
    private int captureQueueDepth;
    private bool hasSentInitialFullFrame;
    private const int DirtyTileSize = 256;
    private const int MaxTilesPerFrame = 16;

    // Double-buffered capture pipeline
    private Bitmap? captureBuffer1;
    private Bitmap? captureBuffer2;
    private Graphics? captureGraphics1;
    private Graphics? captureGraphics2;
    private Bitmap? currentCaptureBuffer;
    private Graphics? currentCaptureGraphics;
    
    // Desktop context caching for efficient BitBlt
    private Graphics? desktopGraphics;
    private IntPtr cachedDesktopHdc = IntPtr.Zero;
    
    private struct CapturedFrame
    {
        public Bitmap Bitmap;
        public long CaptureTimeMs;
        public Rectangle[] DirtyRectangles;
        public bool OwnsBitmap;
    }

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
                                       IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddDllDirectory(string NewDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    public ScreenCaptureManager(bool useTurboJpeg = true, bool useDesktopDuplication = true)
    {
        this.useTurboJpeg = useTurboJpeg;
        SelectedMonitorIndex = 0;
        UpdateEncoderParams();
        InitializeTurboJpeg();
        if (useDesktopDuplication)
        {
            try
            {
                desktopDuplicator = new DesktopDuplicator();
                Console.WriteLine("Desktop Duplication capture initialized.");
            }
            catch (Exception ex)
            {
                desktopDuplicator = null;
                Console.WriteLine($"Desktop Duplication initialization failed, falling back to BitBlt: {ex.Message}");
            }
        }
    }

    private void InitializeTurboJpeg()
    {
        if (!useTurboJpeg)
        {
            turboJpegAvailable = false;
            Console.WriteLine("TurboJPEG disabled by configuration, using managed encoder");
            return;
        }

        try
        {
            SetupDllSearchPaths();
            VerifyTurboJpegAvailability();
            tjCompressor = new TJCompressor();
            turboJpegAvailable = true;
            Console.WriteLine("TurboJPEG encoder initialized successfully");
        }
        catch (Exception ex)
        {
            turboJpegAvailable = false;
            Console.WriteLine($"TurboJPEG initialization failed, falling back to managed encoder: {ex.Message}");
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

    public MetricsCollector MetricsCollector => metricsCollector;
    public AdaptiveBitrateController AdaptiveController => adaptiveController;

    public int SelectedMonitorIndex
    {
        get => selectedMonitorIndex;
        set
        {
            if (selectedMonitorIndex != value)
            {
                selectedMonitorIndex = value;
                UpdateScreenBounds();
            }
        }
    }

    private void UpdateScreenBounds()
    {
        lock (captureLock)
        {
            var screen = Screen.AllScreens[selectedMonitorIndex];
            screenBounds = screen.Bounds;
            persistentGraphics?.Dispose();
            persistentScreenshot?.Dispose();
            scaledGraphics?.Dispose();
            scaledBitmap?.Dispose();
            persistentScreenshot = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppArgb);
            persistentGraphics = Graphics.FromImage(persistentScreenshot);
            persistentGraphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            persistentGraphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            persistentGraphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
            persistentGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
        }
    }

    private void InitializeCaptureObjects()
    {
        lock (captureLock)
        {
            var screen = Screen.AllScreens[selectedMonitorIndex];
            screenBounds = screen.Bounds;
            persistentScreenshot = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppArgb);
            persistentGraphics = Graphics.FromImage(persistentScreenshot);
            persistentGraphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            persistentGraphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            persistentGraphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
            persistentGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
        }
    }

    private void CacheDesktopGraphics()
    {
        if (desktopGraphics == null)
        {
            desktopGraphics = Graphics.FromHwnd(IntPtr.Zero);
            cachedDesktopHdc = desktopGraphics.GetHdc();
        }
    }

    private void ReleaseCachedDesktopGraphics()
    {
        if (desktopGraphics != null)
        {
            desktopGraphics.ReleaseHdc(cachedDesktopHdc);
            desktopGraphics.Dispose();
            desktopGraphics = null;
            cachedDesktopHdc = IntPtr.Zero;
        }
    }

    private void CaptureIntoPersistentBitmap()
    {
        lock (captureLock)
        {
            if (persistentScreenshot is null || persistentGraphics is null)
            {
                InitializeCaptureObjects();
            }
            
            CacheDesktopGraphics();
            IntPtr hdcDest = persistentGraphics!.GetHdc();
            BitBlt(hdcDest, 0, 0, screenBounds.Width, screenBounds.Height, cachedDesktopHdc, screenBounds.X, screenBounds.Y, 0x00CC0020);
            persistentGraphics.ReleaseHdc(hdcDest);
        }
    }

    private void CaptureIntoCachedBuffer(Bitmap targetBuffer, Graphics targetGraphics)
    {
        lock (captureLock)
        {
            CacheDesktopGraphics();
            IntPtr hdcDest = targetGraphics.GetHdc();
            BitBlt(hdcDest, 0, 0, screenBounds.Width, screenBounds.Height, cachedDesktopHdc, screenBounds.X, screenBounds.Y, 0x00CC0020);
            targetGraphics.ReleaseHdc(hdcDest);
        }
    }

    private static ImageCodecInfo GetJpegEncoder() =>
        ImageCodecInfo.GetImageDecoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

    private void UpdateEncoderParams()
    {
        encoderParams?.Dispose();
        encoderParams = new EncoderParameters(1)
        {
            Param = { [0] = new EncoderParameter(Encoder.Quality, currentQuality) }
        };
    }

    public void UpdateQuality(int newQuality)
    {
        if (currentQuality != newQuality)
        {
            currentQuality = newQuality;
            UpdateEncoderParams();
        }
    }

    private Bitmap GetScaledBitmap(Bitmap source)
    {
        lock (captureLock)
        {
            var (scaledWidth, scaledHeight) = adaptiveController.GetScaledDimensions(
                source.Width, source.Height);

            // If no scaling needed, return original
            if (scaledWidth == source.Width && scaledHeight == source.Height)
                return source;

            if (scaledBitmap == null || scaledBitmap.Width != scaledWidth || scaledBitmap.Height != scaledHeight)
            {
                scaledGraphics?.Dispose();
                scaledBitmap?.Dispose();
                scaledBitmap = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format32bppArgb);
                scaledGraphics = Graphics.FromImage(scaledBitmap);
                scaledGraphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                scaledGraphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                scaledGraphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
                scaledGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            }

            scaledGraphics!.DrawImage(source, 0, 0, scaledWidth, scaledHeight);
            return scaledBitmap;
        }
    }

    private byte[] EncodeBitmapWithTurboJpeg(Bitmap bitmap, int quality)
    {
        BitmapData? bitmapData = null;
        try
        {
            // Lock the bitmap to access raw pixel data
            bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            // TurboJPEG expects BGRA format for 32bpp bitmaps
            byte[] compressedData = tjCompressor!.Compress(
                bitmapData.Scan0,
                bitmapData.Stride,
                bitmap.Width,
                bitmap.Height,
                TurboJpegWrapper.TJPixelFormats.TJPF_BGRA,
                TurboJpegWrapper.TJSubsamplingOptions.TJSAMP_420,
                quality,
                TurboJpegWrapper.TJFlags.BOTTOMUP);

            return compressedData;
        }
        finally
        {
            if (bitmapData != null)
            {
                bitmap.UnlockBits(bitmapData);
            }
        }
    }

    private byte[] EncodeRegion(Bitmap source, Rectangle region, int quality)
    {
        Rectangle clampedRegion = Rectangle.Intersect(new Rectangle(0, 0, source.Width, source.Height), region);
        if (clampedRegion.Width <= 0 || clampedRegion.Height <= 0)
        {
            return Array.Empty<byte>();
        }

        using Bitmap patchBitmap = source.Clone(clampedRegion, PixelFormat.Format32bppArgb);
        if (turboJpegAvailable && tjCompressor != null)
        {
            return EncodeBitmapWithTurboJpeg(patchBitmap, quality);
        }

        memoryStream.SetLength(0);
        jpegEncoder ??= GetJpegEncoder();
        patchBitmap.Save(memoryStream, jpegEncoder, encoderParams);
        byte[] buffer = new byte[(int)memoryStream.Length];
        Buffer.BlockCopy(memoryStream.GetBuffer(), 0, buffer, 0, buffer.Length);
        return buffer;
    }

    private List<Rectangle> BuildDirtyTiles(Rectangle[] dirtyRects, int originalWidth, int originalHeight, int scaledWidth, int scaledHeight)
    {
        var tiles = new List<Rectangle>();
        if (dirtyRects == null || dirtyRects.Length == 0)
        {
            return tiles;
        }

        double scaleX = originalWidth > 0 ? (double)scaledWidth / originalWidth : 1.0;
        double scaleY = originalHeight > 0 ? (double)scaledHeight / originalHeight : 1.0;
        var seenTiles = new HashSet<(int X, int Y)>();

        foreach (var rect in dirtyRects)
        {
            int left = Math.Clamp(rect.Left, 0, originalWidth);
            int top = Math.Clamp(rect.Top, 0, originalHeight);
            int right = Math.Clamp(rect.Right, 0, originalWidth);
            int bottom = Math.Clamp(rect.Bottom, 0, originalHeight);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            int scaledLeft = Math.Clamp((int)Math.Floor(left * scaleX), 0, scaledWidth);
            int scaledTop = Math.Clamp((int)Math.Floor(top * scaleY), 0, scaledHeight);
            int scaledRight = Math.Clamp((int)Math.Ceiling(right * scaleX), 0, scaledWidth);
            int scaledBottom = Math.Clamp((int)Math.Ceiling(bottom * scaleY), 0, scaledHeight);
            if (scaledRight <= scaledLeft || scaledBottom <= scaledTop)
            {
                continue;
            }

            int startX = scaledLeft / DirtyTileSize;
            int endX = (scaledRight + DirtyTileSize - 1) / DirtyTileSize;
            int startY = scaledTop / DirtyTileSize;
            int endY = (scaledBottom + DirtyTileSize - 1) / DirtyTileSize;

            for (int ty = startY; ty < endY; ty++)
            {
                for (int tx = startX; tx < endX; tx++)
                {
                    var key = (tx, ty);
                    if (!seenTiles.Add(key))
                    {
                        continue;
                    }

                    int tileX = tx * DirtyTileSize;
                    int tileY = ty * DirtyTileSize;
                    int tileRight = Math.Min(tileX + DirtyTileSize, scaledWidth);
                    int tileBottom = Math.Min(tileY + DirtyTileSize, scaledHeight);
                    int tileWidth = tileRight - tileX;
                    int tileHeight = tileBottom - tileY;
                    if (tileWidth <= 0 || tileHeight <= 0)
                    {
                        continue;
                    }

                    tiles.Add(new Rectangle(tileX, tileY, tileWidth, tileHeight));
                }
            }
        }

        tiles.Sort((a, b) =>
        {
            int yCompare = a.Y.CompareTo(b.Y);
            return yCompare != 0 ? yCompare : a.X.CompareTo(b.X);
        });

        return tiles;
    }

    private static byte[] BuildFrameHash(List<TilePayload> tilePayloads)
    {
        if (tilePayloads.Count == 0)
        {
            using var emptyMd5 = MD5.Create();
            return emptyMd5.ComputeHash(Array.Empty<byte>());
        }

        using var md5 = MD5.Create();
        byte[] headerBuffer = new byte[16];
        foreach (var payload in tilePayloads)
        {
            BinaryPrimitives.WriteInt32LittleEndian(headerBuffer.AsSpan(0, 4), payload.Region.X);
            BinaryPrimitives.WriteInt32LittleEndian(headerBuffer.AsSpan(4, 4), payload.Region.Y);
            BinaryPrimitives.WriteInt32LittleEndian(headerBuffer.AsSpan(8, 4), payload.Region.Width);
            BinaryPrimitives.WriteInt32LittleEndian(headerBuffer.AsSpan(12, 4), payload.Region.Height);
            md5.TransformBlock(headerBuffer, 0, headerBuffer.Length, null, 0);
            md5.TransformBlock(payload.Data, 0, payload.Data.Length, null, 0);
        }

        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return md5.Hash!;
    }

    private readonly struct TilePayload
    {
        public Rectangle Region { get; }
        public byte[] Data { get; }

        public TilePayload(Rectangle region, byte[] data)
        {
            Region = region;
            Data = data;
        }
    }

    public async Task SendScreenshotAsync(NetworkStream stream, Bitmap screenshot, int quality)
    {
        if (currentQuality != quality)
        {
            UpdateQuality(quality);
        }
        memoryStream.SetLength(0);
        jpegEncoder ??= GetJpegEncoder();
        screenshot.Save(memoryStream, jpegEncoder, encoderParams);
        ReadOnlyMemory<byte> frame = new ReadOnlyMemory<byte>(memoryStream.GetBuffer(), 0, (int)memoryStream.Length);
        await DextopCommon.ScreenshotProtocol.WriteBytesAsync(stream, frame).ConfigureAwait(false);
    }

public async Task StartStreamingAsync(NetworkStream stream, CancellationToken ct, int targetFps = 60)
{
    InitializeDoubleCaptureBuffers();
    captureQueueDepth = 0;
    hasSentInitialFullFrame = false;
    
    try
    {
            var frameChannel = Channel.CreateBounded<CapturedFrame>(new BoundedChannelOptions(3) 
            { 
                FullMode = BoundedChannelFullMode.DropOldest 
            });

            // Start capture thread
            var captureTask = Task.Run(() => CaptureLoopThread(frameChannel.Writer, ct, targetFps), ct);
            
            // Start encode/send thread
            var encodeTask = Task.Run(() => EncodeAndSendLoopThread(stream, frameChannel.Reader, ct), ct);

        await Task.WhenAny(captureTask, encodeTask).ConfigureAwait(false);
    }
    finally
    {
        ReleaseDoubleCaptureBuffers();
        ReleaseCachedDesktopGraphics();
        captureQueueDepth = 0;
    }
}

    private void InitializeDoubleCaptureBuffers()
    {
        lock (captureLock)
        {
            var screen = Screen.AllScreens[selectedMonitorIndex];
            screenBounds = screen.Bounds;

            // Initialize two alternating capture buffers
            captureBuffer1 = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppArgb);
            captureGraphics1 = Graphics.FromImage(captureBuffer1);
            captureGraphics1.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            captureGraphics1.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            captureGraphics1.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
            captureGraphics1.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;

            captureBuffer2 = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppArgb);
            captureGraphics2 = Graphics.FromImage(captureBuffer2);
            captureGraphics2.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            captureGraphics2.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            captureGraphics2.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
            captureGraphics2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;

            currentCaptureBuffer = captureBuffer1;
            currentCaptureGraphics = captureGraphics1;
        }
    }

    private void ReleaseDoubleCaptureBuffers()
    {
        lock (captureLock)
        {
            captureGraphics1?.Dispose();
            captureBuffer1?.Dispose();
            captureGraphics2?.Dispose();
            captureBuffer2?.Dispose();
            currentCaptureBuffer = null;
            currentCaptureGraphics = null;
        }
    }

    private void CaptureLoopThread(ChannelWriter<CapturedFrame> writer, CancellationToken ct, int targetFps)
    {
        try
        {
            if (desktopDuplicator != null)
            {
                RunDesktopDuplicationCaptureLoop(writer, ct, targetFps);
            }
            else
            {
                RunLegacyCaptureLoop(writer, ct, targetFps);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in capture loop: {ex.Message}");
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private void RunLegacyCaptureLoop(ChannelWriter<CapturedFrame> writer, CancellationToken ct, int targetFps)
    {
        int safeFps = Math.Max(1, targetFps);
        long ticksPerFrame = 10_000_000 / safeFps;
        long nextFrameTime = Stopwatch.GetTimestamp();

        CacheDesktopGraphics();

        while (!ct.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            long ticksUntilNextFrame = nextFrameTime - now;

            if (ticksUntilNextFrame > 0)
            {
                int msDelay = (int)(ticksUntilNextFrame / 10000);
                if (msDelay > 0)
                {
                    ct.WaitHandle.WaitOne(msDelay);
                }
            }

            Stopwatch captureStopwatch = Stopwatch.StartNew();
            Bitmap bufferToFill;
            Graphics graphicsToUse;

            lock (captureLock)
            {
                if (currentCaptureBuffer == captureBuffer1)
                {
                    bufferToFill = captureBuffer2!;
                    graphicsToUse = captureGraphics2!;
                    currentCaptureBuffer = captureBuffer2;
                    currentCaptureGraphics = captureGraphics2;
                }
                else
                {
                    bufferToFill = captureBuffer1!;
                    graphicsToUse = captureGraphics1!;
                    currentCaptureBuffer = captureBuffer1;
                    currentCaptureGraphics = captureGraphics1;
                }
            }

            CaptureIntoCachedBuffer(bufferToFill, graphicsToUse);
            captureStopwatch.Stop();
            metricsCollector.RecordCaptureTime(captureStopwatch.ElapsedMilliseconds);

            var frame = new CapturedFrame
            {
                Bitmap = bufferToFill,
                CaptureTimeMs = captureStopwatch.ElapsedMilliseconds,
                DirtyRectangles = new[] { new Rectangle(0, 0, bufferToFill.Width, bufferToFill.Height) },
                OwnsBitmap = false
            };

            if (writer.TryWrite(frame))
            {
                Interlocked.Increment(ref captureQueueDepth);
            }
            else
            {
                metricsCollector.RecordDroppedFrame();
            }

            nextFrameTime += ticksPerFrame;
        }
    }

    private void RunDesktopDuplicationCaptureLoop(ChannelWriter<CapturedFrame> writer, CancellationToken ct, int targetFps)
    {
        if (desktopDuplicator == null)
        {
            return;
        }

        int safeFps = Math.Max(1, targetFps);
        long ticksPerFrame = 10_000_000 / safeFps;
        long nextFrameTime = Stopwatch.GetTimestamp();
        int consecutiveFailures = 0;
        const int maxFailuresBeforeFallback = 60; // ~1 second at 60 FPS

        while (!ct.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            long ticksUntilNextFrame = nextFrameTime - now;

            if (ticksUntilNextFrame > 0)
            {
                int msDelay = (int)(ticksUntilNextFrame / 10000);
                if (msDelay > 0)
                {
                    ct.WaitHandle.WaitOne(msDelay);
                }
            }

            Stopwatch captureStopwatch = Stopwatch.StartNew();
            if (!desktopDuplicator.TryCaptureFrame(out Bitmap? capturedBitmap, out Rectangle[] dirtyRects))
            {
                consecutiveFailures++;
                if (consecutiveFailures >= maxFailuresBeforeFallback)
                {
                    Console.WriteLine("Desktop Duplication capture failing, falling back to legacy BitBlt.");
                    desktopDuplicator.Dispose();
                    desktopDuplicator = null;
                    RunLegacyCaptureLoop(writer, ct, targetFps);
                    return;
                }
                nextFrameTime += ticksPerFrame;
                continue;
            }
            consecutiveFailures = 0;

            captureStopwatch.Stop();
            metricsCollector.RecordCaptureTime(captureStopwatch.ElapsedMilliseconds);

            var frameBitmap = capturedBitmap!;
            var frame = new CapturedFrame
            {
                Bitmap = frameBitmap,
                CaptureTimeMs = captureStopwatch.ElapsedMilliseconds,
                DirtyRectangles = dirtyRects.Length > 0
                    ? dirtyRects
                    : new[] { new Rectangle(0, 0, desktopDuplicator.Width, desktopDuplicator.Height) },
                OwnsBitmap = true
            };

            if (!writer.TryWrite(frame))
            {
                metricsCollector.RecordDroppedFrame();
                frameBitmap.Dispose();
            }
            else
            {
                Interlocked.Increment(ref captureQueueDepth);
            }

            nextFrameTime += ticksPerFrame;
        }
    }

    private async Task EncodeAndSendLoopThread(NetworkStream stream, ChannelReader<CapturedFrame> reader, CancellationToken ct)
    {
        int frameCount = 0;
        Stopwatch fpsStopwatch = Stopwatch.StartNew();

        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct))
            {
                int queueDepthSnapshot = Math.Max(0, Interlocked.Decrement(ref captureQueueDepth));
                try
                {
                    Bitmap bitmapToSend = GetScaledBitmap(frame.Bitmap);
                    int scaledWidth = bitmapToSend.Width;
                    int scaledHeight = bitmapToSend.Height;
                    int actualQuality = adaptiveController.CurrentQuality;

                    if (currentQuality != actualQuality)
                    {
                        UpdateQuality(actualQuality);
                    }

                    List<Rectangle> tileRects;
                    if (desktopDuplicator != null)
                    {
                        tileRects = BuildDirtyTiles(frame.DirtyRectangles, frame.Bitmap.Width, frame.Bitmap.Height, scaledWidth, scaledHeight);
                    }
                    else
                    {
                        tileRects = new List<Rectangle> { new Rectangle(0, 0, scaledWidth, scaledHeight) };
                    }

                    if (!hasSentInitialFullFrame)
                    {
                        tileRects = new List<Rectangle> { new Rectangle(0, 0, scaledWidth, scaledHeight) };
                    }
                    else if (tileRects.Count == 0)
                    {
                        continue;
                    }
                    else if (tileRects.Count > MaxTilesPerFrame)
                    {
                        tileRects = new List<Rectangle> { new Rectangle(0, 0, scaledWidth, scaledHeight) };
                    }

                    bool sentFullFrameThisCycle = !hasSentInitialFullFrame || tileRects.Count == 1 && tileRects[0].Width == scaledWidth && tileRects[0].Height == scaledHeight;

                    Stopwatch encodeStopwatch = Stopwatch.StartNew();
                    List<TilePayload> tilePayloads = new List<TilePayload>(tileRects.Count);
                    foreach (var rect in tileRects)
                    {
                        tilePayloads.Add(new TilePayload(rect, EncodeRegion(bitmapToSend, rect, actualQuality)));
                    }
                    encodeStopwatch.Stop();
                    metricsCollector.RecordEncodeTime(encodeStopwatch.ElapsedMilliseconds);

                    byte[] frameHash = BuildFrameHash(tilePayloads);

                    if (adaptiveController.ShouldDropFrame(frameHash))
                    {
                        frameCount++;
                        continue;
                    }

                    long totalBytesSent = 0;
                    Stopwatch sendStopwatch = Stopwatch.StartNew();
                    foreach (var payload in tilePayloads)
                    {
                        var metadata = new ScreenshotProtocol.FrameMetadata(
                            sequenceId: adaptiveController.NextSequenceId,
                            timestamp: DateTime.UtcNow.Ticks,
                            baseWidth: (ushort)Math.Min(scaledWidth, ushort.MaxValue),
                            baseHeight: (ushort)Math.Min(scaledHeight, ushort.MaxValue),
                            regionX: payload.Region.X,
                            regionY: payload.Region.Y,
                            regionWidth: (ushort)Math.Min(payload.Region.Width, ushort.MaxValue),
                            regionHeight: (ushort)Math.Min(payload.Region.Height, ushort.MaxValue),
                            quality: (byte)actualQuality,
                            hash: frameHash
                        );

                        await ScreenshotProtocol.WriteFrameWithMetadataAsync(stream, metadata, payload.Data).ConfigureAwait(false);
                        totalBytesSent += payload.Data.Length;
                    }
                    sendStopwatch.Stop();

                    metricsCollector.RecordBytesSent(totalBytesSent);
                    metricsCollector.RecordAdaptiveQualityLevel(actualQuality);
                    metricsCollector.RecordQueueDepth(queueDepthSnapshot);
                    adaptiveController.RecordMetrics(
                        encodeTimeMs: encodeStopwatch.ElapsedMilliseconds,
                        sendTimeMs: sendStopwatch.ElapsedMilliseconds,
                        queueDepth: queueDepthSnapshot,
                        fps: metricsCollector.Metrics.ClientFps
                    );

                    metricsCollector.UpdateCpuAndMemory();

                    if (sentFullFrameThisCycle && !hasSentInitialFullFrame)
                    {
                        hasSentInitialFullFrame = true;
                    }

                    frameCount++;
                    if (fpsStopwatch.Elapsed.TotalSeconds >= 1.0)
                    {
                        double fps = frameCount / fpsStopwatch.Elapsed.TotalSeconds;
                        metricsCollector.RecordClientFps(fps);
                        frameCount = 0;
                        fpsStopwatch.Restart();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in encode/send loop: {ex.Message}");
                    break;
                }
                finally
                {
                    if (frame.OwnsBitmap)
                    {
                        frame.Bitmap.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in encode/send loop thread: {ex.Message}");
        }
    }

    public async Task CaptureAndSendScreenshot(NetworkStream stream)
    {
        // Check if we should limit concurrent sends
        if (!sendSemaphore.Wait(0))
        {
            // Queue is full, drop this frame
            metricsCollector.RecordDroppedFrame();
            return;
        }

        Bitmap? sourceBitmap = null;
        bool ownsCapturedBitmap = false;

        try
        {
            Stopwatch captureStopwatch = Stopwatch.StartNew();
            if (desktopDuplicator != null && desktopDuplicator.TryCaptureFrame(out var capturedBitmap, out _))
            {
                sourceBitmap = capturedBitmap ?? throw new InvalidOperationException("Desktop duplicator returned an empty bitmap.");
                ownsCapturedBitmap = true;
            }
            else
            {
                CaptureIntoPersistentBitmap();
                sourceBitmap = persistentScreenshot!;
            }
            captureStopwatch.Stop();
            metricsCollector.RecordCaptureTime(captureStopwatch.ElapsedMilliseconds);

            // Get scaled bitmap based on adaptive controller
            Bitmap bitmapToSend = GetScaledBitmap(sourceBitmap!);
            int actualQuality = adaptiveController.CurrentQuality;

            // Update encoder if quality changed
            if (currentQuality != actualQuality)
            {
                UpdateQuality(actualQuality);
            }

            byte[] frameData;
            Stopwatch encodeStopwatch = Stopwatch.StartNew();
            
            if (turboJpegAvailable && tjCompressor != null)
            {
                // Use TurboJPEG SIMD-accelerated encoding
                frameData = EncodeBitmapWithTurboJpeg(bitmapToSend, actualQuality);
            }
            else
            {
                // Fallback to managed encoder
                memoryStream.SetLength(0);
                jpegEncoder ??= GetJpegEncoder();
                bitmapToSend.Save(memoryStream, jpegEncoder, encoderParams);
                frameData = memoryStream.GetBuffer()[0..(int)memoryStream.Length];
            }
            
            encodeStopwatch.Stop();
            metricsCollector.RecordEncodeTime(encodeStopwatch.ElapsedMilliseconds);

            ReadOnlyMemory<byte> frame = new ReadOnlyMemory<byte>(frameData);
            
            // Compute hash for duplicate detection
            byte[] frameHash = ScreenshotProtocol.ComputeFrameHash(frame);
            
            // Check if we should drop this frame (duplicate)
            if (adaptiveController.ShouldDropFrame(frameHash))
            {
                return;
            }

            // Create frame metadata
            var metadata = new ScreenshotProtocol.FrameMetadata(
                sequenceId: adaptiveController.NextSequenceId,
                timestamp: DateTime.UtcNow.Ticks,
                baseWidth: (ushort)Math.Min(bitmapToSend.Width, ushort.MaxValue),
                baseHeight: (ushort)Math.Min(bitmapToSend.Height, ushort.MaxValue),
                regionX: 0,
                regionY: 0,
                regionWidth: (ushort)Math.Min(bitmapToSend.Width, ushort.MaxValue),
                regionHeight: (ushort)Math.Min(bitmapToSend.Height, ushort.MaxValue),
                quality: (byte)actualQuality,
                hash: frameHash
            );

            Stopwatch sendStopwatch = Stopwatch.StartNew();
            await ScreenshotProtocol.WriteFrameWithMetadataAsync(stream, metadata, frame).ConfigureAwait(false);
            sendStopwatch.Stop();

            // Record metrics
            metricsCollector.RecordBytesSent(frame.Length);
            metricsCollector.RecordAdaptiveQualityLevel(actualQuality);
            
            // Update adaptive controller with performance metrics
            int queueDepth = 3 - sendSemaphore.CurrentCount; // Approximate queue depth
            adaptiveController.RecordMetrics(
                encodeTimeMs: encodeStopwatch.ElapsedMilliseconds,
                sendTimeMs: sendStopwatch.ElapsedMilliseconds,
                queueDepth: queueDepth,
                fps: metricsCollector.Metrics.ClientFps
            );

            metricsCollector.UpdateCpuAndMemory();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error during capture or send: " + ex.Message);
        }
        finally
        {
            if (ownsCapturedBitmap)
            {
                sourceBitmap?.Dispose();
            }
            sendSemaphore.Release();
        }
    }

    public void Dispose()
    {
        ReleaseCachedDesktopGraphics();
        ReleaseDoubleCaptureBuffers();
        tjCompressor?.Dispose();
        encoderParams?.Dispose();
        persistentGraphics?.Dispose();
        persistentScreenshot?.Dispose();
        scaledGraphics?.Dispose();
        scaledBitmap?.Dispose();
        desktopDuplicator?.Dispose();
        memoryStream.Dispose();
        sendSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
