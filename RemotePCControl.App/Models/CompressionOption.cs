namespace RemotePCControl.App.Models;

public sealed class CompressionOption
{
    public required string Label { get; init; }

    public required byte EncodingMode { get; init; }

    public required long Quality { get; init; }
}
