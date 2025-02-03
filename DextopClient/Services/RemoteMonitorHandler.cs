using System.Net.Sockets;

namespace DextopClient.Services;

public class RemoteMonitorHandler : IDisposable
{
    private readonly CancellationTokenSource cancellationTokenSource = new();

    public RemoteMonitorHandler(Action<int> onMonitorSelected, string serverAddress = "localhost", int port = 4790)
    {
        _ = ConnectAsync(onMonitorSelected, serverAddress, port);
    }

    private async Task ConnectAsync(Action<int> onMonitorSelected, string serverAddress, int port)
    {
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            TcpClient? client = null;
            NetworkStream? stream = null;
            try
            {
                client = new TcpClient(serverAddress, port) { NoDelay = true };
                stream = client.GetStream();
                while (client.Connected && !cancellationTokenSource.IsCancellationRequested)
                {
                    byte[] buffer = new byte[4];
                    int totalRead = 0;
                    while (totalRead < 4)
                    {
                        int read = await stream.ReadAsync(buffer.AsMemory(totalRead, 4 - totalRead), cancellationTokenSource.Token)
                                               .ConfigureAwait(false);
                        if (read == 0) throw new IOException("Disconnected");
                        totalRead += read;
                    }
                    int monitorIndex = BitConverter.ToInt32(buffer);
                    onMonitorSelected(monitorIndex);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RemoteMonitorHandler error: {ex.Message}. Retrying in 5 seconds...");
            }
            finally
            {
                stream?.Dispose();
                client?.Close();
            }
            await Task.Delay(5000, cancellationTokenSource.Token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        cancellationTokenSource.Cancel();
        cancellationTokenSource.Dispose();
    }
}
