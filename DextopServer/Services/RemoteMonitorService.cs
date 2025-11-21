using System.Net.Sockets;
using System.Net;
using System.Buffers;

namespace DextopServer.Services;

public class RemoteMonitorService : IDisposable
{
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly TcpListener listener;
    private NetworkStream? stream;
    private TcpClient? client;

    public RemoteMonitorService(int port = 4790)
    {
        listener = CreateDualModeListener(port);
        _ = AcceptClientAsync();
    }

    private static TcpListener CreateDualModeListener(int port)
    {
        var tcpListener = new TcpListener(IPAddress.IPv6Any, port);
        tcpListener.Server.DualMode = true;
        tcpListener.Start();
        return tcpListener;
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
                byte[] buffer = ArrayPool<byte>.Shared.Rent(4);
                try
                {
                    BitConverter.GetBytes(monitorIndex).CopyTo(buffer, 0);
                    await stream.WriteAsync(buffer.AsMemory(0, 4)).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
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
