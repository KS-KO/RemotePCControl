#nullable enable
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemotePCControl.App.Infrastructure.Input;

namespace RemotePCControl.App;

public partial class RemoteDesktopWindow : Window
{
    private WriteableBitmap? _bitmap;
    private int _frameWidth;
    private int _frameHeight;

    // 마우스 이벤트 발생 시 (X, Y, Flags) 데이터 전송용
    public event Action<int, int, InputInjectionService.MouseEventFlags>? OnMouseInputCaptured;

    public RemoteDesktopWindow()
    {
        InitializeComponent();
    }

    public void RenderFrame(ReadOnlyMemory<byte> frameData, int width, int height)
    {
        // UI 스레드에서만 WriteableBitmap 접근이 가능하므로 Dispatcher 내부에서 실행
        if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _frameWidth = width;
            _frameHeight = height;
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            DesktopImage.Source = _bitmap;
        }

        _bitmap.Lock();
        try
        {
            unsafe
            {
                using var pinHandle = frameData.Pin();
                Buffer.MemoryCopy(pinHandle.Pointer, (void*)_bitmap.BackBuffer, frameData.Length, frameData.Length);
            }
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            _bitmap.Unlock();
        }
    }

    private void CalculateAndSendMouseEvent(MouseEventArgs e, InputInjectionService.MouseEventFlags flag)
    {
        if (_frameWidth == 0 || _frameHeight == 0) return;

        // Image 컨트롤 기준 현재 마우스 위치
        var pos = e.GetPosition(DesktopImage);
        
        // 실제 Image 렌더링 비율 계산 (Stretch=Uniform 대응 비율)
        double scaleX = _frameWidth / DesktopImage.ActualWidth;
        double scaleY = _frameHeight / DesktopImage.ActualHeight;

        // Black bar(레터박스) 영역 계산 및 보정 (Uniform 이미지의 중심 정렬 고려)
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

        // 보정된 실제 마우스 픽셀 좌표 도출
        int targetX = (int)Math.Round((pos.X - offsetX) * (_frameWidth / effectiveWidth));
        int targetY = (int)Math.Round((pos.Y - offsetY) * (_frameHeight / effectiveHeight));

        // 범위를 벗어난 패스 차단
        if (targetX < 0 || targetX >= _frameWidth || targetY < 0 || targetY >= _frameHeight) return;

        // 이벤트 발동
        OnMouseInputCaptured?.Invoke(targetX, targetY, flag);
    }

    private void DesktopImage_MouseMove(object sender, MouseEventArgs e) => CalculateAndSendMouseEvent(e, InputInjectionService.MouseEventFlags.Move | InputInjectionService.MouseEventFlags.Absolute);
    private void DesktopImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CalculateAndSendMouseEvent(e, InputInjectionService.MouseEventFlags.LeftDown | InputInjectionService.MouseEventFlags.Absolute);
    private void DesktopImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => CalculateAndSendMouseEvent(e, InputInjectionService.MouseEventFlags.LeftUp | InputInjectionService.MouseEventFlags.Absolute);
    private void DesktopImage_MouseRightButtonDown(object sender, MouseButtonEventArgs e) => CalculateAndSendMouseEvent(e, InputInjectionService.MouseEventFlags.RightDown | InputInjectionService.MouseEventFlags.Absolute);
    private void DesktopImage_MouseRightButtonUp(object sender, MouseButtonEventArgs e) => CalculateAndSendMouseEvent(e, InputInjectionService.MouseEventFlags.RightUp | InputInjectionService.MouseEventFlags.Absolute);
}
