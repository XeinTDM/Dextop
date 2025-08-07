using System.Net.Sockets;

namespace DextopCommon;

public static class ScreenshotProtocol
{
    public static async Task WriteInt32Async(NetworkStream stream, int value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        await stream.WriteAsync(bytes.AsMemory()).ConfigureAwait(false);
    }

    public static async Task<int> ReadInt32Async(NetworkStream stream)
    {
        byte[] bytes = new byte[4];
        int totalRead = 0;
        while (totalRead < 4)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(totalRead, 4 - totalRead)).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Disconnected");
            }
            totalRead += read;
        }
        return BitConverter.ToInt32(bytes, 0);
    }

    public static async Task WriteBytesAsync(NetworkStream stream, byte[] data)
    {
        // Avoid allocating a new combined buffer; write header then payload
        await WriteInt32Async(stream, data.Length).ConfigureAwait(false);
        await stream.WriteAsync(data.AsMemory()).ConfigureAwait(false);
    }

    public static async Task WriteBytesAsync(NetworkStream stream, ReadOnlyMemory<byte> data)
    {
        await WriteInt32Async(stream, data.Length).ConfigureAwait(false);
        await stream.WriteAsync(data).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadBytesAsync(NetworkStream stream)
    {
        int length = await ReadInt32Async(stream).ConfigureAwait(false);
        byte[] data = new byte[length];
        int totalRead = 0;
        while (totalRead < length)
        {
            int read = await stream.ReadAsync(data.AsMemory(totalRead, length - totalRead)).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Disconnected");
            }
            totalRead += read;
        }
        return data;
    }
}