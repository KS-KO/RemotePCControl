namespace RemotePCControl.App.Models;

public sealed class DuplicateCheckResult
{
    public static DuplicateCheckResult None { get; } = new()
    {
        IsDuplicate = false,
        Conflicts = []
    };

    public required bool IsDuplicate { get; init; }

    public required IReadOnlyList<DeviceModel> Conflicts { get; init; }
}
