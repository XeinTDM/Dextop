using System.Net.Sockets;
using System.Buffers;
using System.Buffers.Binary;

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
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1, 4), data.X);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(5, 4), data.Y);
            await stream.WriteAsync(buffer.AsMemory(0, 9)).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task<MouseEventData> ReadMouseEventAsync(NetworkStream stream)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(9);
        try
        {
            int totalRead = 0;
            while (totalRead < 9)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, 9 - totalRead)).ConfigureAwait(false);
                if (read == 0) throw new IOException("Disconnected");
                totalRead += read;
            }
            MouseEventType type = (MouseEventType)buffer[0];
            int x = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(1, 4));
            int y = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(5, 4));
            return new MouseEventData(type, x, y);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
