namespace RemotePCControl.App.Models;

public sealed class RecentConnectionEntry
{
    public required string DeviceInternalGuid { get; init; }

    public required string DeviceName { get; init; }

    public required string DeviceCode { get; init; }

    public required string LastApprovalMode { get; init; }

    public required DateTime LastConnectedAt { get; init; }

    public string LastConnectedLabel => LastConnectedAt.ToString("yyyy-MM-dd HH:mm:ss");
}
