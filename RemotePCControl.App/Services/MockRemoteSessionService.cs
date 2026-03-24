using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

public sealed class MockRemoteSessionService : IRemoteSessionService
{
    public event Action<SessionLogEntry>? SessionLogAdded
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

    public IReadOnlyList<DeviceModel> GetDevices()
    {
        return
        [
            new DeviceModel
            {
                Name = "Office Workstation",
                DeviceId = "RPC-OFFICE-01",
                Description = "문서 작업과 원격 지원을 위한 주 업무용 장치",
                LastSeenLabel = "Last seen: just now",
                Status = DeviceStatus.Online,
                IsFavorite = true,
                Capabilities = ["Screen Control", "Clipboard Sync", "Drive Redirect", "File Transfer"]
            },
            new DeviceModel
            {
                Name = "QA Bench PC",
                DeviceId = "RPC-QA-07",
                Description = "테스트 장비 원격 점검 및 재현 확인 장치",
                LastSeenLabel = "Last seen: 3 minutes ago",
                Status = DeviceStatus.Busy,
                IsFavorite = false,
                Capabilities = ["Screen Control", "Reconnect", "Session Logs"]
            },
            new DeviceModel
            {
                Name = "Home Studio",
                DeviceId = "RPC-HOME-12",
                Description = "외부에서 개인 작업을 이어가기 위한 개인 PC",
                LastSeenLabel = "Last seen: 45 minutes ago",
                Status = DeviceStatus.Offline,
                IsFavorite = true,
                Capabilities = ["File Transfer", "Clipboard Sync"]
            }
        ];
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
        var targetName = device?.Name ?? "Unknown Device";
        return new ConnectionSnapshot
        {
            SessionTitle = $"Support session for {targetName}",
            SessionDetail = "상대방 승인 기반 세션이 생성되었고, 지원 시나리오에 맞춘 상태 모니터링이 활성화되었습니다.",
            Status = "Pending Approval",
            QualityPercent = 73,
            QualitySummary = "Approval requested, screen stream preflight complete."
        };
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
}
