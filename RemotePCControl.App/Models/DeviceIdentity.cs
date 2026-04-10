namespace RemotePCControl.App.Models;

public sealed class DeviceIdentity
{
    public required string InternalGuid { get; init; }

    public required string DeviceName { get; set; }

    public required string DeviceCode { get; set; }
}
