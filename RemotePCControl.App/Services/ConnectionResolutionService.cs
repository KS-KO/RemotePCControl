using System;
using System.Linq;
using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

public sealed class ConnectionResolutionService
{
    public DeviceResolutionResult Resolve(string identifier, IReadOnlyList<DeviceModel> devices)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return new DeviceResolutionResult
            {
                Status = DeviceResolutionStatus.NotFound,
                ResolvedDevice = null,
                CandidateDevices = []
            };
        }

        string normalizedIdentifier = identifier.Trim();
        DeviceModel[] matches = devices
            .Where(device =>
                device.DeviceId.Equals(normalizedIdentifier, StringComparison.OrdinalIgnoreCase) ||
                device.DeviceCode.Equals(normalizedIdentifier, StringComparison.OrdinalIgnoreCase) ||
                device.Name.Equals(normalizedIdentifier, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            0 => new DeviceResolutionResult
            {
                Status = DeviceResolutionStatus.NotFound,
                ResolvedDevice = null,
                CandidateDevices = []
            },
            1 => new DeviceResolutionResult
            {
                Status = DeviceResolutionStatus.SingleMatch,
                ResolvedDevice = matches[0],
                CandidateDevices = matches
            },
            _ => new DeviceResolutionResult
            {
                Status = DeviceResolutionStatus.MultipleMatches,
                ResolvedDevice = null,
                CandidateDevices = matches
            }
        };
    }
}
