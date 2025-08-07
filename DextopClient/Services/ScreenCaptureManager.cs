using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.IO;
using System.Net.Sockets;

namespace DextopClient.Services;

public class ScreenCaptureManager
{
    private readonly MemoryStream memoryStream = new();
    private static ImageCodecInfo? jpegEncoder;
    private Bitmap? persistentScreenshot;
    private Graphics? persistentGraphics;
    private int currentQuality = 35;
    private Rectangle screenBounds;
    private int selectedMonitorIndex;
    private readonly object captureLock = new();

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
                                       IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    public ScreenCaptureManager()
    {
        SelectedMonitorIndex = 0;
    }

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

    public byte[] ConvertScreenshotToBytes(Bitmap screenshot, int quality)
    {
        memoryStream.SetLength(0);
        jpegEncoder ??= GetJpegEncoder();
        using var encoderParams = new EncoderParameters(1)
        {
            Param = { [0] = new EncoderParameter(Encoder.Quality, quality) }
        };
        screenshot.Save(memoryStream, jpegEncoder, encoderParams);
        return memoryStream.ToArray();
    }

    private static ImageCodecInfo GetJpegEncoder() =>
        ImageCodecInfo.GetImageDecoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

    public async Task SendScreenshotAsync(NetworkStream stream, Bitmap screenshot, int quality)
    {
        byte[] buffer = ConvertScreenshotToBytes(screenshot, quality);
        await DextopCommon.ScreenshotProtocol.WriteBytesAsync(stream, buffer).ConfigureAwait(false);
    }

    public void UpdateQuality(int newQuality) => currentQuality = newQuality;

    public async Task CaptureAndSendScreenshot(NetworkStream stream)
    {
        try
        {
            CaptureIntoPersistentBitmap();
            // Encode directly from the persistent bitmap to avoid per-frame clone
            memoryStream.SetLength(0);
            jpegEncoder ??= GetJpegEncoder();
            using var encoderParams = new EncoderParameters(1)
            {
                Param = { [0] = new EncoderParameter(Encoder.Quality, currentQuality) }
            };
            persistentScreenshot!.Save(memoryStream, jpegEncoder, encoderParams);
            ReadOnlyMemory<byte> frame = new ReadOnlyMemory<byte>(memoryStream.GetBuffer(), 0, (int)memoryStream.Length);
            await DextopCommon.ScreenshotProtocol.WriteBytesAsync(stream, frame).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error during capture or send: " + ex.Message);
        }
    }
}
