#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RemotePCControl.App.Models;
using Vortice.DXGI;

namespace RemotePCControl.App.Infrastructure.Network;

public sealed class ScreenCaptureService : IDisposable
{
    private bool _isDisposed;
    private int _selectedOutputIndex;
    private ulong _lastFrameSignature;
    private int _unchangedFrameCount;
    private int _targetFramesPerSecond = 30;

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
            return CapturedOutputBounds is not null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenCaptureService] Capture initialization error: {ex.Message}");
            return false;
        }
    }

    public async Task CaptureLoopAsync(Func<ReadOnlyMemory<byte>, int, int, Task> onFrameCaptured, CancellationToken cancellationToken)
    {
        if (_isDisposed || CapturedOutputBounds is null)
        {
            throw new InvalidOperationException("ScreenCaptureService is not correctly initialized.");
        }

        int delayMs = 1000 / Math.Max(1, _targetFramesPerSecond);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bounds = CapturedOutputBounds.Value;

                try
                {
                    using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new System.Drawing.Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
                    }

                    var rect = new Rectangle(0, 0, bounds.Width, bounds.Height);
                    var bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        int stride = bounds.Width * 4;
                        int payloadSize = stride * bounds.Height;
                        byte[] buffer = ArrayPool<byte>.Shared.Rent(payloadSize);
                        try
                        {
                            CopyBitmapToBuffer(bitmapData, buffer, stride, bounds.Height);
                            ulong frameSignature = ComputeFrameSignature(buffer, payloadSize);
                            bool hasChanged = frameSignature != _lastFrameSignature;
                            if (hasChanged)
                            {
                                _lastFrameSignature = frameSignature;
                                _unchangedFrameCount = 0;
                                await onFrameCaptured(new ReadOnlyMemory<byte>(buffer, 0, payloadSize), bounds.Width, bounds.Height).ConfigureAwait(false);
                            }
                            else
                            {
                                _unchangedFrameCount++;
                                if (_unchangedFrameCount >= 90)
                                {
                                    _unchangedFrameCount = 0;
                                    await onFrameCaptured(new ReadOnlyMemory<byte>(buffer, 0, payloadSize), bounds.Width, bounds.Height).ConfigureAwait(false);
                                }
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                    }
                    finally
                    {
                        bitmap.UnlockBits(bitmapData);
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
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenCaptureService] Capture Loop Fatal Error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
    }

    private static void CopyBitmapToBuffer(BitmapData bitmapData, byte[] buffer, int destinationStride, int height)
    {
        for (int y = 0; y < height; y++)
        {
            IntPtr sourceRow = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride);
            Marshal.Copy(sourceRow, buffer, y * destinationStride, destinationStride);
        }
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
