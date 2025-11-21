using DextopClient.Services;
using System.Diagnostics;
using System.Net.Sockets;

namespace DextopClient;

class Program
{
    // Enable TurboJPEG by default for SIMD-accelerated encoding
    // Set to false to use managed encoder if TurboJPEG is unavailable on older systems
    private static readonly ScreenCaptureManager screenCaptureManager = new(useTurboJpeg: true);
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
        _ = Task.Run(() => MetricsLoggingLoop());
        using RemoteMonitorHandler monitorHandler = new(monitorIndex => screenCaptureManager.SelectedMonitorIndex = monitorIndex);
        while (true)
        {
            TcpClient? client = null;
            NetworkStream? stream = null;
            CancellationTokenSource? cts = null;
            try
            {
                client = new TcpClient(ServerAddress, ServerPort) { NoDelay = true };
                stream = client.GetStream();
                cts = new CancellationTokenSource();
                
                // Use the new async capture pipeline targeting 60 FPS
                await screenCaptureManager.StartStreamingAsync(stream, cts.Token, targetFps: 60).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}. Retrying in 5 seconds...");
            }
            finally
            {
                cts?.Cancel();
                cts?.Dispose();
                stream?.Dispose();
                client?.Close();
            }
            await Task.Delay(5000).ConfigureAwait(false);
        }
    }

    static async Task MetricsLoggingLoop()
    {
        while (true)
        {
            await Task.Delay(2000).ConfigureAwait(false);
            var snapshot = screenCaptureManager.MetricsCollector.Metrics.GetSnapshot();
            if (snapshot.ClientFps > 0)
            {
                Console.WriteLine(snapshot);
            }
        }
    }
}
