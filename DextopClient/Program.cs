using DextopClient.Services;
using System.Diagnostics;
using System.Net.Sockets;

namespace DextopClient;

class Program
{
    private static readonly ScreenCaptureManager screenCaptureManager = new();
    private const string ServerAddress = "localhost";
    private const int ServerPort = 4782;

    static async Task Main()
    {
        _ = Task.Run(() =>
        {
            using RemoteMouseHandler mouseHandler = new(() => screenCaptureManager.SelectedMonitorIndex);
            while (true)
                Task.Delay(1000).Wait();
        });
        _ = Task.Run(() =>
        {
            using RemoteKeyboardHandler keyboardHandler = new();
            while (true)
                Task.Delay(1000).Wait();
        });
        using RemoteMonitorHandler monitorHandler = new(monitorIndex => screenCaptureManager.SelectedMonitorIndex = monitorIndex);
        while (true)
        {
            TcpClient? client = null;
            NetworkStream? stream = null;
            try
            {
                client = new TcpClient(ServerAddress, ServerPort) { NoDelay = true };
                stream = client.GetStream();
                while (client.Connected)
                {
                    Stopwatch frameStopwatch = Stopwatch.StartNew();
                    await screenCaptureManager.CaptureAndSendScreenshot(stream).ConfigureAwait(false);
                    int elapsed = (int)frameStopwatch.ElapsedMilliseconds;
                    int delay = Math.Max(0, 33 - elapsed);
                    await Task.Delay(delay).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}. Retrying in 5 seconds...");
            }
            finally
            {
                stream?.Dispose();
                client?.Close();
            }
            await Task.Delay(5000).ConfigureAwait(false);
        }
    }
}
