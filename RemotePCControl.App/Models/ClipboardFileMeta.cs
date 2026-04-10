#nullable enable

namespace RemotePCControl.App.Models;

public sealed class ClipboardFileMeta
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public string FullPath { get; set; } = string.Empty;
}
