using System.Runtime.InteropServices;
using System.Net.Sockets;
using DextopCommon;

namespace DextopClient.Services;

public class RemoteKeyboardHandler : IDisposable
{
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private NetworkStream? stream;
    private TcpClient? client;

    public RemoteKeyboardHandler(string serverAddress = "localhost", int port = 4784)
    {
        _ = ConnectAsync(serverAddress, port);
    }

    private async Task ConnectAsync(string serverAddress, int port)
    {
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                client = new TcpClient(serverAddress, port) { NoDelay = true };
                stream = client.GetStream();
                await ProcessKeyboardEvents().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Keyboard handler error: {ex.Message}. Retrying in 5 seconds...");
            }
            finally
            {
                stream?.Dispose();
                client?.Close();
                await Task.Delay(5000, cancellationTokenSource.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessKeyboardEvents()
    {
        while (!cancellationTokenSource.IsCancellationRequested && client?.Connected == true && stream is not null)
        {
            KeyboardEventData data = await KeyboardProtocol.ReadKeyboardEventAsync(stream).ConfigureAwait(false);
            SimulateKeyboardEvent(data);
        }
    }

    private void SimulateKeyboardEvent(KeyboardEventData data)
    {
        switch (data.EventType)
        {
            case KeyboardEventType.KeyDown:
                SendKeyboardInput(data.VirtualKeyCode, 0);
                break;
            case KeyboardEventType.KeyUp:
                SendKeyboardInput(data.VirtualKeyCode, KEYEVENTF_KEYUP);
                break;
        }
    }

    private void SendKeyboardInput(int keyCode, uint flags)
    {
        INPUT[] input = new INPUT[1];
        input[0].type = INPUT_KEYBOARD;
        input[0].ki.wVk = (ushort)keyCode;
        input[0].ki.wScan = 0;
        input[0].ki.dwFlags = flags;
        input[0].ki.time = 0;
        input[0].ki.dwExtraInfo = IntPtr.Zero;
        SendInput(1, input, Marshal.SizeOf(typeof(INPUT)));
    }

    public void Dispose()
    {
        cancellationTokenSource.Cancel();
        stream?.Dispose();
        client?.Close();
        cancellationTokenSource.Dispose();
    }

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
