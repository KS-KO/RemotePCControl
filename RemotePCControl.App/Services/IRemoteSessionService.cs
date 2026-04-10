using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

public interface IRemoteSessionService
{
    event Action<SessionLogEntry>? SessionLogAdded;
    event Action? DevicesChanged;
    event Action? RecentConnectionsChanged;
    event Action<ConnectionSnapshot>? SessionSnapshotChanged;
    event Action<string>? FileSystemListReceived;
    event Action<double>? FileTransferProgressChanged;

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
    void SetLocalDriveRedirectEnabled(bool enabled);
    void RequestFileSystemList(string path);

    IReadOnlyList<DeviceModel> GetDevices();

    IReadOnlyList<RecentConnectionEntry> GetRecentConnections();

    void ToggleFavorite(string internalGuid);
    void UpdateDeviceMetadata(string internalGuid, string? customName, string? customDescription);
    void RegisterManualDevice(string ip, int port);
    void RemoveDevice(string deviceId);

    DuplicateCheckResult GetDuplicateCheckResult();

    DeviceResolutionResult ResolveDevice(string identifier);

    IReadOnlyList<SessionLogEntry> GetSeedLogs();

    ConnectionSnapshot CreateQuickConnection(DeviceModel? device, string approvalMode);

    ConnectionSnapshot CreateSupportSession(DeviceModel? device);

    void DisconnectCurrentSession();

    SessionLogEntry CreateLog(string title, string message, string meta);

    Task UploadFileAsync(string filePath);
    Task DownloadFileAsync(string remotePath);

    void LockRemoteSession();
    void SetRemoteInputBlocked(bool blocked);

    // FR-8: Ctrl+C / Ctrl+V 파일 전송 지원
    void SetCtrlCopyEnabled(bool enabled);
    Task DownloadClipboardFilesAsync();
    void RequestResolutionChange(int width, int height);

    // Phase D: 인터넷 확장 (Relay)
    Task StartRelayHostAsync(string relayIp, int relayPort, string code);
    Task ConnectViaRelayAsync(string relayIp, int relayPort, string code);
    void SetDownloadPath(string path);
    void CancelCurrentFileTransfer();
    string GetDownloadPath();
}
