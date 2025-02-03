namespace DextopServer.Services;

public class RemoteDesktopHandler(Action<byte[]> onScreenshotReceived, int quality) : IDisposable
{
    private readonly RemoteDesktopService server = new(onScreenshotReceived, quality);

    public void UpdateQuality(int quality)
    {
        server.UpdateQuality(quality);
    }

    public void Dispose() => server.Dispose();
}