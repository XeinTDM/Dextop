using System.Net.Sockets;
using DextopCommon;
using System.Net;

namespace DextopServer.Services;

public class RemoteMouseService : IDisposable
{
    private readonly TcpListener listener;
    private NetworkStream? stream;
    private TcpClient? client;
    private readonly CancellationTokenSource cancellationTokenSource = new();

    public RemoteMouseService(int port = 4783)
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
                if (client != null)
                {
                    newClient.Close();
                    continue;
                }
                client = newClient;
                stream = client.GetStream();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                Console.WriteLine("Mouse socket operation aborted.");
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error in RemoteMouseService: {ex.Message}");
            }
        }
    }

    public async Task SendMouseEventAsync(MouseEventData data)
    {
        if (stream is not null && client?.Connected == true)
        {
            try
            {
                await MouseProtocol.WriteMouseEventAsync(stream, data).ConfigureAwait(false);
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
