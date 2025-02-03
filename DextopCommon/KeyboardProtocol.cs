using System.Net.Sockets;

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
        byte[] buffer = new byte[5];
        buffer[0] = (byte)data.EventType;
        BitConverter.GetBytes(data.VirtualKeyCode).CopyTo(buffer, 1);
        await stream.WriteAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
    }

    public static async Task<KeyboardEventData> ReadKeyboardEventAsync(NetworkStream stream)
    {
        byte[] buffer = new byte[5];
        int totalRead = 0;
        while (totalRead < 5)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, 5 - totalRead)).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("Disconnected");
            totalRead += read;
        }
        KeyboardEventType type = (KeyboardEventType)buffer[0];
        int vk = BitConverter.ToInt32(buffer, 1);
        return new KeyboardEventData(type, vk);
    }
}
