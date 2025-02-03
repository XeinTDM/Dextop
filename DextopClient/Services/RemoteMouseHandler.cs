using System.Runtime.InteropServices;
using System.Net.Sockets;
using DextopCommon;

namespace DextopClient.Services;

public class RemoteMouseHandler : IDisposable
{
    private TcpClient? client;
    private NetworkStream? stream;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly Func<int> _getSelectedMonitorIndex;

    public RemoteMouseHandler(Func<int> getSelectedMonitorIndex, string serverAddress = "localhost", int port = 4783)
    {
        _getSelectedMonitorIndex = getSelectedMonitorIndex;
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
                await ProcessMouseEvents().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Mouse handler error: {ex.Message}. Retrying in 5 seconds...");
            }
            finally
            {
                stream?.Dispose();
                client?.Close();
                await Task.Delay(5000, cancellationTokenSource.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessMouseEvents()
    {
        while (!cancellationTokenSource.IsCancellationRequested && client?.Connected == true && stream is not null)
        {
            MouseEventData data = await MouseProtocol.ReadMouseEventAsync(stream).ConfigureAwait(false);
            SimulateMouseEvent(data);
        }
    }

    private void SimulateMouseEvent(MouseEventData data)
    {
        switch (data.EventType)
        {
            case MouseEventType.Move:
                int monitorIndex = _getSelectedMonitorIndex();
                var monitor = Screen.AllScreens[monitorIndex];
                int absoluteX = monitor.Bounds.X + data.X;
                int absoluteY = monitor.Bounds.Y + data.Y;
                SetCursorPos(absoluteX, absoluteY);
                break;
            case MouseEventType.LeftDown:
                SendMouseInput(MOUSEEVENTF_LEFTDOWN);
                break;
            case MouseEventType.LeftUp:
                SendMouseInput(MOUSEEVENTF_LEFTUP);
                break;
            case MouseEventType.RightDown:
                SendMouseInput(MOUSEEVENTF_RIGHTDOWN);
                break;
            case MouseEventType.RightUp:
                SendMouseInput(MOUSEEVENTF_RIGHTUP);
                break;
        }
    }

    private void SendMouseInput(uint mouseEvent)
    {
        INPUT[] input = new INPUT[1];
        input[0].type = INPUT_MOUSE;
        input[0].mi.dwFlags = mouseEvent;
        SendInput(1, input, Marshal.SizeOf(typeof(INPUT)));
    }

    public void Dispose()
    {
        cancellationTokenSource.Cancel();
        stream?.Dispose();
        client?.Close();
        cancellationTokenSource.Dispose();
    }

    private const int INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public MOUSEINPUT mi;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
