#nullable enable

namespace RemotePCControl.App.Models;

public sealed record ConnectionSetupData(
    string DeviceName,
    string DeviceCode,
    string InternalGuid,
    string RequestedApprovalMode
);

public enum ConnectionSetupResult : byte
{
    Approved = 0x00,
    Denied = 0x01,
    Wait = 0x02
}
