using System.Net.Sockets;
using DextopCommon;
using System.Net;

namespace DextopServer.Services;

public class RemoteKeyboardService : IDisposable
{
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly TcpListener listener;
    private NetworkStream? stream;
    private TcpClient? client;

    public RemoteKeyboardService(int port = 4784)
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
                Console.WriteLine("Keyboard socket operation aborted.");
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error in RemoteKeyboardService: {ex.Message}");
            }
        }
    }

    public async Task SendKeyboardEventAsync(KeyboardEventData data)
    {
        if (stream is not null && client?.Connected == true)
        {
            try
            {
                await KeyboardProtocol.WriteKeyboardEventAsync(stream, data).ConfigureAwait(false);
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
