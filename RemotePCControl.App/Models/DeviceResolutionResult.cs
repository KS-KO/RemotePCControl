namespace RemotePCControl.App.Models;

public sealed class DeviceResolutionResult
{
    public required DeviceResolutionStatus Status { get; init; }

    public DeviceModel? ResolvedDevice { get; init; }

    public required IReadOnlyList<DeviceModel> CandidateDevices { get; init; }
}
