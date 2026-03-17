using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

public interface IRemoteSessionService
{
    IReadOnlyList<DeviceModel> GetDevices();

    IReadOnlyList<SessionLogEntry> GetSeedLogs();

    ConnectionSnapshot CreateQuickConnection(DeviceModel? device, string approvalMode);

    ConnectionSnapshot CreateSupportSession(DeviceModel? device);

    SessionLogEntry CreateLog(string title, string message, string meta);
}
