namespace RemotePCControl.App.Models;

public sealed class SessionLogEntry
{
    public required DateTime Timestamp { get; init; }

    public required string Title { get; init; }

    public required string Message { get; init; }

    public required string Meta { get; init; }

    public string TimestampLabel => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
}
