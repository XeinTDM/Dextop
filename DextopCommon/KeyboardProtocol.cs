using System.Net.Sockets;
using System.Buffers;
using System.Buffers.Binary;

namespace DextopCommon;

public enum KeyboardEventType : byte
{
    KeyDown = 0,
    KeyUp = 1
}

public readonly record struct KeyboardEventData(KeyboardEventType EventType, int VirtualKeyCode);

public static class KeyboardProtocol
{
    public static async Task WriteKeyboardEventAsync(NetworkStream stream, KeyboardEventData data)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(5);
        try
        {
            buffer[0] = (byte)data.EventType;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1, 4), data.VirtualKeyCode);
            await stream.WriteAsync(buffer.AsMemory(0, 5)).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task<KeyboardEventData> ReadKeyboardEventAsync(NetworkStream stream)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(5);
        try
        {
            int totalRead = 0;
            while (totalRead < 5)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, 5 - totalRead)).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException("Disconnected");
                totalRead += read;
            }
            KeyboardEventType type = (KeyboardEventType)buffer[0];
            int vk = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(1, 4));
            return new KeyboardEventData(type, vk);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
