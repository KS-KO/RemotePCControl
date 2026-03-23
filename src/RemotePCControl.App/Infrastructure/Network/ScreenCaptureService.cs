#nullable enable
using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemotePCControl.App.Infrastructure.Network;

public sealed class ScreenCaptureService : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _deviceContext;
    private IDXGIOutputDuplication? _deskDupl;
    private ID3D11Texture2D? _stagingTexture;
    private bool _isDisposed;
    private int _width;
    private int _height;

    // 초기화 루틴 (예외 로깅)
    public bool Initialize()
    {
        try
        {
            // 하드웨어 가속 D3D11 디바이스 생성
            D3D11.D3D11CreateDevice(
                null, 
                DriverType.Hardware, 
                DeviceCreationFlags.None, 
                new[] { FeatureLevel.Level_11_0 }, 
                out _device, out _deviceContext).CheckError();

            using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
            adapter.EnumOutputs(0, out var output).CheckError();
            using var output1 = output.QueryInterface<IDXGIOutput1>();

            _deskDupl = output1.DuplicateOutput(_device);

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenCaptureService] D3D11 / DXGI Desktop Duplication API Init error: {ex.Message}");
            return false;
        }
    }

    // Task 기반 비동기 프레임 수집 루프
    public async Task CaptureLoopAsync(Func<ReadOnlyMemory<byte>, int, int, Task> onFrameCaptured, CancellationToken cancellationToken)
    {
        if (_isDisposed || _deskDupl is null || _deviceContext is null || _device is null)
            throw new InvalidOperationException("ScreenCaptureService is not correctly initialized.");

        // 초당 60프레임 제한
        int delayMs = 1000 / 60; 

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Timeout 100ms로 화면 변화 대기
                var result = _deskDupl.AcquireNextFrame(100, out var frameInfo, out var desktopResource);
                if (result.Failure) 
                {
                    // 변화가 없거나 Timeout 에러: 잠시 대기
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    using var tex = desktopResource.QueryInterface<ID3D11Texture2D>();
                    var desc = tex.Description;

                    // Staging Texture: CPU가 읽을 수 있도록 메모리 공간 캐싱 (할당 최소화)
                    if (_stagingTexture == null || _width != (int)desc.Width || _height != (int)desc.Height)
                    {
                        _stagingTexture?.Dispose();
                        _width = (int)desc.Width;
                        _height = (int)desc.Height;

                        var stagingDesc = new Texture2DDescription
                        {
                            Width = (uint)_width,
                            Height = (uint)_height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = desc.Format,
                            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                            Usage = ResourceUsage.Staging,
                            CPUAccessFlags = CpuAccessFlags.Read,
                            BindFlags = BindFlags.None,
                            MiscFlags = ResourceOptionFlags.None
                        };
                        _stagingTexture = _device.CreateTexture2D(stagingDesc);
                    }

                    // GPU의 리소스를 CPU 접근 가능한 Staging Texture로 복사
                    _deviceContext.CopyResource(_stagingTexture, tex);

                    // Map 메서드를 이용해 메모리 포인터 접근
                    var mappedSubresource = _deviceContext.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        int bytesPerPixel = 4; // R8G8B8A8 포맷 (기본 32비트)
                        int payloadSize = _width * _height * bytesPerPixel;

                        // **LOH 보호 규칙**: 일반 byte[] 배열 대신 ArrayPool을 사용하여 GC 오버헤드 완벽 제거
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(payloadSize);
                        try
                        {
                            unsafe
                            {
                                byte* sourcePtr = (byte*)mappedSubresource.DataPointer;
                                fixed (byte* destPtr = buffer)
                                {
                                    if (mappedSubresource.RowPitch == _width * bytesPerPixel)
                                    {
                                        // 메모리 복사 최적화
                                        Buffer.MemoryCopy(sourcePtr, destPtr, payloadSize, payloadSize);
                                    }
                                    else
                                    {
                                        // Pitch 패딩이 포함된 경우 Row 단위 복사
                                        for (int y = 0; y < _height; y++)
                                        {
                                            Buffer.MemoryCopy(
                                                sourcePtr + y * mappedSubresource.RowPitch, 
                                                destPtr + y * _width * bytesPerPixel, 
                                                _width * bytesPerPixel, 
                                                _width * bytesPerPixel);
                                        }
                                    }
                                }
                            }

                            // 캡처 데이터를 호출부로 비동기 전달 (TCP 전송이나 인코딩 단으로 연동)
                            await onFrameCaptured(new ReadOnlyMemory<byte>(buffer, 0, payloadSize), _width, _height).ConfigureAwait(false);
                        }
                        finally
                        {
                            // 풀에 버퍼 반환
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                    }
                    finally
                    {
                        _deviceContext.Unmap(_stagingTexture, 0);
                    }
                }
                finally
                {
                    _deskDupl.ReleaseFrame();
                }

                // FPS 조절
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 의도적인 루프 종료
        }
        catch (Exception ex)
        {
            // 빈 catch 불가 규정 준수
            Debug.WriteLine($"[ScreenCaptureService] Capture Loop Error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // IDisposable 강제, 언매니지드 리소스 안전 해제
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _stagingTexture?.Dispose();
            _deskDupl?.Dispose();
            _deviceContext?.Dispose();
            _device?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenCaptureService] Dispose Error: {ex.Message}");
        }
    }
}
