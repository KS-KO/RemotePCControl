using RemotePCControl.App.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RemotePCControl.App.Services;

public sealed class MockRemoteSessionService : IRemoteSessionService
{
    public event Action<SessionLogEntry>? SessionLogAdded
    {
        add { }
        remove { }
    }

    public event Action? DevicesChanged
    {
        add { }
        remove { }
    }

    public event Action? RecentConnectionsChanged
    {
        add { }
        remove { }
    }

    public event Action<ConnectionSnapshot>? SessionSnapshotChanged
    {
        add { }
        remove { }
    }

    public event Action<string>? FileSystemListReceived
    {
        add { }
        remove { }
    }

    public event Action<double>? FileTransferProgressChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<CaptureDisplayOption> GetCaptureDisplays()
    {
        return
        [
            new CaptureDisplayOption
            {
                DisplayId = "display-0",
                Label = "Display 1 (1920x1080)",
                OutputIndex = 0,
                X = 0,
                Y = 0,
                Width = 1920,
                Height = 1080
            }
        ];
    }

    public IReadOnlyList<CaptureDisplayOption> GetViewerDisplays()
    {
        return GetCaptureDisplays();
    }

    public void SetCaptureDisplay(string displayId)
    {
    }

    public void SetViewerDisplay(string? displayId)
    {
    }

    public void SetKeepViewerOnSafeDisplay(bool enabled)
    {
    }

    public IReadOnlyList<CaptureRateOption> GetCaptureRates()
    {
        return
        [
            new CaptureRateOption { Label = "15 FPS", FramesPerSecond = 15 },
            new CaptureRateOption { Label = "30 FPS", FramesPerSecond = 30 }
        ];
    }

    public void SetCaptureRate(int framesPerSecond)
    {
    }

    public IReadOnlyList<CompressionOption> GetCompressionOptions()
    {
        return
        [
            new CompressionOption { Label = "Raw BGRA", EncodingMode = 0x00, Quality = 100 },
            new CompressionOption { Label = "JPEG 85", EncodingMode = 0x01, Quality = 85 },
            new CompressionOption { Label = "JPEG 65", EncodingMode = 0x01, Quality = 65 }
        ];
    }

    public void SetCompression(byte encodingMode, long quality)
    {
    }

    public void SetAutoReconnect(bool enabled)
    {
    }

    public void SetClipboardSyncEnabled(bool enabled)
    {
    }

    public void SetLocalDriveRedirectEnabled(bool enabled)
    {
    }

    public void RequestFileSystemList(string path)
    {
    }

    public IReadOnlyList<DeviceModel> GetDevices()
    {
        return
        [
            new DeviceModel
            {
                Name = "Office Workstation",
                DeviceId = "RPC-OFFICE-01",
                DeviceCode = "RPC-OFFICE-01",
                InternalGuid = "mock-office-workstation",
                Description = "문서 작업과 원격 지원을 위한 주 업무용 장치",
                LastSeenLabel = "Last seen: just now",
                Status = DeviceStatus.Online,
                IsFavorite = true,
                Endpoints =
                [
                    new DeviceEndpoint
                    {
                        Address = "127.0.0.1",
                        Port = 9999,
                        Scope = DeviceEndpointScope.Local
                    }
                ],
                Capabilities = ["Screen Control", "Clipboard Sync", "Drive Redirect", "File Transfer"]
            },
            new DeviceModel
            {
                Name = "QA Bench PC",
                DeviceId = "RPC-QA-07",
                DeviceCode = "RPC-QA-07",
                InternalGuid = "mock-qa-bench",
                Description = "테스트 장비 원격 점검 및 재현 확인 장치",
                LastSeenLabel = "Last seen: 3 minutes ago",
                Status = DeviceStatus.Busy,
                IsFavorite = false,
                Endpoints =
                [
                    new DeviceEndpoint
                    {
                        Address = "192.168.0.17",
                        Port = 9999,
                        Scope = DeviceEndpointScope.Local
                    }
                ],
                Capabilities = ["Screen Control", "Reconnect", "Session Logs"]
            },
            new DeviceModel
            {
                Name = "Home Studio",
                DeviceId = "RPC-HOME-12",
                DeviceCode = "RPC-HOME-12",
                InternalGuid = "mock-home-studio",
                Description = "외부에서 개인 작업을 이어가기 위한 개인 PC",
                LastSeenLabel = "Last seen: 45 minutes ago",
                Status = DeviceStatus.Offline,
                IsFavorite = true,
                Endpoints =
                [
                    new DeviceEndpoint
                    {
                        Address = "203.0.113.20",
                        Port = 9999,
                        Scope = DeviceEndpointScope.Public
                    }
                ],
                Capabilities = ["File Transfer", "Clipboard Sync"]
            }
        ];
    }

    public IReadOnlyList<RecentConnectionEntry> GetRecentConnections()
    {
        return
        [
            new RecentConnectionEntry
            {
                DeviceInternalGuid = "mock-office-workstation",
                DeviceName = "Office Workstation",
                DeviceCode = "RPC-OFFICE-01",
                LastApprovalMode = "Pre-approved device",
                LastConnectedAt = DateTime.Now.AddMinutes(-12)
            },
            new RecentConnectionEntry
            {
                DeviceInternalGuid = "mock-home-studio",
                DeviceName = "Home Studio",
                DeviceCode = "RPC-HOME-12",
                LastApprovalMode = "User approval",
                LastConnectedAt = DateTime.Now.AddHours(-3)
            }
        ];
    }

    public void ToggleFavorite(string internalGuid)
    {
    }

    public void UpdateDeviceMetadata(string internalGuid, string? customName, string? customDescription)
    {
    }

    public void RegisterManualDevice(string ip, int port)
    {
    }

    public DuplicateCheckResult GetDuplicateCheckResult() => DuplicateCheckResult.None;

    public DeviceResolutionResult ResolveDevice(string identifier)
    {
        ConnectionResolutionService resolver = new();
        return resolver.Resolve(identifier, GetDevices());
    }

    public IReadOnlyList<SessionLogEntry> GetSeedLogs()
    {
        return
        [
            new SessionLogEntry
            {
                Timestamp = DateTime.Now.AddMinutes(-42),
                Title = "Policy Ready",
                Message = ".NET 9, x64, MVVM 기준 구성이 적용된 초기 런타임 환경이 준비되었습니다.",
                Meta = "Framework: .NET 9 / Architecture: x64"
            },
            new SessionLogEntry
            {
                Timestamp = DateTime.Now.AddMinutes(-18),
                Title = "Device Inventory",
                Message = "문서 기반 기본 장치 목록이 로드되었습니다.",
                Meta = "Devices: 3"
            }
        ];
    }

    public ConnectionSnapshot CreateQuickConnection(DeviceModel? device, string approvalMode)
    {
        var targetName = device?.Name ?? "Unknown Device";
        return new ConnectionSnapshot
        {
            SessionTitle = $"Connected to {targetName}",
            SessionDetail = $"{approvalMode} approval policy로 연결이 준비되었고, 원격 제어, 파일 전송, 드라이브 리디렉션 검증이 가능합니다.",
            Status = "Connected",
            QualityPercent = 88,
            QualitySummary = "Latency 38ms, bandwidth stable, reconnect standby enabled."
        };
    }

    public ConnectionSnapshot CreateSupportSession(DeviceModel? device)
    {
        return CreateQuickConnection(device, "Support request");
    }

    public void DisconnectCurrentSession()
    {
    }

    public SessionLogEntry CreateLog(string title, string message, string meta)
    {
        return new SessionLogEntry
        {
            Timestamp = DateTime.Now,
            Title = title,
            Message = message,
            Meta = meta
        };
    }

    public Task UploadFileAsync(string filePath)
    {
        return Task.CompletedTask;
    }

    public Task DownloadFileAsync(string remotePath)
    {
        return Task.CompletedTask;
    }

    public void LockRemoteSession()
    {
    }

    public void SetRemoteInputBlocked(bool blocked)
    {
    }

    public void SetCtrlCopyEnabled(bool enabled)
    {
    }

    public Task DownloadClipboardFilesAsync()
    {
        return Task.CompletedTask;
    }

    public void RequestResolutionChange(int width, int height)
    {
    }

    public void SetDownloadPath(string path)
    {
    }

    public void CancelCurrentFileTransfer()
    {
    }

    public string GetDownloadPath()
    {
        return string.Empty;
    }

    public Task StartRelayHostAsync(string relayIp, int relayPort, string code)
    {
        return Task.CompletedTask;
    }

    public Task ConnectViaRelayAsync(string relayIp, int relayPort, string code)
    {
        return Task.CompletedTask;
    }

    public void RemoveDevice(string deviceId)
    {
    }
}
