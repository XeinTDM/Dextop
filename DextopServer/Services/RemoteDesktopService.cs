using System.Diagnostics;
using System.Net.Sockets;
using System.Net;

namespace DextopServer.Services;

public class RemoteDesktopService : IDisposable
{
    private readonly CancellationTokenSource cancellationTokenSource;
    private readonly Action<byte[]> onScreenshotReceived;
    private const int TargetFrameDelay = 33;
    private readonly TcpListener server;
    private NetworkStream? stream;
    private TcpClient? client;
    private int jpegQuality;
    private bool disposed;

    public RemoteDesktopService(Action<byte[]> onScreenshotReceived, int quality)
    {
        this.onScreenshotReceived = onScreenshotReceived;
        jpegQuality = quality;
        server = new TcpListener(IPAddress.Any, 4782);
        server.Start();
        cancellationTokenSource = new CancellationTokenSource();
        _ = AcceptClientAsync();
    }

    public void UpdateQuality(int quality) => jpegQuality = quality;

    private async Task AcceptClientAsync()
    {
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                var newClient = await server.AcceptTcpClientAsync().ConfigureAwait(false);
                newClient.NoDelay = true;
                if (client != null)
                {
                    newClient.Close();
                    continue;
                }
                client = newClient;
                stream = client.GetStream();
                _ = Task.Run(() => StartRequestingScreenshotsAsync(), cancellationTokenSource.Token);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                Console.WriteLine("Socket operation aborted, likely due to application closing.");
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error in AcceptClient: {ex.Message}");
            }
        }
    }

    private async Task StartRequestingScreenshotsAsync()
    {
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            if (client == null || !client.Connected || stream == null)
            {
                HandleClientDisconnection();
                break;
            }

            var frameStopwatch = Stopwatch.StartNew();
            try
            {
                byte[] imageData = await DextopCommon.ScreenshotProtocol.ReadBytesAsync(stream).ConfigureAwait(false);
                onScreenshotReceived?.Invoke(imageData);
            }
            catch
            {
                HandleClientDisconnection();
                break;
            }

            var delay = TargetFrameDelay - (int)frameStopwatch.ElapsedMilliseconds;
            if (delay > 0)
            {
                try
                {
                    await Task.Delay(delay, cancellationTokenSource.Token)
                              .ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void HandleClientDisconnection()
    {
        stream?.Close();
        client?.Close();
        client = null;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                cancellationTokenSource.Cancel();
                stream?.Dispose();
                client?.Dispose();
                server.Stop();
                cancellationTokenSource.Dispose();
            }
            disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}