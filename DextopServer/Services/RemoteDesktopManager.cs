using System.Windows.Media.Imaging;
using DextopServer.Configurations;
using System.IO;
using System.Diagnostics;
using DextopCommon;

namespace DextopServer.Services;

public class RemoteDesktopManager : IDisposable
{
    private readonly RemoteDesktopService rdService;
    private bool disposed;
    private readonly RemoteDesktopUIManager? rdUIManager;

    public event Action<BitmapSource>? ScreenshotReceived;

    public RemoteDesktopManager(AppConfiguration config, RemoteDesktopUIManager? rdUIManager = null)
    {
        this.rdUIManager = rdUIManager;
        rdService = new RemoteDesktopService(OnScreenshotReceived, config.JpegQuality);
    }

    private void OnScreenshotReceived(byte[] buffer)
    {
        Stopwatch decodeStopwatch = Stopwatch.StartNew();
        BitmapSource image = DecodeJpeg(buffer);
        decodeStopwatch.Stop();
        rdUIManager?.RecordDecodeTime(decodeStopwatch.ElapsedMilliseconds);
        rdUIManager?.RecordBytesReceived(buffer.Length);
        image.Freeze();
        ScreenshotReceived?.Invoke(image);
    }

    private static BitmapSource DecodeJpeg(byte[] buffer)
    {
        using var ms = new MemoryStream(buffer);
        var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    public void UpdateQuality(int newQuality) => rdService.UpdateQuality(newQuality);

    public void Dispose()
    {
        if (!disposed)
        {
            rdService.Dispose();
            disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
