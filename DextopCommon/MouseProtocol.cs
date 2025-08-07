using System.Net.Sockets;
using System.Buffers;

namespace DextopCommon;

public enum MouseEventType : byte
{
    Move = 0,
    LeftDown = 1,
    LeftUp = 2,
    RightDown = 3,
    RightUp = 4
}

public readonly record struct MouseEventData(MouseEventType EventType, int X, int Y);

public static class MouseProtocol
{
    public static async Task WriteMouseEventAsync(NetworkStream stream, MouseEventData data)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(9);
        try
        {
            buffer[0] = (byte)data.EventType;
            BitConverter.GetBytes(data.X).CopyTo(buffer, 1);
            BitConverter.GetBytes(data.Y).CopyTo(buffer, 5);
            await stream.WriteAsync(buffer.AsMemory(0, 9)).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task<MouseEventData> ReadMouseEventAsync(NetworkStream stream)
    {
        byte[] buffer = new byte[9];
        int totalRead = 0;
        while (totalRead < 9)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, 9 - totalRead)).ConfigureAwait(false);
            if (read == 0) throw new IOException("Disconnected");
            totalRead += read;
        }
        MouseEventType type = (MouseEventType)buffer[0];
        int x = BitConverter.ToInt32(buffer, 1);
        int y = BitConverter.ToInt32(buffer, 5);
        return new MouseEventData(type, x, y);
    }
}
