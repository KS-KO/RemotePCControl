namespace RemotePCControl.App.Models;

public sealed class DeviceEndpoint
{
    public required string Address { get; init; }

    public required int Port { get; init; }

    public required DeviceEndpointScope Scope { get; init; }

    public string Summary => $"{Scope}: {Address}:{Port}";
}
