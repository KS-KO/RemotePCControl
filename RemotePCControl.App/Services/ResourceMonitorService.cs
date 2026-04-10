#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

public sealed class ResourceMonitorService : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private ulong _previousIdleTime;
    private ulong _previousKernelTime;
    private ulong _previousUserTime;
    private bool _isDisposed;

    public event Action<ResourceUsageSnapshot>? SnapshotUpdated;

    public void Start()
    {
        ThrowIfDisposed();
        _ = MonitorLoopAsync(_cts.Token);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        // 첫 샘플은 기준 시점 저장용으로 사용합니다.
        CaptureCpuTimes(out _previousIdleTime, out _previousKernelTime, out _previousUserTime);

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

            ResourceUsageSnapshot snapshot = CreateSnapshot();
            SnapshotUpdated?.Invoke(snapshot);
        }
    }

    private ResourceUsageSnapshot CreateSnapshot()
    {
        CaptureCpuTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

        ulong idleDelta = idleTime - _previousIdleTime;
        ulong kernelDelta = kernelTime - _previousKernelTime;
        ulong userDelta = userTime - _previousUserTime;
        ulong totalDelta = kernelDelta + userDelta;

        double cpuUsagePercent = totalDelta == 0
            ? 0
            : Math.Clamp((1d - (double)idleDelta / totalDelta) * 100d, 0d, 100d);

        _previousIdleTime = idleTime;
        _previousKernelTime = kernelTime;
        _previousUserTime = userTime;

        MEMORYSTATUSEX memoryStatus = MEMORYSTATUSEX.Create();
        GlobalMemoryStatusEx(ref memoryStatus);

        double totalMemoryGb = memoryStatus.ullTotalPhys / 1024d / 1024d / 1024d;
        double usedMemoryGb = (memoryStatus.ullTotalPhys - memoryStatus.ullAvailPhys) / 1024d / 1024d / 1024d;
        double memoryUsagePercent = totalMemoryGb <= 0
            ? 0
            : Math.Clamp(usedMemoryGb / totalMemoryGb * 100d, 0d, 100d);

        return new ResourceUsageSnapshot
        {
            CpuUsagePercent = cpuUsagePercent,
            MemoryUsagePercent = memoryUsagePercent,
            UsedMemoryGb = usedMemoryGb,
            TotalMemoryGb = totalMemoryGb
        };
    }

    private static void CaptureCpuTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime)
    {
        FILETIME idle;
        FILETIME kernel;
        FILETIME user;
        GetSystemTimes(out idle, out kernel, out user);

        idleTime = ToUInt64(idle);
        kernelTime = ToUInt64(kernel);
        userTime = ToUInt64(user);
    }

    private static ulong ToUInt64(FILETIME time)
    {
        return ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(ResourceMonitorService));
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public static MEMORYSTATUSEX Create()
        {
            return new MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
            };
        }
    }
}
