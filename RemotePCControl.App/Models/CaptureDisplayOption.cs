namespace RemotePCControl.App.Models;

public sealed class CaptureDisplayOption
{
    public required string DisplayId { get; init; }

    public required string Label { get; init; }

    public required int OutputIndex { get; init; }

    public required int X { get; init; }

    public required int Y { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }
}
