using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using System.Threading.Channels;
using DextopCommon;

namespace DextopServer.Services;

public class RemoteDesktopService : IDisposable
{
    private readonly CancellationTokenSource cancellationTokenSource;
    private readonly Channel<(ScreenshotProtocol.FrameMetadata Metadata, PooledBuffer FrameData)> frameChannel;
    private readonly TcpListener server;
    private NetworkStream? stream;
    private TcpClient? client;
    private int jpegQuality;
    private bool disposed;

    public Channel<(ScreenshotProtocol.FrameMetadata Metadata, PooledBuffer FrameData)> FrameChannel => frameChannel;

    public RemoteDesktopService(int quality)
    {
        jpegQuality = quality;
        server = new TcpListener(IPAddress.Any, 4782);
        server.Start();
        cancellationTokenSource = new CancellationTokenSource();
        frameChannel = Channel.CreateBounded<(ScreenshotProtocol.FrameMetadata, PooledBuffer)>(new BoundedChannelOptions(3)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
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

            try
            {
                // Try to read with metadata first
                try
                {
                    var (metadata, frameData) = await ScreenshotProtocol.ReadFrameWithMetadataAsync(stream).ConfigureAwait(false);
                    await frameChannel.Writer.WriteAsync((metadata, frameData), cancellationTokenSource.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Fallback to old protocol if metadata parsing fails
                    PooledBuffer buffer = await ScreenshotProtocol.ReadBytesPooledAsync(stream).ConfigureAwait(false);
                    
                    // Create dummy metadata for backward compatibility
                    var dummyMetadata = new ScreenshotProtocol.FrameMetadata(
                        sequenceId: 0,
                        timestamp: DateTime.UtcNow.Ticks,
                        width: 0,
                        height: 0,
                        quality: (byte)jpegQuality
                    );
                    
                    await frameChannel.Writer.WriteAsync((dummyMetadata, buffer), cancellationTokenSource.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                HandleClientDisconnection();
                break;
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
                frameChannel.Writer.Complete();
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
