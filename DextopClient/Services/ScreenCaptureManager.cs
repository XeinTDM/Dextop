using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.IO;
using System.Net.Sockets;
using System.Diagnostics;
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

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
                                       IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    public ScreenCaptureManager(bool useTurboJpeg = true)
    {
        this.useTurboJpeg = useTurboJpeg;
        SelectedMonitorIndex = 0;
        UpdateEncoderParams();
        InitializeTurboJpeg();
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

    private void CaptureIntoPersistentBitmap()
    {
        lock (captureLock)
        {
            if (persistentScreenshot is null || persistentGraphics is null)
            {
                InitializeCaptureObjects();
            }
            using Graphics desktopGraphics = Graphics.FromHwnd(IntPtr.Zero);
            IntPtr hdcDesktop = desktopGraphics.GetHdc();
            IntPtr hdcDest = persistentGraphics!.GetHdc();
            BitBlt(hdcDest, 0, 0, screenBounds.Width, screenBounds.Height, hdcDesktop, screenBounds.X, screenBounds.Y, 0x00CC0020);
            persistentGraphics.ReleaseHdc(hdcDest);
            desktopGraphics.ReleaseHdc(hdcDesktop);
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

    private Bitmap GetScaledBitmap()
    {
        lock (captureLock)
        {
            if (persistentScreenshot == null)
                return persistentScreenshot!;

            var (scaledWidth, scaledHeight) = adaptiveController.GetScaledDimensions(
                persistentScreenshot.Width, persistentScreenshot.Height);

            // If no scaling needed, return original
            if (scaledWidth == persistentScreenshot.Width && scaledHeight == persistentScreenshot.Height)
                return persistentScreenshot;

            // Create or update scaled bitmap
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

            // Draw scaled version
            scaledGraphics!.DrawImage(persistentScreenshot, 0, 0, scaledWidth, scaledHeight);
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

    public async Task CaptureAndSendScreenshot(NetworkStream stream)
    {
        // Check if we should limit concurrent sends
        if (!sendSemaphore.Wait(0))
        {
            // Queue is full, drop this frame
            metricsCollector.RecordDroppedFrame();
            return;
        }

        try
        {
            Stopwatch captureStopwatch = Stopwatch.StartNew();
            CaptureIntoPersistentBitmap();
            captureStopwatch.Stop();
            metricsCollector.RecordCaptureTime(captureStopwatch.ElapsedMilliseconds);

            // Get scaled bitmap based on adaptive controller
            Bitmap bitmapToSend = GetScaledBitmap();
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
                width: (ushort)bitmapToSend.Width,
                height: (ushort)bitmapToSend.Height,
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
            sendSemaphore.Release();
        }
    }

    public void Dispose()
    {
        tjCompressor?.Dispose();
        encoderParams?.Dispose();
        persistentGraphics?.Dispose();
        persistentScreenshot?.Dispose();
        scaledGraphics?.Dispose();
        scaledBitmap?.Dispose();
        memoryStream.Dispose();
        sendSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
