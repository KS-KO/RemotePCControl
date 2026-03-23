#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RemotePCControl.App.Infrastructure.Input;
using RemotePCControl.App.Infrastructure.Network;
using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

public sealed class RealRemoteSessionService : IRemoteSessionService, IDisposable
{
    private readonly TcpConnectionManager _tcpManager;
    private readonly ScreenCaptureService _captureService;
    private readonly InputInjectionService _inputService;
    private readonly FileTransferService _fileTransferService;
    private readonly ClipboardSyncService _clipboardSyncService;
    private TcpSession? _currentSession;
    private CancellationTokenSource? _captureCts;
    private RemoteDesktopWindow? _rdpWindow;
    private bool _isDisposed;

    // MVP 테스트용 로컬 루프백 접속 기기
    private readonly List<DeviceModel> _devices = [
        new DeviceModel
        {
            DeviceId = "local-loopback-01",
            Name = "Local Host (Mock Target)",
            Description = "127.0.0.1로 연결하여 서비스 통합 테스트를 진행합니다.",
            LastSeenLabel = "Last seen: just now",
            Status = DeviceStatus.Online,
            IsFavorite = true,
            Capabilities = ["Screen", "Input"]
        }
    ];

    public RealRemoteSessionService()
    {
        // 핵심 5대 엔진 인스턴스화
        _tcpManager = new TcpConnectionManager();
        _captureService = new ScreenCaptureService();
        _inputService = new InputInjectionService();
        _fileTransferService = new FileTransferService();
        _clipboardSyncService = new ClipboardSyncService();

        _tcpManager.OnSessionConnected += OnSessionConnected;
        _tcpManager.OnSessionDisconnected += OnSessionDisconnected;

        // 원격 제어 수신 대기 (서버 역할 - 포트 9999)
        _tcpManager.StartListening(9999);
    }

    public IReadOnlyList<DeviceModel> GetDevices() => _devices;

    public IReadOnlyList<SessionLogEntry> GetSeedLogs() => [
        CreateLog("Engine Started", "RealRemoteSessionService initialized with Network, Capture, and Input engines.", "System Ready")
    ];

    public ConnectionSnapshot CreateQuickConnection(DeviceModel? device, string approvalMode)
    {
        // 호스트 장치 연결 시뮬레이션
        _ = ConnectToTargetAsync("127.0.0.1", 9999);
        return new ConnectionSnapshot
        {
            SessionTitle = "Network Handshake",
            SessionDetail = $"TCP Socket 연결을 시도 중입니다: 127.0.0.1:9999",
            Status = "Connecting",
            QualityPercent = 50,
            QualitySummary = "Awaiting socket connection"
        };
    }

    private async Task ConnectToTargetAsync(string ip, int port)
    {
        try
        {
            var session = await _tcpManager.ConnectAsync(ip, port).ConfigureAwait(false);
            // 연결 성공 시 OnSessionConnected 이벤트가 발생함
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealRemoteSessionService] Connection to {ip}:{port} failed: {ex.Message}");
        }
    }

    public ConnectionSnapshot CreateSupportSession(DeviceModel? device)
    {
        return new ConnectionSnapshot
        {
            SessionTitle = "Waiting for Approval",
            SessionDetail = $"사용자 승인을 기다리고 있습니다.",
            Status = "Pending",
            QualityPercent = 10,
            QualitySummary = "Support Request"
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

    private void OnSessionConnected(TcpSession session)
    {
        _currentSession = session;
        session.OnMessageReceived += HandleIncomingMessage;

        // 현재 클라이언트가 서버 역할을 수행한다고 가정할 때 화면 캡처 시작
        if (_captureService.Initialize())
        {
            _captureCts = new CancellationTokenSource();
            _ = _captureService.CaptureLoopAsync(OnFrameCapturedAsync, _captureCts.Token);
        }
    }

    private async Task OnFrameCapturedAsync(ReadOnlyMemory<byte> frameData, int width, int height)
    {
        if (_currentSession is not null && !_isDisposed)
        {
            try
            {
                // 해상도 정보(8바이트)와 타입 식별자(0x00)를 영상 프레임 앞에 붙여 전송
                // 구조: [Type: 1] + [Width: 4] + [Height: 4] + [FrameData]
                byte[] packet = System.Buffers.ArrayPool<byte>.Shared.Rent(frameData.Length + 9);
                try
                {
                    packet[0] = 0x00; // Type: Screen Frame
                    BitConverter.TryWriteBytes(packet.AsSpan(1, 4), width);
                    BitConverter.TryWriteBytes(packet.AsSpan(5, 4), height);
                    frameData.Span.CopyTo(packet.AsSpan(9));
                    
                    await _currentSession.SendAsync(new ReadOnlyMemory<byte>(packet, 0, frameData.Length + 9)).ConfigureAwait(false);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(packet);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RealRemoteSessionService] Frame send error: {ex.Message}");
            }
        }
    }

    private void HandleIncomingMessage(TcpSession session, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0) return;

        byte packetType = payload.Span[0];

        if (packetType == 0x00 && payload.Length > 9)
        {
            // 스크린 캡처 프레임 렌더링
            int width = BitConverter.ToInt32(payload.Span.Slice(1, 4));
            int height = BitConverter.ToInt32(payload.Span.Slice(5, 4));
            var frameData = payload.Slice(9);

            Application.Current?.Dispatcher.Invoke(() => 
            {
                if (_rdpWindow == null || !_rdpWindow.IsLoaded)
                {
                    _rdpWindow = new RemoteDesktopWindow();
                    _rdpWindow.OnMouseInputCaptured += HandleClientMouseInput;
                    _rdpWindow.Show();
                }
                _rdpWindow.RenderFrame(frameData, width, height);
            });
        }
        else if (packetType == 0x01 && payload.Length == 13)
        {
            // 수신측 (서버 역할): 마우스 조작 패킷 디코딩 후 시스템 주입
            int x = BitConverter.ToInt32(payload.Span.Slice(1, 4));
            int y = BitConverter.ToInt32(payload.Span.Slice(5, 4));
            uint flags = BitConverter.ToUInt32(payload.Span.Slice(9, 4));
            
            _inputService.InjectMouse(x, y, (InputInjectionService.MouseEventFlags)flags);
        }
        else if (packetType == 0x03)
        {
            // 파일 청크 수신 - 테스트용으로 지정 경로에 무조건 덤프 (실 구현 시 경로/파일명 통신 추가 필요)
            var chunkData = payload.Slice(1);
            _ = _fileTransferService.ReceiveFileChunkAsync("C:\\Temp\\ReceivedFile.dat", chunkData, default);
        }
    }

    private void HandleClientMouseInput(int x, int y, InputInjectionService.MouseEventFlags flags)
    {
        if (_currentSession == null || _isDisposed) return;

        // 클라이언트 측: 마우스 좌표와 클릭 상태 패킷 구성 (Type=1, X=4, Y=4, Flags=4 -> 13 bytes)
        byte[] inputPacket = new byte[13];
        inputPacket[0] = 0x01; // Mouse Input ID
        BitConverter.TryWriteBytes(inputPacket.AsSpan(1, 4), x);
        BitConverter.TryWriteBytes(inputPacket.AsSpan(5, 4), y);
        BitConverter.TryWriteBytes(inputPacket.AsSpan(9, 4), (uint)flags);

        _ = _currentSession.SendAsync(inputPacket).ConfigureAwait(false);
    }

    public Task UploadFileAsync(string filePath)
    {
        if (_currentSession == null || _isDisposed) return Task.CompletedTask;
        return _fileTransferService.SendFileAsync(filePath, _currentSession, default);
    }

    private void OnSessionDisconnected(TcpSession session, Exception? ex)
    {
        _currentSession = null;
        _captureCts?.Cancel();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Application.Current?.Dispatcher.Invoke(() => _rdpWindow?.Close());

        // 모든 엔진 리소스 안전 반환
        _captureCts?.Cancel();
        _tcpManager.Dispose();
        _captureService.Dispose();
        _inputService.Dispose();
        _captureCts?.Dispose();
    }
}
