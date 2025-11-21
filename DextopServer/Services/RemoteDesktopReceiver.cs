using System.Windows.Media.Imaging;
using DextopServer.Configurations;
using System.Windows.Controls;
using DextopCommon;

namespace DextopServer.Services;

public class RemoteDesktopReceiver
{
    private readonly RemoteDesktopUIManager rdUIManager;
    private readonly RemoteDesktopManager rdManager;
    private readonly System.Windows.Controls.Image screenshotImageControl;
    private readonly TextBlock fpsTextControl;

    public RemoteDesktopReceiver(System.Windows.Controls.Image screenshotImageControl, TextBlock fpsTextControl, RemoteDesktopUIManager rdUIManager, AppConfiguration config)
    {
        this.screenshotImageControl = screenshotImageControl;
        this.fpsTextControl = fpsTextControl;
        this.rdUIManager = rdUIManager;
        rdManager = new RemoteDesktopManager(config, rdUIManager);
        rdManager.ScreenshotReceived += OnScreenshotReceived;
    }

    private void OnScreenshotReceived(BitmapSource image)
    {
        screenshotImageControl.Dispatcher.BeginInvoke(() =>
        {
            screenshotImageControl.Source = image;
            var fpsTextUpdate = rdUIManager.Update();
            if (fpsTextUpdate is not null)
            {
                fpsTextControl.Text = fpsTextUpdate;
            }
        });
    }

    public void UpdateQuality(int newQuality) => rdManager.UpdateQuality(newQuality);

    public MetricsCollector GetMetricsCollector() => rdUIManager.MetricsCollector;

    public void Dispose() => rdManager.Dispose();
}
