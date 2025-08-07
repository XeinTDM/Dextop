using System.Windows.Media.Imaging;
using DextopServer.Configurations;
using System.IO;

namespace DextopServer.Services;

public class RemoteDesktopManager : IDisposable
{
    private readonly RemoteDesktopService rdService;
    private bool disposed;

    public event Action<BitmapSource>? ScreenshotReceived;

    public RemoteDesktopManager(AppConfiguration config)
    {
        rdService = new RemoteDesktopService(OnScreenshotReceived, config.JpegQuality);
    }

    private void OnScreenshotReceived(byte[] buffer)
    {
        Task.Run(() =>
        {
            BitmapSource image = DecodeJpeg(buffer);
            image.Freeze();
            ScreenshotReceived?.Invoke(image);
        });
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