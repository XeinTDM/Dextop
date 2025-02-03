using DextopServer.UI;
using System.Windows;

namespace DextopServer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OpenRemoteDesktop_Click(object sender, RoutedEventArgs e)
    {
        var remoteDesktopWindow = new RemoteDesktopWindow();
        remoteDesktopWindow.Show();
    }
}