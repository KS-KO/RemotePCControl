namespace RemotePCControl.App.Models;

public sealed class ResourceUsageSnapshot
{
    public required double CpuUsagePercent { get; init; }

    public required double MemoryUsagePercent { get; init; }

    public required double UsedMemoryGb { get; init; }

    public required double TotalMemoryGb { get; init; }
}
