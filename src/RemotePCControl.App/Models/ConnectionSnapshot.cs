namespace RemotePCControl.App.Models;

public sealed class ConnectionSnapshot
{
    public required string SessionTitle { get; init; }

    public required string SessionDetail { get; init; }

    public required string Status { get; init; }

    public required int QualityPercent { get; init; }

    public required string QualitySummary { get; init; }
}
