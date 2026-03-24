using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

public interface IRemoteSessionService
{
    event Action<SessionLogEntry>? SessionLogAdded;

    IReadOnlyList<CaptureDisplayOption> GetCaptureDisplays();

    void SetCaptureDisplay(string displayId);

    IReadOnlyList<CaptureDisplayOption> GetViewerDisplays();

    void SetViewerDisplay(string? displayId);

    void SetKeepViewerOnSafeDisplay(bool enabled);

    IReadOnlyList<CaptureRateOption> GetCaptureRates();

    void SetCaptureRate(int framesPerSecond);

    IReadOnlyList<CompressionOption> GetCompressionOptions();

    void SetCompression(byte encodingMode, long quality);

    IReadOnlyList<DeviceModel> GetDevices();

    IReadOnlyList<SessionLogEntry> GetSeedLogs();

    ConnectionSnapshot CreateQuickConnection(DeviceModel? device, string approvalMode);

    ConnectionSnapshot CreateSupportSession(DeviceModel? device);

    SessionLogEntry CreateLog(string title, string message, string meta);

    Task UploadFileAsync(string filePath);
}
