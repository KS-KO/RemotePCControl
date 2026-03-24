namespace RemotePCControl.App.Models;

public sealed class CaptureRateOption
{
    public required string Label { get; init; }

    public required int FramesPerSecond { get; init; }
}
