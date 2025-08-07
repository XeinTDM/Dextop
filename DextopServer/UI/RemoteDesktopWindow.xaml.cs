using System.Windows;
using System.Windows.Controls;
using DextopServer.Configurations;
using DextopServer.Services;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DextopCommon;
using System;

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
    private readonly SystemMetricsService metricsService = new();

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

        metricsService.MetricsUpdated += OnMetricsUpdated;
    }

    private void OnMetricsUpdated(double cpu, double gpu)
    {
        Dispatcher.BeginInvoke(() =>
        {
            cpuText.Text = $"CPU: {cpu:F0}%";
            gpuText.Text = $"GPU: {gpu:F0}%";
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
        rdReceiver.Dispose();
        remoteMouseService.Dispose();
        remoteKeyboardService.Dispose();
        remoteMonitorService.Dispose();
        metricsService.Dispose();
        base.OnClosed(e);
    }
}
