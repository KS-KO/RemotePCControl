#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemotePCControl.App.Infrastructure.Input;

namespace RemotePCControl.App;

public partial class RemoteDesktopWindow : Window
{
    private const ushort VirtualKeyEscape = 0x1B;
    private const ushort VirtualKeyLeftWindows = 0x5B;
    private const ushort VirtualKeyR = 0x52;
    private const ushort VirtualKeyTab = 0x09;
    private const ushort VirtualKeyLeftAlt = 0xA4;
    private const ushort VirtualKeyLeftControl = 0xA2;
    private const ushort VirtualKeyLeftShift = 0xA0;

    private WriteableBitmap? _bitmap;
    private readonly Int32Rect? _capturedOutputBounds;
    private string _capturedDisplayLabel;
    private Int32Rect? _preferredViewerBounds;
    private string _viewerDisplayLabel;
    private string _compressionLabel;
    private bool _keepOnSafeDisplay;
    private int _frameWidth;
    private int _frameHeight;
    private readonly System.Windows.Threading.DispatcherTimer _resizeTimer;

    public event Action<int, int, InputInjectionService.MouseEventFlags>? OnMouseInputCaptured;
    public event Action<ushort, InputInjectionService.KeyEventFlags>? OnKeyboardInputCaptured;
    public event Action<int, int>? OnResolutionRequested;
    public event Action? OnLocalPasteRequested;

    public RemoteDesktopWindow(
        Int32Rect? capturedOutputBounds,
        Int32Rect? preferredViewerBounds,
        bool keepOnSafeDisplay,
        string capturedDisplayLabel,
        string viewerDisplayLabel,
        string compressionLabel)
    {
        _capturedOutputBounds = capturedOutputBounds;
        _preferredViewerBounds = preferredViewerBounds;
        _keepOnSafeDisplay = keepOnSafeDisplay;
        _capturedDisplayLabel = capturedDisplayLabel;
        _viewerDisplayLabel = viewerDisplayLabel;
        _compressionLabel = compressionLabel;
        InitializeComponent();
        _resizeTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _resizeTimer.Tick += OnResizeTimerTick;

        Loaded += (_, _) =>
        {
            RefreshStatusBadges();
            MoveToSafeDisplayIfNeeded();
        };
        LocationChanged += (_, _) => MoveToSafeDisplayIfNeeded();
        SizeChanged += OnWindowSizeChanged;
    }

    public void SetKeepOnSafeDisplay(bool keepOnSafeDisplay)
    {
        _keepOnSafeDisplay = keepOnSafeDisplay;
        MoveToSafeDisplayIfNeeded();
    }

    public void SetPreferredViewerBounds(Int32Rect? preferredViewerBounds)
    {
        _preferredViewerBounds = preferredViewerBounds;
        MoveToSafeDisplayIfNeeded();
    }

    public void SetStatusDetails(string capturedDisplayLabel, string viewerDisplayLabel, string compressionLabel)
    {
        _capturedDisplayLabel = capturedDisplayLabel;
        _viewerDisplayLabel = viewerDisplayLabel;
        _compressionLabel = compressionLabel;
        RefreshStatusBadges();
    }

    public void RenderFrame(ReadOnlyMemory<byte> frameData, int width, int height, byte encodingMode)
    {
        if (encodingMode == 0x01)
        {
            RenderCompressedFrame(frameData, width, height);
            return;
        }

        if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _frameWidth = width;
            _frameHeight = height;
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            DesktopImage.Source = _bitmap;
        }

        int stride = width * 4;
        if (frameData.Length < stride * height)
        {
            return;
        }

        unsafe
        {
            using var pinHandle = frameData.Pin();
            _bitmap.WritePixels(
                new Int32Rect(0, 0, width, height),
                (IntPtr)pinHandle.Pointer,
                stride * height,
                stride);
        }
    }

    private void RenderCompressedFrame(ReadOnlyMemory<byte> frameData, int width, int height)
    {
        using var stream = new MemoryStream(frameData.ToArray(), writable: false);
        BitmapImage bitmapImage = new();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.StreamSource = stream;
        bitmapImage.EndInit();
        bitmapImage.Freeze();

        _frameWidth = width;
        _frameHeight = height;
        _bitmap = null;
        DesktopImage.Source = bitmapImage;
    }

    public void UpdateRemoteCursor(string cursorName, bool isVisible)
    {
        if (!isVisible)
        {
            DesktopImage.Cursor = Cursors.None;
            return;
        }

        try
        {
            DesktopImage.Cursor = cursorName switch
            {
                "Arrow" => Cursors.Arrow,
                "IBeam" => Cursors.IBeam,
                "Wait" => Cursors.Wait,
                "Cross" => Cursors.Cross,
                "UpArrow" => Cursors.UpArrow,
                "SizeNWSE" => Cursors.SizeNWSE,
                "SizeNESW" => Cursors.SizeNESW,
                "SizeWE" => Cursors.SizeWE,
                "SizeNS" => Cursors.SizeNS,
                "SizeAll" => Cursors.SizeAll,
                "No" => Cursors.No,
                "Hand" => Cursors.Hand,
                "AppStarting" => Cursors.AppStarting,
                "Help" => Cursors.Help,
                _ => Cursors.Arrow
            };
        }
        catch
        {
            DesktopImage.Cursor = Cursors.Arrow;
        }
    }

    private void CalculateAndSendMouseEvent(MouseEventArgs e, InputInjectionService.MouseEventFlags flag)
    {
        if (_frameWidth == 0 || _frameHeight == 0)
        {
            return;
        }

        var pos = e.GetPosition(DesktopImage);
        double renderRatio = (double)_frameWidth / _frameHeight;
        double controlRatio = DesktopImage.ActualWidth / DesktopImage.ActualHeight;

        double offsetX = 0;
        double offsetY = 0;
        double effectiveWidth = DesktopImage.ActualWidth;
        double effectiveHeight = DesktopImage.ActualHeight;

        if (controlRatio > renderRatio)
        {
            effectiveWidth = DesktopImage.ActualHeight * renderRatio;
            offsetX = (DesktopImage.ActualWidth - effectiveWidth) / 2;
        }
        else
        {
            effectiveHeight = DesktopImage.ActualWidth / renderRatio;
            offsetY = (DesktopImage.ActualHeight - effectiveHeight) / 2;
        }

        int targetX = (int)Math.Round((pos.X - offsetX) * (_frameWidth / effectiveWidth));
        int targetY = (int)Math.Round((pos.Y - offsetY) * (_frameHeight / effectiveHeight));

        if (targetX < 0 || targetX >= _frameWidth || targetY < 0 || targetY >= _frameHeight)
        {
            return;
        }

        OnMouseInputCaptured?.Invoke(targetX, targetY, flag);
    }

    private void DesktopImage_MouseMove(object sender, MouseEventArgs e) => CalculateAndSendMouseEvent(e, InputInjectionService.MouseEventFlags.Move | InputInjectionService.MouseEventFlags.Absolute);
    private void DesktopImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        FocusRemoteWindow();
        CalculateAndSendMouseEvent(e, InputInjectionService.MouseEventFlags.LeftDown | InputInjectionService.MouseEventFlags.Absolute);
    }

    private void DesktopImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => CalculateAndSendMouseEvent(e, InputInjectionService.MouseEventFlags.LeftUp | InputInjectionService.MouseEventFlags.Absolute);

    private void DesktopImage_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        FocusRemoteWindow();
        CalculateAndSendMouseEvent(e, InputInjectionService.MouseEventFlags.RightDown | InputInjectionService.MouseEventFlags.Absolute);
    }

    private void DesktopImage_MouseRightButtonUp(object sender, MouseButtonEventArgs e) => CalculateAndSendMouseEvent(e, InputInjectionService.MouseEventFlags.RightUp | InputInjectionService.MouseEventFlags.Absolute);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // FR-8: Ctrl+V 입력을 통한 로컬 파일 업로드 처리
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            if (Clipboard.ContainsFileDropList())
            {
                OnLocalPasteRequested?.Invoke();
                e.Handled = true;
                return;
            }
        }

        if (TryTranslateVirtualKey(e, out ushort virtualKey))
        {
            OnKeyboardInputCaptured?.Invoke(virtualKey, InputInjectionService.KeyEventFlags.KeyDown);
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (TryTranslateVirtualKey(e, out ushort virtualKey))
        {
            OnKeyboardInputCaptured?.Invoke(virtualKey, InputInjectionService.KeyEventFlags.KeyUp);
            e.Handled = true;
        }
    }

    private void FocusRemoteWindow()
    {
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    private static bool TryTranslateVirtualKey(KeyEventArgs e, out ushort virtualKey)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None)
        {
            virtualKey = 0;
            return false;
        }

        int translated = KeyInterop.VirtualKeyFromKey(key);
        if (translated <= 0 || translated > ushort.MaxValue)
        {
            virtualKey = 0;
            return false;
        }

        virtualKey = (ushort)translated;
        return true;
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        FocusRemoteWindow();
        SendKeyPress(VirtualKeyLeftWindows);
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        FocusRemoteWindow();
        SendKeyChord(VirtualKeyLeftWindows, VirtualKeyR);
    }

    private void TaskManagerButton_Click(object sender, RoutedEventArgs e)
    {
        FocusRemoteWindow();
        SendKeyChord(VirtualKeyLeftControl, VirtualKeyLeftShift, VirtualKeyEscape);
    }

    private void AltTabButton_Click(object sender, RoutedEventArgs e)
    {
        FocusRemoteWindow();
        SendKeyChord(VirtualKeyLeftAlt, VirtualKeyTab);
    }

    private void EscapeButton_Click(object sender, RoutedEventArgs e)
    {
        FocusRemoteWindow();
        SendKeyPress(VirtualKeyEscape);
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.CanResize;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            ResizeMode = ResizeMode.NoResize;
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnResizeTimerTick(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        if (WindowState == WindowState.Maximized) return;

        int newWidth = (int)Math.Round(DesktopImage.ActualWidth);
        int newHeight = (int)Math.Round(DesktopImage.ActualHeight);

        if (newWidth > 0 && newHeight > 0)
        {
            OnResolutionRequested?.Invoke(newWidth, newHeight);
        }
    }

    private void SendKeyPress(ushort virtualKey)
    {
        OnKeyboardInputCaptured?.Invoke(virtualKey, InputInjectionService.KeyEventFlags.KeyDown);
        OnKeyboardInputCaptured?.Invoke(virtualKey, InputInjectionService.KeyEventFlags.KeyUp);
    }

    private void SendKeyChord(params ushort[] virtualKeys)
    {
        foreach (ushort virtualKey in virtualKeys)
        {
            OnKeyboardInputCaptured?.Invoke(virtualKey, InputInjectionService.KeyEventFlags.KeyDown);
        }

        for (int index = virtualKeys.Length - 1; index >= 0; index--)
        {
            OnKeyboardInputCaptured?.Invoke(virtualKeys[index], InputInjectionService.KeyEventFlags.KeyUp);
        }
    }

    private void RefreshStatusBadges()
    {
        CaptureDisplayBadge.Text = $"Capture: {_capturedDisplayLabel}";
        ViewerDisplayBadge.Text = $"Viewer: {_viewerDisplayLabel}";
        CompressionBadge.Text = $"Compression: {_compressionLabel}";
    }

    private void MoveToSafeDisplayIfNeeded()
    {
        var workAreas = GetMonitorWorkAreas();
        if (_preferredViewerBounds is not null)
        {
            var preferredWorkArea = workAreas.FirstOrDefault(workArea => SameBounds(workArea, _preferredViewerBounds.Value));
            bool preferredIsCapturedDisplay = _capturedOutputBounds is not null && SameBounds(preferredWorkArea, _capturedOutputBounds.Value);
            if (preferredWorkArea.Width > 0 && preferredWorkArea.Height > 0 && !(_keepOnSafeDisplay && preferredIsCapturedDisplay))
            {
                MoveWindowToWorkArea(preferredWorkArea);
                return;
            }
        }

        if (!_keepOnSafeDisplay || _capturedOutputBounds is null || workAreas.Count < 2)
        {
            return;
        }

        var windowBounds = new Int32Rect(
            (int)Math.Round(Left),
            (int)Math.Round(Top),
            (int)Math.Max(1, ActualWidth > 0 ? ActualWidth : Width),
            (int)Math.Max(1, ActualHeight > 0 ? ActualHeight : Height));

        if (!Intersects(windowBounds, _capturedOutputBounds.Value))
        {
            return;
        }

        foreach (var workArea in workAreas)
        {
            if (Intersects(workArea, _capturedOutputBounds.Value))
            {
                continue;
            }

            MoveWindowToWorkArea(workArea);
            return;
        }
    }

    private void MoveWindowToWorkArea(Int32Rect workArea)
    {
        var windowWidth = Math.Max(1, ActualWidth > 0 ? ActualWidth : Width);
        var windowHeight = Math.Max(1, ActualHeight > 0 ? ActualHeight : Height);
        Left = workArea.X + Math.Max(0, (workArea.Width - windowWidth) / 2.0);
        Top = workArea.Y + Math.Max(0, (workArea.Height - windowHeight) / 2.0);
        WindowState = WindowState.Normal;
        Activate();
    }

    private static bool Intersects(Int32Rect left, Int32Rect right)
    {
        return left.X < right.X + right.Width &&
               left.X + left.Width > right.X &&
               left.Y < right.Y + right.Height &&
               left.Y + left.Height > right.Y;
    }

    private static bool SameBounds(Int32Rect left, Int32Rect right)
    {
        return left.X == right.X &&
               left.Y == right.Y &&
               left.Width == right.Width &&
               left.Height == right.Height;
    }

    private static List<Int32Rect> GetMonitorWorkAreas()
    {
        List<Int32Rect> workAreas = [];
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, hdc, rect, data) =>
        {
            var monitorInfo = MONITORINFO.Create();
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                workAreas.Add(new Int32Rect(
                    monitorInfo.rcWork.Left,
                    monitorInfo.rcWork.Top,
                    monitorInfo.rcWork.Right - monitorInfo.rcWork.Left,
                    monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top));
            }

            return true;
        }, IntPtr.Zero);

        return workAreas;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        public static MONITORINFO Create()
        {
            return new MONITORINFO
            {
                cbSize = Marshal.SizeOf<MONITORINFO>()
            };
        }
    }
}
