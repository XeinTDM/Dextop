using System.Windows.Media.Imaging;
using System.Windows.Media;
using DextopServer.Configurations;
using System.IO;
using System.Diagnostics;
using DextopCommon;
using System.Threading.Channels;
using System.Runtime.InteropServices;

namespace DextopServer.Services;

public class RemoteDesktopManager : IDisposable
{
    private readonly RemoteDesktopService rdService;
    private readonly CancellationTokenSource cancellationTokenSource;
    private bool disposed;
    private readonly RemoteDesktopUIManager? rdUIManager;
    private WriteableBitmap? writeableBitmap;
    private int bitmapWidth;
    private int bitmapHeight;
    private byte[]? pixelBuffer;
    private readonly object bitmapLock = new object();

    public WriteableBitmap? WriteableBitmap => writeableBitmap;

    public RemoteDesktopManager(AppConfiguration config, RemoteDesktopUIManager? rdUIManager = null)
    {
        this.rdUIManager = rdUIManager;
        rdService = new RemoteDesktopService(config.JpegQuality);
        cancellationTokenSource = new CancellationTokenSource();
        _ = Task.Run(() => DecodeFramesAsync(), cancellationTokenSource.Token);
    }

    private async Task DecodeFramesAsync()
    {
        ChannelReader<PooledBuffer> reader = rdService.FrameChannel.Reader;
        
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                PooledBuffer buffer = await reader.ReadAsync(cancellationTokenSource.Token).ConfigureAwait(false);
                
                try
                {
                    Stopwatch decodeStopwatch = Stopwatch.StartNew();
                    ProcessFrame(buffer);
                    decodeStopwatch.Stop();
                    rdUIManager?.RecordDecodeTime(decodeStopwatch.ElapsedMilliseconds);
                    rdUIManager?.RecordBytesReceived(buffer.Length);
                }
                finally
                {
                    buffer.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error decoding frame: {ex.Message}");
            }
        }
    }

    private void ProcessFrame(PooledBuffer buffer)
    {
        BitmapSource decodedBitmap = DecodeJpeg(buffer.Memory);
        
        lock (bitmapLock)
        {
            int width = decodedBitmap.PixelWidth;
            int height = decodedBitmap.PixelHeight;
            int stride = (width * decodedBitmap.Format.BitsPerPixel + 7) / 8;
            int bufferSize = stride * height;
            
            if (writeableBitmap == null || bitmapWidth != width || bitmapHeight != height)
            {
                bitmapWidth = width;
                bitmapHeight = height;
                pixelBuffer = new byte[bufferSize];
                
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    writeableBitmap = new WriteableBitmap(
                        bitmapWidth,
                        bitmapHeight,
                        96,
                        96,
                        PixelFormats.Bgr24,
                        null);
                });
            }
            else if (pixelBuffer == null || pixelBuffer.Length < bufferSize)
            {
                pixelBuffer = new byte[bufferSize];
            }
            
            decodedBitmap.CopyPixels(pixelBuffer, stride, 0);
            
            byte[] pixelsToWrite = new byte[bufferSize];
            Buffer.BlockCopy(pixelBuffer, 0, pixelsToWrite, 0, bufferSize);
            
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    writeableBitmap?.Lock();
                    try
                    {
                        Marshal.Copy(pixelsToWrite, 0, writeableBitmap!.BackBuffer, pixelsToWrite.Length);
                        writeableBitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, bitmapWidth, bitmapHeight));
                    }
                    finally
                    {
                        writeableBitmap?.Unlock();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error writing pixels: {ex.Message}");
                }
            });
        }
    }

    private static BitmapSource DecodeJpeg(Memory<byte> buffer)
    {
        using var ms = new MemoryStream(buffer.ToArray());
        var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    public void UpdateQuality(int newQuality) => rdService.UpdateQuality(newQuality);

    public void Dispose()
    {
        if (!disposed)
        {
            cancellationTokenSource.Cancel();
            rdService.Dispose();
            cancellationTokenSource.Dispose();
            disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
