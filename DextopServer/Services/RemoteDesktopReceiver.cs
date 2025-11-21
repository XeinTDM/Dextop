using System.Windows.Media.Imaging;
using DextopServer.Configurations;
using System.Windows.Controls;
using DextopCommon;
using System.Windows.Threading;

namespace DextopServer.Services;

public class RemoteDesktopReceiver
{
    private readonly RemoteDesktopUIManager rdUIManager;
    private readonly RemoteDesktopManager rdManager;
    private readonly System.Windows.Controls.Image screenshotImageControl;
    private readonly TextBlock fpsTextControl;
    private readonly DispatcherTimer updateTimer;

    public RemoteDesktopReceiver(System.Windows.Controls.Image screenshotImageControl, TextBlock fpsTextControl, RemoteDesktopUIManager rdUIManager, AppConfiguration config)
    {
        this.screenshotImageControl = screenshotImageControl;
        this.fpsTextControl = fpsTextControl;
        this.rdUIManager = rdUIManager;
        rdManager = new RemoteDesktopManager(config, rdUIManager);
        
        updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        updateTimer.Tick += UpdateTimerTick;
        updateTimer.Start();
    }

    private void UpdateTimerTick(object? sender, EventArgs e)
    {
        if (screenshotImageControl.Source == null && rdManager.WriteableBitmap != null)
        {
            screenshotImageControl.Source = rdManager.WriteableBitmap;
        }
        
        var fpsTextUpdate = rdUIManager.Update();
        if (fpsTextUpdate is not null)
        {
            fpsTextControl.Text = fpsTextUpdate;
        }
    }

    public void UpdateQuality(int newQuality) => rdManager.UpdateQuality(newQuality);

    public MetricsCollector GetMetricsCollector() => rdUIManager.MetricsCollector;

    public void Dispose()
    {
        updateTimer.Stop();
        rdManager.Dispose();
    }
}
