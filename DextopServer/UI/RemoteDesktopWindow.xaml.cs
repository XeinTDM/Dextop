using System.Windows;
using System.Windows.Controls;
using DextopServer.Configurations;
using DextopServer.Services;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DextopCommon;
using System;
using System.Diagnostics;

namespace DextopServer.UI;

public partial class RemoteDesktopWindow : Window
{
    private readonly RemoteKeyboardService remoteKeyboardService;
    private readonly RemoteDesktopUIManager rdUIManager = new();
    private readonly RemoteMouseService remoteMouseService;
    private readonly RemoteDesktopReceiver rdReceiver;
    private readonly AppConfiguration config = new();
    private bool keyboardSupportActive;
    private bool isFullScreen = false;
    private WindowState previousState;
    private WindowStyle previousStyle;
    private bool mouseSupportActive;
    private Rect windowRect;
    private readonly RemoteMonitorService remoteMonitorService;
    private bool showMetricsOverlay = false;
    private System.Timers.Timer? metricsUpdateTimer;

    public RemoteDesktopWindow()
    {
        InitializeComponent();
        rdReceiver = new RemoteDesktopReceiver(screenshotImage, fpsText, rdUIManager, config);
        QualitySlider.Value = config.JpegQuality;
        PreviewKeyDown += RemoteDesktopWindow_PreviewKeyDown;
        PreviewKeyUp += RemoteDesktopWindow_PreviewKeyUp;
        remoteMouseService = new RemoteMouseService();
        remoteKeyboardService = new RemoteKeyboardService();
        remoteMonitorService = new RemoteMonitorService();
        MouseSupportBtn.Checked += MouseSupportBtn_Checked;
        MouseSupportBtn.Unchecked += MouseSupportBtn_Unchecked;
        KeyboardSupportBtn.Checked += KeyboardSupportBtn_Checked;
        KeyboardSupportBtn.Unchecked += KeyboardSupportBtn_Unchecked;
        PopulateMonitorComboBox();
        InitializeMetricsOverlay();
    }

    private void InitializeMetricsOverlay()
    {
        metricsUpdateTimer = new System.Timers.Timer(500);
        metricsUpdateTimer.Elapsed += (s, e) => UpdateMetricsDisplay();
        metricsUpdateTimer.Start();
    }

    private void UpdateMetricsDisplay()
    {
        if (!showMetricsOverlay)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            var metrics = rdReceiver.GetMetricsCollector().Metrics;
            var snapshot = metrics.GetSnapshot();

            string metricsInfo = $"Client FPS: {snapshot.ClientFps:F1}\n" +
                                $"Server FPS: {snapshot.ServerFps:F1}\n" +
                                $"Bandwidth: {(snapshot.BytesSent + snapshot.BytesReceived) / (1024.0 * 1024.0):F2} MB\n" +
                                $"Quality: {snapshot.AdaptiveQualityLevel}\n" +
                                $"CPU: {snapshot.ProcessCpuPercent:F1}%\n" +
                                $"Memory: {snapshot.ProcessMemoryMb} MB\n" +
                                $"Capture: {snapshot.CaptureTimeMs}ms\n" +
                                $"Encode: {snapshot.EncodeTimeMs}ms\n" +
                                $"Decode: {snapshot.DecodeTimeMs}ms\n" +
                                $"Queue: {snapshot.QueueDepth}\n" +
                                $"Dropped: {snapshot.DroppedFrames}";

            metricsText.Text = metricsInfo;
        });
    }

    private void PopulateMonitorComboBox()
    {
        foreach (var screen in Screen.AllScreens)
        {
            var index = Array.IndexOf(Screen.AllScreens, screen);
            var item = new ComboBoxItem { Content = $"Monitor {index + 1}", Tag = index };
            monitorComboBox.Items.Add(item);
        }
        monitorComboBox.SelectedIndex = 0;
    }

    private async void MonitorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (monitorComboBox.SelectedItem is ComboBoxItem selectedItem &&
            int.TryParse(selectedItem.Tag?.ToString(), out int monitorIndex))
        {
            await remoteMonitorService.SendMonitorSelectionAsync(monitorIndex).ConfigureAwait(false);
        }
    }

    private void MouseSupportBtn_Checked(object sender, RoutedEventArgs e)
    {
        if (!mouseSupportActive)
        {
            screenshotImage.MouseMove += ScreenshotImage_MouseMove;
            screenshotImage.MouseLeftButtonDown += ScreenshotImage_MouseLeftButtonDown;
            screenshotImage.MouseLeftButtonUp += ScreenshotImage_MouseLeftButtonUp;
            screenshotImage.MouseRightButtonDown += ScreenshotImage_MouseRightButtonDown;
            screenshotImage.MouseRightButtonUp += ScreenshotImage_MouseRightButtonUp;
            mouseSupportActive = true;
        }
    }

    private void MouseSupportBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        if (mouseSupportActive)
        {
            screenshotImage.MouseMove -= ScreenshotImage_MouseMove;
            screenshotImage.MouseLeftButtonDown -= ScreenshotImage_MouseLeftButtonDown;
            screenshotImage.MouseLeftButtonUp -= ScreenshotImage_MouseLeftButtonUp;
            screenshotImage.MouseRightButtonDown -= ScreenshotImage_MouseRightButtonDown;
            screenshotImage.MouseRightButtonUp -= ScreenshotImage_MouseRightButtonUp;
            mouseSupportActive = false;
        }
    }

    private void KeyboardSupportBtn_Checked(object sender, RoutedEventArgs e) => keyboardSupportActive = true;
    private void KeyboardSupportBtn_Unchecked(object sender, RoutedEventArgs e) => keyboardSupportActive = false;

    private async void ScreenshotImage_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        await SendMouseEvent(e, MouseEventType.Move).ConfigureAwait(false);
    }

    private async void ScreenshotImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        await SendMouseEvent(e, MouseEventType.LeftDown).ConfigureAwait(false);
    }

    private async void ScreenshotImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        await SendMouseEvent(e, MouseEventType.LeftUp).ConfigureAwait(false);
    }

    private async void ScreenshotImage_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        await SendMouseEvent(e, MouseEventType.RightDown).ConfigureAwait(false);
    }

    private async void ScreenshotImage_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        await SendMouseEvent(e, MouseEventType.RightUp).ConfigureAwait(false);
    }

    private async Task SendMouseEvent(System.Windows.Input.MouseEventArgs e, MouseEventType type)
    {
        if (screenshotImage.Source is BitmapSource bs && screenshotImage.ActualWidth > 0 && screenshotImage.ActualHeight > 0)
        {
            System.Windows.Point pos = e.GetPosition(screenshotImage);
            int x = (int)(pos.X * bs.PixelWidth / screenshotImage.ActualWidth);
            int y = (int)(pos.Y * bs.PixelHeight / screenshotImage.ActualHeight);
            var data = new MouseEventData(type, x, y);
            await remoteMouseService.SendMouseEventAsync(data).ConfigureAwait(false);
        }
    }

    private async void RemoteDesktopWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F12)
        {
            ToggleMetricsOverlay();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F10)
        {
            ToggleMetricsRecording();
            e.Handled = true;
            return;
        }
        if (keyboardSupportActive)
        {
            int vk = KeyInterop.VirtualKeyFromKey(e.Key);
            var data = new KeyboardEventData(KeyboardEventType.KeyDown, vk);
            await remoteKeyboardService.SendKeyboardEventAsync(data).ConfigureAwait(false);
            e.Handled = true;
        }
    }

    private async void RemoteDesktopWindow_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (keyboardSupportActive)
        {
            int vk = KeyInterop.VirtualKeyFromKey(e.Key);
            var data = new KeyboardEventData(KeyboardEventType.KeyUp, vk);
            await remoteKeyboardService.SendKeyboardEventAsync(data).ConfigureAwait(false);
            e.Handled = true;
        }
    }

    private void ToggleMetricsOverlay()
    {
        showMetricsOverlay = !showMetricsOverlay;
        metricsOverlay.Visibility = showMetricsOverlay ? Visibility.Visible : Visibility.Hidden;
    }

    private void ToggleMetricsRecording()
    {
        var metricsCollector = rdReceiver.GetMetricsCollector();
        if (metricsCollector.IsRecording)
        {
            metricsCollector.StopRecordingMetrics();
            System.Windows.MessageBox.Show("Metrics recording stopped.", "Telemetry", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        else
        {
            metricsCollector.StartRecordingMetrics();
            System.Windows.MessageBox.Show("Metrics recording started.", "Telemetry", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (rdReceiver == null)
            return;
        int newQuality = (int)QualitySlider.Value;
        config.JpegQuality = newQuality;
        rdReceiver.UpdateQuality(newQuality);
    }

    private void ToggleFullScreen()
    {
        if (!isFullScreen)
        {
            windowRect = new Rect(Left, Top, Width, Height);
            previousStyle = WindowStyle;
            previousState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            isFullScreen = true;
        }
        else
        {
            WindowStyle = previousStyle;
            WindowState = previousState;
            Left = windowRect.Left;
            Top = windowRect.Top;
            Width = windowRect.Width;
            Height = windowRect.Height;
            isFullScreen = false;
        }
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        metricsUpdateTimer?.Stop();
        metricsUpdateTimer?.Dispose();
        rdReceiver.Dispose();
        remoteMouseService.Dispose();
        remoteKeyboardService.Dispose();
        remoteMonitorService.Dispose();
        base.OnClosed(e);
    }
}
