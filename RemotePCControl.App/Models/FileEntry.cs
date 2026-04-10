#nullable enable
namespace RemotePCControl.App.Models;

public class FileEntry
{
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    
    // UI용 Helper
    public string SizeLabel => IsDirectory ? "Folder" : FormatSize(Size);

    private static string FormatSize(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        int i;
        double dblSByte = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblSByte = bytes / 1024.0;
        }
        return $"{dblSByte:0.##} {suffix[i]}";
    }
}
