#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RemotePCControl.App.Models;
using Vortice;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace RemotePCControl.App.Infrastructure.Network;

public sealed class ScreenCaptureService : IDisposable
{
    private bool _isDisposed;
    private int _selectedOutputIndex;
    private ulong _lastFrameSignature;
    private int _unchangedFrameCount;
    private int _targetFramesPerSecond = 30;
    
    private ID3D11Device? _d3d11Device;
    private IDXGIOutputDuplication? _deskDupl;
    private ID3D11Texture2D? _stagingTexture;

    public Int32Rect? CapturedOutputBounds { get; private set; }

    public IReadOnlyList<CaptureDisplayOption> GetAvailableDisplays()
    {
        List<CaptureDisplayOption> displays = [];

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
                if (adapterResult.Failure || adapter is null)
                {
                    break;
                }

                using (adapter)
                {
                    for (int outputIndex = 0; ; outputIndex++)
                    {
                        var outputResult = adapter.EnumOutputs((uint)outputIndex, out var output);
                        if (outputResult.Failure || output is null)
                        {
                            break;
                        }

                        using (output)
                        {
                            var description = output.Description;
                            int width = description.DesktopCoordinates.Right - description.DesktopCoordinates.Left;
                            int height = description.DesktopCoordinates.Bottom - description.DesktopCoordinates.Top;

                            displays.Add(new CaptureDisplayOption
                            {
                                DisplayId = $"display-{displays.Count}",
                                Label = $"Display {displays.Count + 1} ({width}x{height})",
                                OutputIndex = displays.Count,
                                X = description.DesktopCoordinates.Left,
                                Y = description.DesktopCoordinates.Top,
                                Width = width,
                                Height = height
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenCaptureService] Display enumeration error: {ex.Message}");
        }

        return displays;
    }

    public void SelectOutput(int outputIndex)
    {
        _selectedOutputIndex = Math.Max(0, outputIndex);
    }

    public void SetCaptureRate(int framesPerSecond)
    {
        _targetFramesPerSecond = Math.Clamp(framesPerSecond, 1, 60);
    }

    public Int32Rect? GetOutputBounds(int outputIndex)
    {
        return GetAvailableDisplays()
            .Where(display => display.OutputIndex == outputIndex)
            .Select(display => new Int32Rect(display.X, display.Y, display.Width, display.Height))
            .Cast<Int32Rect?>()
            .FirstOrDefault();
    }

    public bool Initialize()
    {
        try
        {
            CapturedOutputBounds = GetOutputBounds(_selectedOutputIndex);
            if (CapturedOutputBounds == null) return false;

            // D3D11 디바이스 생성
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None, null, out _d3d11Device).CheckError();
            
            using (IDXGIDevice dxgiDevice = _d3d11Device!.QueryInterface<IDXGIDevice>())
            {
                using (IDXGIAdapter adapter = dxgiDevice.GetParent<IDXGIAdapter>())
                {
                    adapter.EnumOutputs((uint)_selectedOutputIndex, out IDXGIOutput output).CheckError();
                    using (output)
                    {
                        using (IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>())
                        {
                            _deskDupl = output1.DuplicateOutput(_d3d11Device);
                        }
                    }
                }
            }
            
            // Staging Texture 생성 (CPU 읽기용)
            var desc = new Texture2DDescription
            {
                Width = (uint)CapturedOutputBounds.Value.Width,
                Height = (uint)CapturedOutputBounds.Value.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };
            _stagingTexture = _d3d11Device.CreateTexture2D(desc);

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenCaptureService] DXGI Initialization error: {ex.Message}");
            return false;
        }
    }

    public async Task CaptureLoopAsync(Func<ReadOnlyMemory<byte>, int, int, Task> onFrameCaptured, CancellationToken cancellationToken)
    {
        if (_isDisposed || CapturedOutputBounds is null || _deskDupl is null || _d3d11Device is null || _stagingTexture is null)
        {
            throw new InvalidOperationException("ScreenCaptureService is not correctly initialized with DXGI.");
        }

        int delayMs = 1000 / Math.Max(1, _targetFramesPerSecond);
        var bounds = CapturedOutputBounds.Value;
        int stride = bounds.Width * 4;
        int payloadSize = stride * bounds.Height;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(payloadSize);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var result = _deskDupl.AcquireNextFrame(500, out var frameInfo, out var desktopResource);
                    if (result.Success && desktopResource != null)
                    {
                        using (ID3D11Texture2D desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
                        {
                            _d3d11Device.ImmediateContext.CopyResource(_stagingTexture, desktopTexture);
                        }
                        desktopResource.Dispose(); 
                        _deskDupl.ReleaseFrame();

                        // 데이터 매핑 및 복사
                        var mappedResource = _d3d11Device.ImmediateContext.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            unsafe
                            {
                                byte* srcPtr = (byte*)mappedResource.DataPointer;
                                fixed (byte* dstPtr = buffer)
                                {
                                    for (int y = 0; y < bounds.Height; y++)
                                    {
                                        Buffer.MemoryCopy(srcPtr + (y * (long)mappedResource.RowPitch), dstPtr + (y * (long)stride), (long)stride, (long)stride);
                                    }
                                }
                            }

                            // DXGI가 프레임 갱신을 알려주지만, 내용 변화가 없을 수도 있으므로 Hash 체크 병행
                            ulong frameSignature = ComputeFrameSignature(buffer, payloadSize);
                            if (frameSignature != _lastFrameSignature)
                            {
                                _lastFrameSignature = frameSignature;
                                _unchangedFrameCount = 0;
                                await onFrameCaptured(new ReadOnlyMemory<byte>(buffer, 0, payloadSize), bounds.Width, bounds.Height).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            _d3d11Device.ImmediateContext.Unmap(_stagingTexture, 0);
                        }
                    }
                    else if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
                    {
                        // 화면 변화 없음
                        _unchangedFrameCount++;
                        if (_unchangedFrameCount >= 60) // 약 2초마다 강제 전송 (연결 확인용)
                        {
                            _unchangedFrameCount = 0;
                            await onFrameCaptured(new ReadOnlyMemory<byte>(buffer, 0, payloadSize), bounds.Width, bounds.Height).ConfigureAwait(false);
                        }
                    }
                    else if (result.Code == Vortice.DXGI.ResultCode.AccessLost.Code)
                    {
                        // 세션 변경 등으로 권한 상실 시 재초기화 필요
                        Debug.WriteLine("[ScreenCaptureService] DXGI Access Lost. Re-initializing...");
                        break; 
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenCaptureService] Capture Loop Error: {ex.Message}");
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _deskDupl?.Dispose();
        _stagingTexture?.Dispose();
        _d3d11Device?.Dispose();
    }


    private static ulong ComputeFrameSignature(byte[] buffer, int length)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offsetBasis;
        int step = Math.Max(1, length / 4096);
        for (int i = 0; i < length; i += step)
        {
            hash ^= buffer[i];
            hash *= prime;
        }

        hash ^= (ulong)length;
        hash *= prime;
        return hash;
    }
}
