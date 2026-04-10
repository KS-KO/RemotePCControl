using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

public interface IRemoteSessionService
{
    event Action<SessionLogEntry>? SessionLogAdded;
    event Action? DevicesChanged;
    event Action? RecentConnectionsChanged;
    event Action<ConnectionSnapshot>? SessionSnapshotChanged;

    IReadOnlyList<CaptureDisplayOption> GetCaptureDisplays();

    void SetCaptureDisplay(string displayId);

    IReadOnlyList<CaptureDisplayOption> GetViewerDisplays();

    void SetViewerDisplay(string? displayId);

    void SetKeepViewerOnSafeDisplay(bool enabled);

    IReadOnlyList<CaptureRateOption> GetCaptureRates();

    void SetCaptureRate(int framesPerSecond);

    IReadOnlyList<CompressionOption> GetCompressionOptions();

    void SetCompression(byte encodingMode, long quality);

    void SetAutoReconnect(bool enabled);

    void SetClipboardSyncEnabled(bool enabled);

    IReadOnlyList<DeviceModel> GetDevices();

    IReadOnlyList<RecentConnectionEntry> GetRecentConnections();

    void ToggleFavorite(string internalGuid);

    DuplicateCheckResult GetDuplicateCheckResult();

    DeviceResolutionResult ResolveDevice(string identifier);

    IReadOnlyList<SessionLogEntry> GetSeedLogs();

    ConnectionSnapshot CreateQuickConnection(DeviceModel? device, string approvalMode);

    ConnectionSnapshot CreateSupportSession(DeviceModel? device);

    void DisconnectCurrentSession();

    SessionLogEntry CreateLog(string title, string message, string meta);

    Task UploadFileAsync(string filePath);
}
