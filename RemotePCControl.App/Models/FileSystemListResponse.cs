#nullable enable
namespace RemotePCControl.App.Models;

public sealed class FileSystemListResponse
{
    public bool IsSuccess { get; set; }

    public string CurrentPath { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public List<FileEntry> Entries { get; set; } = [];
}
