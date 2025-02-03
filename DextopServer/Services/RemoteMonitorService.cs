using System.Net.Sockets;
using System.Net;

namespace DextopServer.Services;

public class RemoteMonitorService : IDisposable
{
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly TcpListener listener;
    private NetworkStream? stream;
    private TcpClient? client;

    public RemoteMonitorService(int port = 4790)
    {
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        _ = AcceptClientAsync();
    }

    private async Task AcceptClientAsync()
    {
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                TcpClient newClient = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                newClient.NoDelay = true;
                if (client is not null)
                {
                    newClient.Close();
                    continue;
                }
                client = newClient;
                stream = client.GetStream();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                Console.WriteLine("Monitor socket operation aborted.");
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error in RemoteMonitorService: {ex.Message}");
            }
        }
    }

    public async Task SendMonitorSelectionAsync(int monitorIndex)
    {
        if (stream is not null && client?.Connected == true)
        {
            try
            {
                byte[] buffer = BitConverter.GetBytes(monitorIndex);
                await stream.WriteAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
            }
            catch
            {
                stream?.Close();
                client?.Close();
                client = null;
            }
        }
    }

    public void Dispose()
    {
        cancellationTokenSource.Cancel();
        stream?.Dispose();
        client?.Dispose();
        listener.Stop();
        cancellationTokenSource.Dispose();
    }
}
