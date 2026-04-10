#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RemotePCControl.App.Infrastructure.Input;
using RemotePCControl.App.Infrastructure.Network;
using RemotePCControl.App.Infrastructure.FileSystem;
using RemotePCControl.App.Infrastructure.Logging;
using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

public sealed class RealRemoteSessionService : IRemoteSessionService, IDisposable
{
    private const int MaxReconnectAttempts = 5;
    private const int MaxClipboardTextLength = 64 * 1024;
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(16);
    private static readonly TimeSpan MaxReconnectWindow = TimeSpan.FromMinutes(5);
    private static readonly string DownloadFolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "RemotePCControl");
    private const byte ScreenFramePacketType = 0x00;
    private const byte MouseInputPacketType = 0x01;
    private const byte KeyboardInputPacketType = 0x02;
    private const byte FileChunkPacketType = 0x03;
    private const byte ClipboardTextPacketType = 0x04;
    private const byte FileMetaPacketType = 0x05;
    private const byte FileDownloadRequestPacketType = 0x06;
    private const byte FileSystemListRequestPacketType = 0x09;
    private const byte FileSystemListResponsePacketType = 0x0A;
    private const byte RawBgraEncoding = 0x00;
    private const byte JpegEncoding = 0x01;
    private const byte ClipboardImagePacketType = 0x0C;
    private const byte CursorShapePacketType = 0x0D;
    private const byte LockSessionPacketType = 0x0E;
    private const byte BlockInputPacketType = 0x0F;
    private const byte ClipboardFilesPacketType = 0x10;
    private const byte ResolutionChangePacketType = 0x11;
    private const byte ConnectionSetupPacketType = 0x12;
    private const byte ConnectionSetupResponsePacketType = 0x13;
    private const byte PingPacketType = 0x14;
    private const byte PongPacketType = 0x15;
    private const byte RelayHostPacketType = 0x30;
    private const byte RelayConnectPacketType = 0x31;
    private const byte RelayErrorPacketType = 0x32;

    private readonly TcpConnectionManager _tcpManager;
    private readonly ScreenCaptureService _captureService;
    private readonly InputInjectionService _inputService;
    private readonly FileTransferService _fileTransferService;
    private readonly ClipboardSyncService _clipboardSyncService;
    private readonly DeviceIdentityStore _deviceIdentityStore;
    private readonly DevicePreferenceStore _devicePreferenceStore;
    private readonly LocalDiscoveryService _localDiscoveryService;
    private readonly ConnectionResolutionService _connectionResolutionService;
    private readonly ApprovalService _approvalService;
    private readonly FileSystemService _fileSystemService;
    private readonly SessionLogStore _logStore;
    private readonly CursorCaptureService _cursorCaptureService;
    private readonly Dictionary<string, DeviceModel> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoriteDeviceInternalGuids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DevicePreferenceStore.DeviceMetadata> _deviceMetadataMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RecentConnectionEntry> _recentConnections = [];
    private readonly DeviceIdentity _localIdentity;
    private DuplicateCheckResult _duplicateCheckResult = DuplicateCheckResult.None;

    private TcpSession? _currentSession;
    private CancellationTokenSource? _captureCts;
    private RemoteDesktopWindow? _rdpWindow;
    private int _receivedFrameCount;
    private string? _selectedCaptureDisplayId;
    private string? _selectedViewerDisplayId;
    private bool _keepViewerOnSafeDisplay = true;
    private byte _compressionEncodingMode = JpegEncoding;
    private long _jpegQuality = 85;
    private bool _viewerClosedByUser;
    private bool _isClosingViewerWindow;
    private bool _autoReconnectEnabled = true;
    private bool _isClipboardSyncEnabled = true;
    private bool _userInitiatedDisconnect;
    private int _isReconnectInProgress;
    private DeviceModel? _lastRequestedDevice;
    private string? _lastRequestedIdentifier;
    private string? _lastRequestedAddress;
    private int _lastRequestedPort;
    private string _lastApprovalMode = "User approval";
    private CancellationTokenSource? _reconnectCts;
    private CancellationTokenSource? _clipboardSyncCts;
    private CancellationTokenSource? _cursorCts;
    private string _lastSentClipboardText = string.Empty;
    private string _lastAppliedClipboardText = string.Empty;
    private ulong _lastSentClipboardImageHash = 0;
    private ulong _lastAppliedClipboardImageHash = 0;
    private ulong _lastSentClipboardFilesHash = 0;
    private List<Models.ClipboardFileMeta> _remoteClipboardFiles = new();
    private bool _isCtrlCopyEnabled = false;
    private string _receivingFilePath = string.Empty;
    private long _receivingFileSize = 0;
    private long _receivedBytes = 0;
    private DevicePreferenceStore.LastKnownConnectionInfo? _lastKnownConnection;
    private bool _isSessionApproved;
    private long _lastMeasuredRttMs = -1;
    private CancellationTokenSource? _telemetryCts;
    private CancellationTokenSource? _fileTransferCts;
    private string _customDownloadPath = string.Empty;
    private ConnectionSnapshot _activeSnapshot = new() { SessionTitle = "Idle", SessionDetail = "No active session", Status = "Idle", QualityPercent = 0, QualitySummary = "N/A" };
    private int _connectionQualityPercent;
    private string _connectionQualitySummary = "N/A";
    private bool _isDisposed;

    public RealRemoteSessionService()
    {
        _tcpManager = new TcpConnectionManager();
        _captureService = new ScreenCaptureService();
        _inputService = new InputInjectionService();
        _fileTransferService = new FileTransferService();
        _fileTransferService.ProgressChanged += (path, progress) => FileTransferProgressChanged?.Invoke(progress);
        _clipboardSyncService = new ClipboardSyncService();
        _deviceIdentityStore = new DeviceIdentityStore();
        _devicePreferenceStore = new DevicePreferenceStore();
        _localDiscoveryService = new LocalDiscoveryService();
        _connectionResolutionService = new ConnectionResolutionService();
        _approvalService = new ApprovalService();
        _fileSystemService = new Infrastructure.FileSystem.FileSystemService();
        _logStore = new SessionLogStore();
        _cursorCaptureService = new CursorCaptureService();
        _localIdentity = _deviceIdentityStore.LoadOrCreate();
        LoadPersistedPreferences();

        _tcpManager.OnSessionConnected += OnSessionConnected;
        _tcpManager.OnSessionDisconnected += OnSessionDisconnected;
        _tcpManager.StartListening(9999);

        UpsertDevice(CreateLocalDeviceModel());
        try
        {
            _duplicateCheckResult = _localDiscoveryService
                .CheckDuplicateAsync(_localIdentity, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealRemoteSessionService] Duplicate check failed: {ex.Message}");
        }

        _localDiscoveryService.DevicesUpdated += HandleDiscoveredDevicesUpdated;
        _localDiscoveryService.Start(_localIdentity);
        MergeDiscoveredDevices();
    }

    public event Action<SessionLogEntry>? SessionLogAdded;
    public event Action? DevicesChanged;
    public event Action? RecentConnectionsChanged;
    public event Action<ConnectionSnapshot>? SessionSnapshotChanged;
    public event Action<string>? FileSystemListReceived;
    public event Action<double>? FileTransferProgressChanged;

    public IReadOnlyList<DeviceModel> GetDevices() => _devices.Values.OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<RecentConnectionEntry> GetRecentConnections() => _recentConnections.ToArray();

    public void ToggleFavorite(string internalGuid)
    {
        if (string.IsNullOrWhiteSpace(internalGuid))
        {
            return;
        }

        if (_favoriteDeviceInternalGuids.Contains(internalGuid))
        {
            _favoriteDeviceInternalGuids.Remove(internalGuid);
        }
        else
        {
            _favoriteDeviceInternalGuids.Add(internalGuid);
        }

        PersistPreferences();

        if (_devices.TryGetValue(internalGuid, out DeviceModel? device))
        {
            device.IsFavorite = _favoriteDeviceInternalGuids.Contains(internalGuid);
        }

        DevicesChanged?.Invoke();
    }

    public DuplicateCheckResult GetDuplicateCheckResult() => _duplicateCheckResult;

    public DeviceResolutionResult ResolveDevice(string identifier) => _connectionResolutionService.Resolve(identifier, GetDevices());

    public IReadOnlyList<CaptureDisplayOption> GetCaptureDisplays() => _captureService.GetAvailableDisplays();

    public IReadOnlyList<CaptureDisplayOption> GetViewerDisplays() => _captureService.GetAvailableDisplays();

    public IReadOnlyList<CaptureRateOption> GetCaptureRates() =>
    [
        new CaptureRateOption { Label = "15 FPS", FramesPerSecond = 15 },
        new CaptureRateOption { Label = "30 FPS", FramesPerSecond = 30 }
    ];

    public IReadOnlyList<CompressionOption> GetCompressionOptions() =>
    [
        new CompressionOption { Label = "Raw BGRA", EncodingMode = RawBgraEncoding, Quality = 100 },
        new CompressionOption { Label = "JPEG 85", EncodingMode = JpegEncoding, Quality = 85 },
        new CompressionOption { Label = "JPEG 65", EncodingMode = JpegEncoding, Quality = 65 }
    ];

    private void HandleClipboardFiles(ReadOnlyMemory<byte> payload)
    {
        try
        {
            string json = Encoding.UTF8.GetString(payload.Span);
            var files = JsonSerializer.Deserialize<List<Models.ClipboardFileMeta>>(json);
            if (files != null)
            {
                _remoteClipboardFiles = files;
                PublishLog("Remote Clipboard", $"원격지로부터 {files.Count}개의 파일 클립보드 정보를 수신했습니다. 'Paste' 버튼으로 다운로드 가능합니다.", "Clipboard Inbound");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Session] Clipboard File Handle Error: {ex.Message}");
        }
    }

    private ulong CalculateFilesHash(string[] files)
    {
        ulong hash = 17;
        foreach (var f in files)
        {
            hash = hash * 23 + (ulong)f.GetHashCode();
        }
        return hash;
    }

    private void HandleResolutionChange(ReadOnlyMemory<byte> payload)
    {
        try
        {
            int width = BitConverter.ToInt32(payload.Span.Slice(0, 4));
            int height = BitConverter.ToInt32(payload.Span.Slice(4, 4));
            PublishLog("Resolution Change Requested", $"Viewer requested resolution: {width}x{height}", "Display");
            
            bool success = _inputService.ChangeDisplayResolution(width, height);
            PublishLog("Resolution Change result", success ? "Success: 원격지 해상도가 변경되었습니다." : "Failed: 지원하지 않는 해상도이거나 권한이 부족합니다.", "Display");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Session] Resolution Change Error: {ex.Message}");
        }
    }

    public void RequestResolutionChange(int width, int height)
    {
        if (_currentSession == null || _isDisposed) return;

        byte[] packet = new byte[9];
        packet[0] = ResolutionChangePacketType;
        BitConverter.TryWriteBytes(packet.AsSpan(1, 4), width);
        BitConverter.TryWriteBytes(packet.AsSpan(5, 4), height);
        _ = _currentSession.SendAsync(packet);
        PublishLog("Resolution Request Sent", $"Requested remote resolution: {width}x{height}", "View Profile");
    }

    public void SetAutoReconnect(bool enabled)
    {
        _autoReconnectEnabled = enabled;
        PublishLog("Auto Reconnect", enabled ? "Auto reconnect enabled." : "Auto reconnect disabled.", "Reconnect");
    }

    public void SetClipboardSyncEnabled(bool enabled)
    {
        _isClipboardSyncEnabled = enabled;
        PublishLog("Clipboard Sync", enabled ? "Clipboard text sync enabled." : "Clipboard text sync disabled.", "Clipboard / Text / State");

        if (!enabled)
        {
            StopClipboardSyncLoop();
            return;
        }

        TryStartClipboardSyncLoop();
    }

    public void SetLocalDriveRedirectEnabled(bool enabled)
    {
        _fileSystemService.SetDriveRedirectEnabled(enabled);
        PublishLog("Drive Redirection", enabled ? "Local drives available to remote side." : "Local drives hidden from remote side.", "FileSystem");
    }

    public void RequestFileSystemList(string path)
    {
        if (_currentSession == null || _isDisposed)
        {
            return;
        }

        if (!_fileSystemService.IsDriveRedirectEnabled)
        {
            PublishLog("FS List Blocked", "Drive redirection is disabled. File system request was not sent.", "FileSystem");
            return;
        }

        byte[] pathBytes = Encoding.UTF8.GetBytes(path);
        byte[] packet = new byte[1 + 4 + pathBytes.Length];
        packet[0] = FileSystemListRequestPacketType;
        BitConverter.TryWriteBytes(packet.AsSpan(1, 4), pathBytes.Length);
        pathBytes.CopyTo(packet.AsSpan(5));

        _ = _currentSession.SendAsync(packet);
        PublishLog("FS List Request Sent", $"Requested directory listing for: {path}", "FileSystem");
    }

    public void SetCaptureDisplay(string displayId)
    {
        _selectedCaptureDisplayId = displayId;
        var selectedDisplay = _captureService
            .GetAvailableDisplays()
            .FirstOrDefault(display => display.DisplayId == displayId);

        if (selectedDisplay is null)
        {
            return;
        }

        _captureService.SelectOutput(selectedDisplay.OutputIndex);
        RefreshViewerWindowStatus();
        PublishLog("Capture Display Selected", $"{selectedDisplay.Label} selected for capture.", "Display");
    }

    public void SetViewerDisplay(string? displayId)
    {
        _selectedViewerDisplayId = string.IsNullOrWhiteSpace(displayId) ? null : displayId;
        var selectedBounds = GetSelectedViewerBounds();
        Application.Current?.Dispatcher.Invoke(() => _rdpWindow?.SetPreferredViewerBounds(selectedBounds));
        RefreshViewerWindowStatus();

        if (selectedBounds is null)
        {
            PublishLog("Viewer Display Selected", "Viewer display follows automatic placement.", "Viewer");
            return;
        }

        var selectedViewerDisplay = _captureService
            .GetAvailableDisplays()
            .FirstOrDefault(display => display.DisplayId == _selectedViewerDisplayId);
        PublishLog("Viewer Display Selected", $"Viewer display set to {selectedViewerDisplay?.Label ?? _selectedViewerDisplayId}.", "Viewer");
    }

    public void SetKeepViewerOnSafeDisplay(bool enabled)
    {
        _keepViewerOnSafeDisplay = enabled;
        Application.Current?.Dispatcher.Invoke(() => _rdpWindow?.SetKeepOnSafeDisplay(enabled));
        PublishLog("Viewer Placement", enabled ? "Viewer will stay off the captured display." : "Viewer can be placed on the captured display.", "Viewer");
    }

    public void SetCaptureRate(int framesPerSecond)
    {
        _captureService.SetCaptureRate(framesPerSecond);
        PublishLog("Capture Rate Updated", $"Capture rate set to {framesPerSecond} FPS.", "Capture");
    }

    public void SetCompression(byte encodingMode, long quality)
    {
        _compressionEncodingMode = encodingMode;
        _jpegQuality = Math.Clamp(quality, 1, 100);
        string label = encodingMode == JpegEncoding ? $"JPEG {_jpegQuality}" : "Raw BGRA";
        RefreshViewerWindowStatus();
        PublishLog("Compression Updated", $"Transfer encoding set to {label}.", "Transport");
    }

    public IReadOnlyList<SessionLogEntry> GetSeedLogs()
    {
        // 영구 저장된 로그를 먼저 가져오고 기본 로그 추가
        var savedLogs = _logStore.LoadAllLogs();
        if (savedLogs.Count > 0)
        {
            return savedLogs;
        }

        List<SessionLogEntry> seedLogs =
        [
            new SessionLogEntry
            {
                Timestamp = DateTime.Now,
                Title = "Engine Started",
                Message = "RealRemoteSessionService initialized with Network, Capture, Discovery, and Input engines.",
                Meta = "System Ready"
            },
            new SessionLogEntry
            {
                Timestamp = DateTime.Now,
                Title = "Local Device Ready",
                Message = $"{_localIdentity.DeviceName} / {_localIdentity.DeviceCode}",
                Meta = $"GUID: {_localIdentity.InternalGuid[..8]}"
            },
            _duplicateCheckResult.IsDuplicate
                ? new SessionLogEntry
                {
                    Timestamp = DateTime.Now,
                    Title = "Duplicate Identifier Warning",
                    Message = "같은 로컬 네트워크에서 중복 장치 이름 또는 장치 번호가 감지되었습니다.",
                    Meta = $"Conflicts: {_duplicateCheckResult.Conflicts.Count}"
                }
                : new SessionLogEntry
                {
                    Timestamp = DateTime.Now,
                    Title = "Duplicate Identifier Check",
                    Message = "로컬 네트워크 기준 중복 장치 식별자가 발견되지 않았습니다.",
                    Meta = "Broadcast probe complete"
                }
        ];

        _logStore.ReplaceAllLogs(seedLogs);
        return seedLogs.OrderByDescending(log => log.Timestamp).ToArray();
    }

    public ConnectionSnapshot CreateQuickConnection(DeviceModel? device, string approvalMode)
    {
        _lastRequestedDevice = device;
        _lastRequestedIdentifier = device?.DeviceCode ?? device?.DeviceId;
        _lastApprovalMode = ApprovalService.NormalizePolicy(approvalMode);
        _userInitiatedDisconnect = false;

        if (_lastApprovalMode == "Pre-approved device" && !IsPreApprovedDevice(device))
        {
            ConnectionSnapshot deniedSnapshot = CreateApprovalDeniedSnapshot(
                "Approval denied",
                "Pre-approved device policy rejected this connection because the target is not marked as trusted.",
                "Approval denied");
            PublishLog("Connection Denied", $"{device?.Name ?? "Unknown Device"} is not in the pre-approved device set.", "Trusted Approval");
            PublishSnapshot(deniedSnapshot);
            return deniedSnapshot;
        }

        DeviceEndpoint? selectedEndpoint = device?.Endpoints.FirstOrDefault(endpoint => endpoint.Scope == DeviceEndpointScope.Local)
            ?? device?.Endpoints.FirstOrDefault();
        string targetAddress = selectedEndpoint?.Address ?? IPAddress.Loopback.ToString();
        int targetPort = selectedEndpoint?.Port ?? 9999;
        _lastRequestedAddress = targetAddress;
        _lastRequestedPort = targetPort;

        _viewerClosedByUser = false;
        _ = ConnectToTargetAsync(targetAddress, targetPort, device, _lastApprovalMode, isReconnect: false, CancellationToken.None);
        ConnectionSnapshot pendingSnapshot = CreatePendingApprovalSnapshot(targetAddress, targetPort, _lastApprovalMode);
        PublishSnapshot(pendingSnapshot);
        return pendingSnapshot;
    }

    public ConnectionSnapshot CreateSupportSession(DeviceModel? device)
    {
        return CreateQuickConnection(device, "Support request");
    }

    public void DisconnectCurrentSession()
    {
        _userInitiatedDisconnect = true;
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;
        _captureCts?.Cancel();
        _captureCts?.Dispose();
        _captureCts = null;
        StopClipboardSyncLoop();
        Application.Current?.Dispatcher.Invoke(CloseViewerWindowInternal);
        TcpSession? sessionToClose = _currentSession;
        _currentSession = null;
        sessionToClose?.Dispose();
        PublishSnapshot(new ConnectionSnapshot
        {
            SessionTitle = "No active session",
            SessionDetail = "세션이 종료되었습니다. 다른 장치를 선택하거나 Quick Connect를 다시 시작할 수 있습니다.",
            Status = "Idle",
            QualityPercent = 0,
            QualitySummary = "No active connection"
        });
    }

    public SessionLogEntry CreateLog(string title, string message, string meta)
    {
        var entry = new SessionLogEntry
        {
            Timestamp = DateTime.Now,
            Title = title,
            Message = message,
            Meta = meta
        };
        _logStore.SaveLog(entry);
        return entry;
    }

    public async Task StartRelayHostAsync(string relayIp, int relayPort, string code)
    {
        PublishLog("Relay Host", $"릴레이 서버({relayIp})를 통해 세션을 대기합니다. 코드: {code}", "Relay Setup");
        try
        {
            TcpClient client = new TcpClient();
            await client.ConnectAsync(relayIp, relayPort).ConfigureAwait(false);
            var session = new TcpSession(client, client.GetStream());
            byte[] codeBytes = Encoding.UTF8.GetBytes(code);
            byte[] packet = new byte[codeBytes.Length + 1];
            packet[0] = RelayHostPacketType;
            codeBytes.CopyTo(packet, 1);
            await session.SendAsync(packet).ConfigureAwait(false);
            _currentSession = session;
            _currentSession.OnMessageReceived += (s, data) => 
            {
                if (data.Length >= 2 && data.Span[0] == RelayConnectPacketType && data.Span[1] == 0x02)
                {
                    PublishLog("Relay Session Active", "릴레이 서버를 통해 상대방이 연결되었습니다.", "Relay Connect");
                    StartHostSessionInternal(s);
                }
                else HandleSessionMessage(s, data);
            };
            _currentSession.OnDisconnected += OnSessionDisconnected;
            _currentSession.StartReceiving();
        }
        catch (Exception ex)
        {
            PublishLog("Relay Error", $"릴레이 연결 실패: {ex.Message}", "Network Error");
            throw;
        }
    }

    public async Task ConnectViaRelayAsync(string relayIp, int relayPort, string code)
    {
        PublishLog("Relay Connect", $"릴레이 서버({relayIp})를 통해 원격지에 접속합니다. 코드: {code}", "Relay Setup");
        try
        {
            TcpClient client = new TcpClient();
            await client.ConnectAsync(relayIp, relayPort).ConfigureAwait(false);
            var session = new TcpSession(client, client.GetStream());
            byte[] codeBytes = Encoding.UTF8.GetBytes(code);
            byte[] packet = new byte[codeBytes.Length + 1];
            packet[0] = RelayConnectPacketType;
            codeBytes.CopyTo(packet, 1);
            await session.SendAsync(packet).ConfigureAwait(false);
            _currentSession = session;
            _currentSession.OnDisconnected += OnSessionDisconnected;
            _currentSession.OnMessageReceived += HandleSessionMessage;
            _currentSession.StartReceiving();
            PublishSnapshot(new ConnectionSnapshot { Status = "Connecting", SessionTitle = "Relay Connecting", SessionDetail = "릴레이 서버를 통한 세션 협상 중...", QualityPercent = 0, QualitySummary = "Establishing tunnel" });
            _ = SendConnectionSetupAsync(session);
        }
        catch (Exception ex)
        {
            PublishLog("Relay Error", $"릴레이 연결 실패: {ex.Message}", "Network Error");
            throw;
        }
    }

    private void StartHostSessionInternal(TcpSession session)
    {
        session.OnMessageReceived -= HandleSessionMessage;
        session.OnMessageReceived += HandleSessionMessage;
        PublishSnapshot(new ConnectionSnapshot { Status = "Pending", SessionTitle = "Relay Host Waiting", SessionDetail = "상대방의 승인을 대기 중입니다.", QualityPercent = 0, QualitySummary = "Relay session pending" });
    }

    private void HandleSessionMessage(TcpSession session, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0) return;
        byte type = payload.Span[0];
        ReadOnlyMemory<byte> data = payload.Slice(1);
        switch (type)
        {
            case ScreenFramePacketType: _receivedFrameCount++; _ = OnFrameCapturedAsync(session, data, 1920, 1080); break;
            case FileChunkPacketType: HandleFileChunkReceived(data); break;
            case ClipboardTextPacketType: HandleIncomingClipboardText(data); break;
            case FileMetaPacketType: HandleIncomingFileMeta(data); break;
            case FileDownloadRequestPacketType: _ = HandleRemoteDownloadRequestAsync(Encoding.UTF8.GetString(data.Span), session); break;
            case FileSystemListRequestPacketType: HandleFileSystemListRequest(data, session); break;
            case FileSystemListResponsePacketType: FileSystemListReceived?.Invoke(Encoding.UTF8.GetString(data.Span)); break;
            case ConnectionSetupPacketType: _ = HandleIncomingConnectionSetupAsync(session, data); break;
            case ConnectionSetupResponsePacketType: HandleIncomingConnectionSetupResponse(session, data.Span[0]); break;
            case PingPacketType: _ = session.SendAsync(Combine(new byte[] { PongPacketType }, data.Span)); break;
            case PongPacketType: HandleIncomingPong(data); break;
        }
    }

    public void SetDownloadPath(string path)
    {
        _customDownloadPath = path;
        PublishLog("Download Path Updated", $"다운로드 경로가 변경되었습니다: {path}", "Settings");
    }

    public string GetDownloadPath() => string.IsNullOrWhiteSpace(_customDownloadPath) ? DownloadFolderPath : _customDownloadPath;

    public void CancelCurrentFileTransfer()
    {
        if (_fileTransferCts != null)
        {
            _fileTransferCts.Cancel();
            _fileTransferCts.Dispose();
            _fileTransferCts = null;
            FileTransferProgressChanged?.Invoke(0.0);
        }
    }

    public Task UploadFileAsync(string filePath)
    {
        if (_currentSession == null || _isDisposed)
        {
            return Task.CompletedTask;
        }

        return _fileTransferService.SendFileWithMetaAsync(filePath, _currentSession, FileMetaPacketType, FileChunkPacketType, default);
    }

    public Task DownloadFileAsync(string remotePath)
    {
        if (_currentSession == null || _isDisposed)
        {
            return Task.CompletedTask;
        }

        byte[] pathBytes = Encoding.UTF8.GetBytes(remotePath);
        byte[] packet = new byte[1 + 4 + pathBytes.Length];
        packet[0] = FileDownloadRequestPacketType;
        BitConverter.TryWriteBytes(packet.AsSpan(1, 4), pathBytes.Length);
        pathBytes.CopyTo(packet.AsSpan(5));

        return _currentSession.SendAsync(packet);
    }

    public void LockRemoteSession()
    {
        if (_currentSession == null || _isDisposed) return;
        byte[] packet = [LockSessionPacketType];
        _ = _currentSession.SendAsync(packet);
        PublishLog("Lock Request Sent", "Requested remote workstation to lock.", "Security");
    }

    public void SetRemoteInputBlocked(bool blocked)
    {
        if (_currentSession == null || _isDisposed) return;
        byte[] packet = [BlockInputPacketType, (byte)(blocked ? 1 : 0)];
        _ = _currentSession.SendAsync(packet);
        PublishLog("Input Block Request Sent", $"Requested remote input to be {(blocked ? "blocked" : "unblocked")}.", "Security");
    }

    public void SetCtrlCopyEnabled(bool enabled)
    {
        _isCtrlCopyEnabled = enabled;
        PublishLog("Ctrl+C/V File Sync", enabled ? "Enabled." : "Disabled.", "Clipboard");
    }

    public async Task DownloadClipboardFilesAsync()
    {
        if (_remoteClipboardFiles.Count == 0)
        {
            PublishLog("Paste Failed", "No files found in remote clipboard.", "Clipboard");
            return;
        }

        PublishLog("Paste Started", $"{_remoteClipboardFiles.Count}개의 원격 클립보드 파일 다운로드를 시작합니다.", "Clipboard Inbound");
        foreach (var file in _remoteClipboardFiles)
        {
            await DownloadFileAsync(file.FullPath).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Application.Current?.Dispatcher.Invoke(CloseViewerWindowInternal);
        _captureCts?.Cancel();
        StopClipboardSyncLoop();
        _tcpManager.Dispose();
        _localDiscoveryService.DevicesUpdated -= HandleDiscoveredDevicesUpdated;
        _localDiscoveryService.Dispose();
        _captureService.Dispose();
        _inputService.Dispose();
        _captureCts?.Dispose();
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _clipboardSyncCts?.Dispose();
    }

    private async Task ConnectToTargetAsync(
        string ip,
        int port,
        DeviceModel? device,
        string approvalMode,
        bool isReconnect,
        CancellationToken cancellationToken)
    {
        try
        {
            _lastRequestedAddress = ip;
            _lastRequestedPort = port;
            string normalizedApprovalMode = ApprovalService.NormalizePolicy(approvalMode);
            string approvalLogCategory = ApprovalService.GetApprovalLogCategory(normalizedApprovalMode);
            if (isReconnect && ApprovalService.RequiresInteractiveApproval(normalizedApprovalMode))
            {
                PublishLog("Reconnect Approval Bypass", $"{normalizedApprovalMode} 정책 재연결은 이전 승인 세션으로 간주하여 자동 승인됩니다.", approvalLogCategory);
            }

            ApprovalDecision approvalDecision = await _approvalService
                .RequestApprovalAsync(device, normalizedApprovalMode, isReconnect, cancellationToken)
                .ConfigureAwait(false);
            if (approvalDecision == ApprovalDecision.Denied)
            {
                ConnectionSnapshot deniedSnapshot = CreateApprovalDeniedSnapshot(
                    "Approval denied",
                    $"Connection to {device?.Name ?? $"{ip}:{port}"} was denied by policy {normalizedApprovalMode}.",
                    "Approval denied");
                PublishLog("Connection Denied", $"Connection to {device?.Name ?? $"{ip}:{port}"} was denied by policy {normalizedApprovalMode}.", approvalLogCategory);
                PublishSnapshot(deniedSnapshot);
                return;
            }

            if (approvalDecision == ApprovalDecision.Cancelled)
            {
                ConnectionSnapshot cancelledSnapshot = new()
                {
                    SessionTitle = "Approval cancelled",
                    SessionDetail = $"Connection to {device?.Name ?? $"{ip}:{port}"} was cancelled before the session was created.",
                    Status = "Cancelled",
                    QualityPercent = 0,
                    QualitySummary = "Approval cancelled"
                };
                PublishLog("Connection Cancelled", $"Connection to {device?.Name ?? $"{ip}:{port}"} was cancelled by the operator.", approvalLogCategory);
                PublishSnapshot(cancelledSnapshot);
                return;
            }

            if (approvalDecision == ApprovalDecision.TimedOut)
            {
                ConnectionSnapshot timedOutSnapshot = new()
                {
                    SessionTitle = "Approval timed out",
                    SessionDetail = $"Connection to {device?.Name ?? $"{ip}:{port}"} timed out while waiting for policy {normalizedApprovalMode}.",
                    Status = "Timed Out",
                    QualityPercent = 0,
                    QualitySummary = "Approval timed out"
                };
                PublishLog("Connection Timed Out", $"Connection to {device?.Name ?? $"{ip}:{port}"} timed out under policy {normalizedApprovalMode}.", approvalLogCategory);
                PublishSnapshot(timedOutSnapshot);
                return;
            }

            string? expectedThumbprint = GetExpectedTrustedThumbprint(device);
            var session = await _tcpManager.ConnectAsync(ip, port, expectedThumbprint, cancellationToken).ConfigureAwait(false);
            PublishLog("Connection Established", $"Connected to {ip}:{port}. Session ID: {session.SessionId[..8]}", "TCP Connected");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealRemoteSessionService] Connection to {ip}:{port} failed: {ex.Message}");
            PublishLog("Connection Failed", $"Unable to connect to {ip}:{port}. {ex.Message}", "TCP Error");
            PublishSnapshot(new ConnectionSnapshot
            {
                SessionTitle = isReconnect ? "Reconnect pending" : "Connection failed",
                SessionDetail = isReconnect
                    ? $"{device?.Name ?? $"{ip}:{port}"} 장치 재연결이 실패했습니다. 다음 시도를 준비합니다. {ex.Message}"
                    : $"Unable to connect to {device?.Name ?? $"{ip}:{port}"}. {ex.Message}",
                Status = isReconnect ? "Reconnecting" : "Failed",
                QualityPercent = 0,
                QualitySummary = isReconnect ? "Reconnect attempt failed" : "TCP error"
            });
        }
    }

    private void OnSessionConnected(TcpSession session)
    {
        _currentSession = session;
        _receivedFrameCount = 0;
        _viewerClosedByUser = false;
        _userInitiatedDisconnect = false;
        _reconnectCts?.Cancel();
        session.OnMessageReceived += HandleIncomingMessage;
        PublishLog("Session Connected", $"Session {session.SessionId[..8]} is active.", "Network");
        
        if (session.Stream is SslStream sslStream && _lastRequestedDevice != null)
        {
            var cert = sslStream.RemoteCertificate as X509Certificate2;
            if (cert != null)
            {
                string thumbprint = cert.Thumbprint;
                var existingMeta = _devicePreferenceStore.Load().DeviceMetadata;
                if (existingMeta.TryGetValue(_lastRequestedDevice.InternalGuid, out var meta) && !string.IsNullOrEmpty(meta.TrustedThumbprint))
                {
                    PublishLog("Security Verified", "인증서 지문이 확인되었습니다.", "Security");
                }
                else
                {
                    _devicePreferenceStore.UpdateDeviceTrustedThumbprint(_lastRequestedDevice.InternalGuid, thumbprint);
                    if (_deviceMetadataMap.TryGetValue(_lastRequestedDevice.InternalGuid, out DevicePreferenceStore.DeviceMetadata? currentMetadata))
                    {
                        _deviceMetadataMap[_lastRequestedDevice.InternalGuid] = currentMetadata with { TrustedThumbprint = thumbprint };
                    }
                    else
                    {
                        _deviceMetadataMap[_lastRequestedDevice.InternalGuid] = new DevicePreferenceStore.DeviceMetadata(null, null, thumbprint);
                    }
                    PublishLog("Security Information", "새로운 장치의 인증서 지문을 신뢰할 수 있는 것으로 등록했습니다.", "Security");
                }
            }
        }

        PublishSnapshot(new ConnectionSnapshot
        {
            SessionTitle = $"Connected to {_lastRequestedDevice?.Name ?? session.SessionId[..8]}",
            SessionDetail = "승인과 네트워크 연결이 완료되어 원격 세션이 활성화되었습니다.",
            Status = "Connected",
            QualityPercent = 85,
            QualitySummary = "Session active"
        });
        PersistLastKnownConnection(_lastRequestedDevice);
        RecordRecentConnection(_lastRequestedDevice);
        TryStartClipboardSyncLoop();
        TryStartCursorSyncLoop();

        if (_captureService.Initialize())
        {
            _captureCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!_captureCts.Token.IsCancellationRequested && _currentSession != null)
                {
                    try
                    {
                        await _captureService.CaptureLoopAsync(
                            (frameData, width, height) => OnFrameCapturedAsync(session, frameData, width, height),
                            _captureCts.Token);
                    }
                    catch (Exception ex)
                    {
                        PublishLog("Capture Interrupted", $"Screen capture loop stopped: {ex.Message}. Retrying...", "Capture");
                        await Task.Delay(2000, _captureCts.Token);
                        if (!_captureService.Initialize()) break;
                    }
                }
            }, _captureCts.Token);
            PublishLog("Capture Started", "Screen capture loop started with auto-recovery.", "Capture");
        }
        else
        {
            PublishLog("Capture Initialization Failed", "Screen capture could not be initialized.", "Capture Error");
        }
    }

    private async Task OnFrameCapturedAsync(TcpSession session, ReadOnlyMemory<byte> frameData, int width, int height)
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            byte[] encodedFrame = EncodeFrame(frameData, width, height, _compressionEncodingMode, _jpegQuality);
            byte[] packet = ArrayPool<byte>.Shared.Rent(encodedFrame.Length + 10);
            try
            {
                packet[0] = ScreenFramePacketType;
                BitConverter.TryWriteBytes(packet.AsSpan(1, 4), width);
                BitConverter.TryWriteBytes(packet.AsSpan(5, 4), height);
                packet[9] = _compressionEncodingMode;
                encodedFrame.CopyTo(packet.AsSpan(10));

                await session.SendAsync(new ReadOnlyMemory<byte>(packet, 0, encodedFrame.Length + 10)).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealRemoteSessionService] Frame send error: {ex.Message}");
        }
    }

    private void HandleIncomingMessage(TcpSession session, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        byte packetType = payload.Span[0];

        if (packetType == ScreenFramePacketType && payload.Length > 10)
        {
            if (_viewerClosedByUser)
            {
                return;
            }

            int width = BitConverter.ToInt32(payload.Span.Slice(1, 4));
            int height = BitConverter.ToInt32(payload.Span.Slice(5, 4));
            byte encodingMode = payload.Span[9];
            ReadOnlyMemory<byte> frameData = payload.Slice(10);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (_rdpWindow == null || !_rdpWindow.IsLoaded)
                {
                    _rdpWindow = new RemoteDesktopWindow(
                        _captureService.CapturedOutputBounds,
                        GetSelectedViewerBounds(),
                        _keepViewerOnSafeDisplay,
                        GetSelectedCaptureDisplayLabel(),
                        GetSelectedViewerDisplayLabel(),
                        GetCompressionLabel());
                    _rdpWindow.OnMouseInputCaptured += HandleClientMouseInput;
                    _rdpWindow.OnKeyboardInputCaptured += HandleClientKeyboardInput;
                    _rdpWindow.OnResolutionRequested += (w, h) => RequestResolutionChange(w, h);
                    _rdpWindow.Closed += HandleViewerWindowClosed;
                    _rdpWindow.Show();
                    PublishLog("Remote Window Opened", "Remote desktop window created.", "Viewer");
                }

                _receivedFrameCount++;
                if (_receivedFrameCount == 1 || _receivedFrameCount % 120 == 0)
                {
                    PublishLog("Frame Received", $"Received frame #{_receivedFrameCount} at {width}x{height}.", "Viewer");
                }

                _rdpWindow.RenderFrame(frameData, width, height, encodingMode);
            });
        }
        else if (packetType == MouseInputPacketType && payload.Length == 13)
        {
            int x = BitConverter.ToInt32(payload.Span.Slice(1, 4));
            int y = BitConverter.ToInt32(payload.Span.Slice(5, 4));
            uint flags = BitConverter.ToUInt32(payload.Span.Slice(9, 4));
            _inputService.InjectMouse(x, y, (InputInjectionService.MouseEventFlags)flags);
        }
        else if (packetType == KeyboardInputPacketType && payload.Length == 7)
        {
            ushort virtualKey = BitConverter.ToUInt16(payload.Span.Slice(1, 2));
            uint flags = BitConverter.ToUInt32(payload.Span.Slice(3, 4));
            _inputService.InjectKeyboard(virtualKey, (InputInjectionService.KeyEventFlags)flags);
        }
        else if (packetType == FileChunkPacketType)
        {
            ReadOnlyMemory<byte> chunkData = payload.Slice(1);
            string destination = string.IsNullOrWhiteSpace(_receivingFilePath)
                ? GetFallbackDownloadPath()
                : _receivingFilePath;
            _ = _fileTransferService.ReceiveFileChunkAsync(destination, chunkData, default);
            
            _receivedBytes += chunkData.Length;
            if (_receivingFileSize > 0)
            {
                double progress = (double)_receivedBytes * 100 / _receivingFileSize;
                FileTransferProgressChanged?.Invoke(progress);
                if (_receivedBytes >= _receivingFileSize)
                {
                    PublishLog("File Transfer Completed", $"파일 수신이 완료되었습니다: {destination}", "File Inbound");
                }
            }
        }
        else if (packetType == FileMetaPacketType && payload.Length > 13) // Header(1) + NameLen(4) + Name(?) + Size(8)
        {
            int nameLength = BitConverter.ToInt32(payload.Span.Slice(1, 4));
            string fileName = Encoding.UTF8.GetString(payload.Span.Slice(5, nameLength));
            _receivingFileSize = BitConverter.ToInt64(payload.Span.Slice(5 + nameLength, 8));
            _receivedBytes = 0;

            string sanitizedFileName = SanitizeFileName(fileName);
            _receivingFilePath = ResolveUniqueDownloadPath(sanitizedFileName);
            
            PublishLog("File Transfer Started", $"Receiving file: {sanitizedFileName} ({_receivingFileSize / 1024} KB)", $"File Inbound / Destination={_receivingFilePath}");
            FileTransferProgressChanged?.Invoke(0.0);
        }
        else if (packetType == FileDownloadRequestPacketType && payload.Length > 5)
        {
            int pathLength = BitConverter.ToInt32(payload.Span.Slice(1, 4));
            string remotePath = Encoding.UTF8.GetString(payload.Span.Slice(5, pathLength));
            _ = HandleRemoteDownloadRequestAsync(remotePath, session);
        }
        else if (packetType == CursorShapePacketType && payload.Length >= 2)
        {
            bool isVisible = payload.Span[1] != 0;
            string cursorName = payload.Length > 2 ? Encoding.UTF8.GetString(payload.Span.Slice(2)) : "Arrow";
            Application.Current?.Dispatcher.Invoke(() => _rdpWindow?.UpdateRemoteCursor(cursorName, isVisible));
        }
        else if (packetType == FileSystemListRequestPacketType && payload.Length >= 5)
        {
            int pathLength = BitConverter.ToInt32(payload.Span.Slice(1, 4));
            string path = pathLength > 0 ? Encoding.UTF8.GetString(payload.Span.Slice(5, pathLength)) : string.Empty;
            string json = _fileSystemService.GetDirectoryListingJson(path);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            byte[] responsePacket = new byte[1 + 4 + jsonBytes.Length];
            responsePacket[0] = FileSystemListResponsePacketType;
            BitConverter.TryWriteBytes(responsePacket.AsSpan(1, 4), jsonBytes.Length);
            jsonBytes.CopyTo(responsePacket.AsSpan(5));
            _ = session.SendAsync(responsePacket);
            PublishLog("FS List Requested", $"Remote side requested listing for: {path}", "FileSystem");
        }
        else if (packetType == FileSystemListResponsePacketType && payload.Length >= 5)
        {
            int jsonLength = BitConverter.ToInt32(payload.Span.Slice(1, 4));
            string json = Encoding.UTF8.GetString(payload.Span.Slice(5, jsonLength));
            FileSystemListReceived?.Invoke(json);

            Debug.WriteLine($"[RealRemoteSessionService] FS List Response: {json[..Math.Min(json.Length, 100)]}...");

            try
            {
                FileSystemListResponse? response = JsonSerializer.Deserialize<FileSystemListResponse>(json);
                if (response?.IsSuccess == true)
                {
                    PublishLog("FS List Received", $"Redirected drive data received from client. Entries: {response.Entries.Count}", "FileSystem");
                }
                else
                {
                    PublishLog("FS List Failed", response?.ErrorMessage ?? "Unknown file system error.", "FileSystem");
                }
            }
            catch (Exception ex)
            {
                PublishLog("FS List Failed", $"Unable to parse file system response. {ex.Message}", "FileSystem");
            }
        }
        else if (packetType == ClipboardTextPacketType)
        {
            HandleIncomingClipboardText(payload.Slice(1));
        }
        else if (packetType == ClipboardImagePacketType)
        {
            HandleIncomingClipboardImage(payload.Slice(1));
        }
        else if (packetType == LockSessionPacketType)
        {
            bool success = _inputService.LockSession();
            PublishLog("Remote Session Locked", success ? "Session locked successfully." : "Failed to lock session.", "Security");
        }
        else if (packetType == BlockInputPacketType && payload.Length >= 2)
        {
            bool blocked = payload.Span[1] != 0;
            bool success = _inputService.SetInputBlock(blocked);
            PublishLog("Remote Input Blocking", success ? $"Input {(blocked ? "blocked" : "unblocked")} successfully." : "Failed to change input block state.", "Security");
        }
        else if (packetType == ClipboardFilesPacketType)
        {
            HandleClipboardFiles(payload.Slice(1));
        }
        else if (packetType == ResolutionChangePacketType && payload.Length >= 9)
        {
            HandleResolutionChange(payload.Slice(1));
        }
    }

    private void HandleClientMouseInput(int x, int y, InputInjectionService.MouseEventFlags flags)
    {
        if (_currentSession == null || _isDisposed)
        {
            return;
        }

        byte[] inputPacket = new byte[13];
        inputPacket[0] = MouseInputPacketType;
        BitConverter.TryWriteBytes(inputPacket.AsSpan(1, 4), x);
        BitConverter.TryWriteBytes(inputPacket.AsSpan(5, 4), y);
        BitConverter.TryWriteBytes(inputPacket.AsSpan(9, 4), (uint)flags);
        _ = _currentSession.SendAsync(inputPacket).ConfigureAwait(false);
    }

    private void HandleClientKeyboardInput(ushort virtualKey, InputInjectionService.KeyEventFlags flags)
    {
        if (_currentSession == null || _isDisposed)
        {
            return;
        }

        byte[] inputPacket = new byte[7];
        inputPacket[0] = KeyboardInputPacketType;
        BitConverter.TryWriteBytes(inputPacket.AsSpan(1, 2), virtualKey);
        BitConverter.TryWriteBytes(inputPacket.AsSpan(3, 4), (uint)flags);
        _ = _currentSession.SendAsync(inputPacket).ConfigureAwait(false);
    }

    private void OnSessionDisconnected(TcpSession session, Exception? ex)
    {
        if (ReferenceEquals(_currentSession, session))
        {
            _currentSession = null;
        }

        _captureCts?.Cancel();
        _captureCts?.Dispose();
        _captureCts = null;
        StopClipboardSyncLoop();
        Application.Current?.Dispatcher.Invoke(CloseViewerWindowInternal);
        PublishLog(
            "Session Disconnected",
            ex is null ? $"Session {session.SessionId[..8]} closed." : $"Session {session.SessionId[..8]} closed with error: {ex.Message}",
            ex is null ? "Network Closed" : "Network Error");
        PublishSnapshot(new ConnectionSnapshot
        {
            SessionTitle = ex is null ? "Session closed" : "Session disconnected",
            SessionDetail = ex is null
                ? "원격 세션이 정상적으로 종료되었습니다."
                : $"원격 세션이 연결 오류로 종료되었습니다. {ex.Message}",
            Status = ex is null ? "Idle" : "Disconnected",
            QualityPercent = 0,
            QualitySummary = ex is null ? "No active connection" : "Transport failure"
        });

        if (_autoReconnectEnabled && !_userInitiatedDisconnect && !_isDisposed && ex is not null)
        {
            _ = TryReconnectAsync();
        }
    }

    private void PublishLog(string title, string message, string meta)
    {
        SessionLogAdded?.Invoke(CreateLog(title, message, meta));
    }

    private void PublishSnapshot(ConnectionSnapshot snapshot)
    {
        SessionSnapshotChanged?.Invoke(snapshot);
    }

    private void LoadPersistedPreferences()
    {
        DevicePreferenceStore.DevicePreferenceSnapshot snapshot = _devicePreferenceStore.Load();
        _favoriteDeviceInternalGuids.Clear();
        foreach (var guid in snapshot.FavoriteDeviceInternalGuids) _favoriteDeviceInternalGuids.Add(guid);
        
        _deviceMetadataMap.Clear();
        foreach (var pair in snapshot.DeviceMetadata) _deviceMetadataMap.Add(pair.Key, pair.Value);

        _recentConnections.Clear();
        _recentConnections.AddRange(snapshot.RecentConnections.OrderByDescending(entry => entry.LastConnectedAt));
        _lastKnownConnection = snapshot.LastKnownConnection;
    }

    private void PersistPreferences()
    {
        _devicePreferenceStore.Save(new DevicePreferenceStore.DevicePreferenceSnapshot(
            _favoriteDeviceInternalGuids.ToArray(),
            _recentConnections.ToArray(),
            _deviceMetadataMap,
            _lastKnownConnection));
    }

    private void UpsertDevice(DeviceModel device)
    {
        // 커스텀 메타데이터 적용
        if (_deviceMetadataMap.TryGetValue(device.InternalGuid, out var meta))
        {
            if (!string.IsNullOrWhiteSpace(meta.CustomName)) device.Name = meta.CustomName;
            if (!string.IsNullOrWhiteSpace(meta.CustomDescription)) device.Description = meta.CustomDescription;
        }

        device.IsFavorite = _favoriteDeviceInternalGuids.Contains(device.InternalGuid);
        _devices[device.InternalGuid] = device;
    }

    private void RecordRecentConnection(DeviceModel? device)
    {
        if (device is null)
        {
            return;
        }

        RecentConnectionEntry entry = new()
        {
            DeviceInternalGuid = device.InternalGuid,
            DeviceName = device.Name,
            DeviceCode = device.DeviceCode,
            LastApprovalMode = _lastApprovalMode,
            LastConnectedAt = DateTime.Now
        };

        _recentConnections.RemoveAll(existing => string.Equals(existing.DeviceInternalGuid, device.InternalGuid, StringComparison.OrdinalIgnoreCase));
        _recentConnections.Insert(0, entry);
        if (_recentConnections.Count > 10)
        {
            _recentConnections.RemoveRange(10, _recentConnections.Count - 10);
        }

        PersistPreferences();
        RecentConnectionsChanged?.Invoke();
    }

    private bool IsPreApprovedDevice(DeviceModel? device)
    {
        if (device is null)
        {
            return false;
        }

        return device.IsFavorite || _favoriteDeviceInternalGuids.Contains(device.InternalGuid) || _devicePreferenceStore.IsFavoriteDevice(device.InternalGuid);
    }

    private string? GetExpectedTrustedThumbprint(DeviceModel? device)
    {
        if (device is null)
        {
            return null;
        }

        if (_deviceMetadataMap.TryGetValue(device.InternalGuid, out DevicePreferenceStore.DeviceMetadata? metadata) &&
            !string.IsNullOrWhiteSpace(metadata.TrustedThumbprint))
        {
            return metadata.TrustedThumbprint;
        }

        DevicePreferenceStore.DevicePreferenceSnapshot snapshot = _devicePreferenceStore.Load();
        if (snapshot.DeviceMetadata.TryGetValue(device.InternalGuid, out DevicePreferenceStore.DeviceMetadata? persistedMetadata) &&
            !string.IsNullOrWhiteSpace(persistedMetadata.TrustedThumbprint))
        {
            return persistedMetadata.TrustedThumbprint;
        }

        return null;
    }

    private void PersistLastKnownConnection(DeviceModel? device)
    {
        if (device is null || string.IsNullOrWhiteSpace(_lastRequestedAddress) || _lastRequestedPort <= 0)
        {
            return;
        }

        // 마지막으로 정상 연결된 엔드포인트를 저장해 두면 탐색 실패 상황에서도 재연결 시 우선 활용할 수 있습니다.
        _lastKnownConnection = new DevicePreferenceStore.LastKnownConnectionInfo(
            device.InternalGuid,
            device.Name,
            device.DeviceCode,
            _lastApprovalMode,
            _lastRequestedAddress,
            _lastRequestedPort,
            DateTime.Now);
        PersistPreferences();
    }

    private (DeviceModel? Device, string Address, int Port, string Reason)? ResolveReconnectTarget()
    {
        if (!string.IsNullOrWhiteSpace(_lastRequestedIdentifier))
        {
            DeviceResolutionResult resolution = ResolveDevice(_lastRequestedIdentifier);
            if (resolution.Status == DeviceResolutionStatus.SingleMatch && resolution.ResolvedDevice is not null)
            {
                DeviceModel resolvedDevice = resolution.ResolvedDevice;
                DeviceEndpoint? selectedEndpoint = resolvedDevice.Endpoints.FirstOrDefault(endpoint => endpoint.Scope == DeviceEndpointScope.Local)
                    ?? resolvedDevice.Endpoints.FirstOrDefault();
                if (selectedEndpoint is not null)
                {
                    return (resolvedDevice, selectedEndpoint.Address, selectedEndpoint.Port, "Discovery");
                }
            }
        }

        if (_lastKnownConnection is not null && !string.IsNullOrWhiteSpace(_lastKnownConnection.Address) && _lastKnownConnection.Port > 0)
        {
            DeviceModel? fallbackDevice = !string.IsNullOrWhiteSpace(_lastKnownConnection.DeviceInternalGuid)
                && _devices.TryGetValue(_lastKnownConnection.DeviceInternalGuid, out DeviceModel? persistedDevice)
                    ? persistedDevice
                    : null;
            return (fallbackDevice, _lastKnownConnection.Address, _lastKnownConnection.Port, "LastKnownGood");
        }

        return null;
    }

    private async Task HandleRemoteDownloadRequestAsync(string remotePath, TcpSession session)
    {
        try
        {
            PublishLog("Download Requested", $"Remote side requested to download: {remotePath}", "File Outbound");
            await _fileTransferService
                .SendFileWithMetaAsync(remotePath, session, FileMetaPacketType, FileChunkPacketType, default)
                .ConfigureAwait(false);
            PublishLog("Download Sent", $"Remote file download completed: {remotePath}", "File Outbound");
        }
        catch (FileNotFoundException ex)
        {
            PublishLog("Download Failed", $"다운로드 요청 파일을 찾을 수 없습니다: {remotePath}", $"File Outbound / {ex.GetType().Name}");
        }
        catch (UnauthorizedAccessException ex)
        {
            PublishLog("Download Failed", $"다운로드 요청 파일에 접근할 수 없습니다: {remotePath}", $"File Outbound / {ex.GetType().Name}");
        }
        catch (OperationCanceledException)
        {
            PublishLog("Download Cancelled", $"다운로드 전송이 취소되었습니다: {remotePath}", "File Outbound");
        }
        catch (Exception ex)
        {
            PublishLog("Download Failed", $"다운로드 전송 중 오류가 발생했습니다: {remotePath}. {ex.Message}", $"File Outbound / {ex.GetType().Name}");
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        string candidate = string.IsNullOrWhiteSpace(fileName) ? "ReceivedFile.dat" : Path.GetFileName(fileName);
        char[] invalidChars = Path.GetInvalidFileNameChars();
        StringBuilder builder = new(candidate.Length);
        foreach (char ch in candidate)
        {
            builder.Append(invalidChars.Contains(ch) ? '_' : ch);
        }

        string sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "ReceivedFile.dat" : sanitized;
    }

    private static string ResolveUniqueDownloadPath(string fileName)
    {
        Directory.CreateDirectory(DownloadFolderPath);
        string sanitizedFileName = SanitizeFileName(fileName);
        string extension = Path.GetExtension(sanitizedFileName);
        string baseName = Path.GetFileNameWithoutExtension(sanitizedFileName);
        string candidatePath = Path.Combine(DownloadFolderPath, sanitizedFileName);

        if (!File.Exists(candidatePath))
        {
            return candidatePath;
        }

        for (int suffix = 1; suffix <= 9999; suffix++)
        {
            string nextCandidate = Path.Combine(DownloadFolderPath, $"{baseName} ({suffix}){extension}");
            if (!File.Exists(nextCandidate))
            {
                return nextCandidate;
            }
        }

        return Path.Combine(DownloadFolderPath, $"{baseName}_{DateTime.Now:yyyyMMddHHmmss}{extension}");
    }

    private static string GetFallbackDownloadPath()
    {
        return ResolveUniqueDownloadPath("ReceivedFile.dat");
    }

    private void TryStartClipboardSyncLoop()
    {
        if (!_isClipboardSyncEnabled || _currentSession is null || _clipboardSyncCts is not null || _isDisposed)
        {
            return;
        }

        _clipboardSyncCts = new CancellationTokenSource();
        _ = RunClipboardSyncLoopAsync(_currentSession, _clipboardSyncCts.Token);
    }

    private void StopClipboardSyncLoop()
    {
        _clipboardSyncCts?.Cancel();
        _clipboardSyncCts?.Dispose();
        _clipboardSyncCts = null;
        _lastSentClipboardText = string.Empty;
        _lastAppliedClipboardText = string.Empty;
        _lastSentClipboardImageHash = 0;
        _lastAppliedClipboardImageHash = 0;
    }

    private async Task RunClipboardSyncLoopAsync(TcpSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_isDisposed)
            {
                if (!_isClipboardSyncEnabled || !ReferenceEquals(_currentSession, session))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                string clipboardText = _clipboardSyncService.GetText();
                string validationReason = string.Empty;
                bool isClipboardTextValid = !string.IsNullOrWhiteSpace(clipboardText) && TryValidateClipboardText(clipboardText, out validationReason);
                if (isClipboardTextValid)
                {
                    if (string.Equals(clipboardText, _lastSentClipboardText, StringComparison.Ordinal) ||
                        string.Equals(clipboardText, _lastAppliedClipboardText, StringComparison.Ordinal))
                    {
                        PublishLog("Clipboard Skipped", "동일한 텍스트 클립보드가 이미 전송되었거나 수신 적용되어 재전송을 건너뜁니다.", "Clipboard / Text / Skipped");
                    }
                    else
                    {
                        await SendClipboardTextAsync(session, clipboardText, cancellationToken).ConfigureAwait(false);
                        _lastSentClipboardText = clipboardText;
                        PublishLog("Clipboard Sent", "텍스트 클립보드가 전송되었습니다.", "Clipboard / Text / Sent");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(clipboardText))
                {
                    PublishLog("Clipboard Skipped", $"텍스트 클립보드 전송을 건너뜁니다. {validationReason}", "Clipboard / Text / Skipped");
                }
                else
                {
                    // 텍스트 변화가 없을 때만 이미지 체크 (대역폭 배분)
                    byte[]? imageBytes = _clipboardSyncService.GetImageAsPng();
                    if (imageBytes != null)
                    {
                        ulong imageHash = ComputeSimpleHash(imageBytes);
                        if (imageHash != _lastSentClipboardImageHash && imageHash != _lastAppliedClipboardImageHash)
                        {
                            await SendClipboardImageAsync(session, imageBytes, cancellationToken).ConfigureAwait(false);
                            _lastSentClipboardImageHash = imageHash;
                            PublishLog("Clipboard Synced (Image)", $"이미지 클립보드가 전송되었습니다. ({imageBytes.Length / 1024} KB)", "Clipboard / Image / Sent");
                        }
                    }
                    else
                    {
                        _lastSentClipboardImageHash = 0; // 이미지 사라짐 대응
                    }
                }

                // 3. 파일 클립보드 체크 (FR-8 기초)
                if (_isCtrlCopyEnabled)
                {
                    string[]? files = _clipboardSyncService.GetFileDropList();
                    if (files != null && files.Length > 0)
                    {
                        ulong currentHash = CalculateFilesHash(files);
                        if (currentHash != _lastSentClipboardFilesHash)
                        {
                            _lastSentClipboardFilesHash = currentHash;
                            var metaList = files.Select(f => new Models.ClipboardFileMeta
                            {
                                Name = Path.GetFileName(f),
                                FullPath = f,
                                Size = File.Exists(f) ? new FileInfo(f).Length : 0
                            }).ToList();

                            string json = JsonSerializer.Serialize(metaList);
                            await _currentSession!.SendAsync(Combine([ClipboardFilesPacketType], Encoding.UTF8.GetBytes(json)), cancellationToken).ConfigureAwait(false);
                            PublishLog("Clipboard Files", $"클립보드에 {files.Length}개의 파일이 복사되어 동기화를 준비합니다.", "Clipboard / Files / Sent");
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealRemoteSessionService] Clipboard sync loop error: {ex.Message}");
            PublishLog("Clipboard Sync Error", $"클립보드 동기화 루프 중 오류가 발생했습니다. {ex.Message}", "Clipboard / Error");
        }
        finally
        {
            if (_clipboardSyncCts?.Token == cancellationToken)
            {
                _clipboardSyncCts.Dispose();
                _clipboardSyncCts = null;
            }
        }
    }

    private void TryStartCursorSyncLoop()
    {
        if (_currentSession == null || _isDisposed) return;
        _cursorCts?.Cancel();
        _cursorCts = new CancellationTokenSource();
        _ = CursorSyncLoopAsync(_currentSession, _cursorCts.Token);
    }

    private async Task CursorSyncLoopAsync(TcpSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _currentSession == session)
            {
                var cursorInfo = _cursorCaptureService.CaptureCurrentCursor();
                if (cursorInfo != null)
                {
                    byte[] nameBytes = Encoding.UTF8.GetBytes(cursorInfo.CursorName);
                    byte[] packet = new byte[2 + nameBytes.Length];
                    packet[0] = CursorShapePacketType;
                    packet[1] = (byte)(cursorInfo.IsVisible ? 1 : 0);
                    nameBytes.CopyTo(packet.AsSpan(2));
                    await session.SendAsync(packet, cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealRemoteSessionService] Cursor sync loop error: {ex.Message}");
        }
    }

    private async Task SendClipboardTextAsync(TcpSession session, string clipboardText, CancellationToken cancellationToken)
    {
        byte[] textBytes = Encoding.UTF8.GetBytes(clipboardText);
        byte[] packet = ArrayPool<byte>.Shared.Rent(textBytes.Length + 1);
        try
        {
            packet[0] = ClipboardTextPacketType;
            textBytes.CopyTo(packet.AsSpan(1));
            await session.SendAsync(new ReadOnlyMemory<byte>(packet, 0, textBytes.Length + 1), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    private async Task SendClipboardImageAsync(TcpSession session, byte[] imageBytes, CancellationToken cancellationToken)
    {
        byte[] packet = ArrayPool<byte>.Shared.Rent(imageBytes.Length + 1);
        try
        {
            packet[0] = ClipboardImagePacketType;
            imageBytes.CopyTo(packet.AsSpan(1));
            await session.SendAsync(new ReadOnlyMemory<byte>(packet, 0, imageBytes.Length + 1), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    private static ulong ComputeSimpleHash(byte[] data)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private void HandleIncomingClipboardText(ReadOnlyMemory<byte> payload)
    {
        if (!_isClipboardSyncEnabled || payload.IsEmpty)
        {
            return;
        }

        string clipboardText;
        try
        {
            clipboardText = Encoding.UTF8.GetString(payload.Span);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealRemoteSessionService] Clipboard payload decode error: {ex.Message}");
            PublishLog("Clipboard Sync Error", $"수신 클립보드 텍스트를 해석하지 못했습니다. {ex.Message}", "Clipboard / Text / Error");
            return;
        }

        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            PublishLog("Clipboard Skipped", "비어 있는 텍스트 클립보드 payload를 수신하여 적용을 건너뜁니다.", "Clipboard / Text / Skipped");
            return;
        }

        if (!TryValidateClipboardText(clipboardText, out string validationReason))
        {
            PublishLog("Clipboard Skipped", $"수신 텍스트 클립보드 적용을 건너뜁니다. {validationReason}", "Clipboard / Text / Skipped");
            return;
        }

        string currentClipboardText = _clipboardSyncService.GetText();
        if (string.Equals(currentClipboardText, clipboardText, StringComparison.Ordinal))
        {
            _lastAppliedClipboardText = clipboardText;
            PublishLog("Clipboard Skipped", "동일한 텍스트 클립보드가 이미 로컬에 존재하여 적용을 건너뜁니다.", "Clipboard / Text / Skipped");
            return;
        }

        _clipboardSyncService.SetText(clipboardText);
        _lastAppliedClipboardText = clipboardText;
        _lastSentClipboardText = clipboardText;
        PublishLog("Clipboard Received", $"텍스트 클립보드가 {_lastRequestedDevice?.Name ?? "현재 세션"}에서 동기화되었습니다.", "Clipboard / Text / Received");
    }

    private void HandleIncomingClipboardImage(ReadOnlyMemory<byte> payload)
    {
        if (!_isClipboardSyncEnabled || payload.IsEmpty)
        {
            return;
        }

        byte[] imageBytes = payload.ToArray();
        ulong imageHash = ComputeSimpleHash(imageBytes);

        if (imageHash == _lastSentClipboardImageHash || imageHash == _lastAppliedClipboardImageHash)
        {
            return;
        }

        Application.Current?.Dispatcher.Invoke(() =>
        {
            _lastAppliedClipboardImageHash = imageHash;
            _clipboardSyncService.SetImageFromPng(imageBytes);
            PublishLog("Clipboard Received (Image)", $"이미지 클립보드가 수신되어 적용되었습니다. ({imageBytes.Length / 1024} KB)", "Clipboard / Image / Received");
        });
    }

    private static bool TryValidateClipboardText(string clipboardText, out string reason)
    {
        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            reason = "빈 텍스트입니다.";
            return false;
        }

        if (clipboardText.Length > MaxClipboardTextLength)
        {
            reason = $"텍스트 길이가 제한({MaxClipboardTextLength}자)를 초과했습니다.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static ConnectionSnapshot CreateApprovalDeniedSnapshot(string title, string detail, string qualitySummary)
    {
        return new ConnectionSnapshot
        {
            SessionTitle = title,
            SessionDetail = detail,
            Status = "Denied",
            QualityPercent = 0,
            QualitySummary = qualitySummary
        };
    }

    private static ConnectionSnapshot CreatePendingApprovalSnapshot(string targetAddress, int targetPort, string approvalMode)
    {
        bool requiresInteractiveApproval = ApprovalService.RequiresInteractiveApproval(approvalMode);
        bool isSupportRequest = approvalMode == "Support request";
        return new ConnectionSnapshot
        {
            SessionTitle = requiresInteractiveApproval
                ? isSupportRequest ? "Waiting for support approval" : "Waiting for approval"
                : "Network Handshake",
            SessionDetail = requiresInteractiveApproval
                ? isSupportRequest
                    ? $"지원 요청 정책에 따라 세션 승인을 기다리는 중입니다. 대상: {targetAddress}:{targetPort}"
                    : $"{approvalMode} 정책에 따라 세션 승인을 기다리는 중입니다. 대상: {targetAddress}:{targetPort}"
                : $"TCP socket connection requested: {targetAddress}:{targetPort}",
            Status = requiresInteractiveApproval ? "Pending Approval" : "Connecting",
            QualityPercent = requiresInteractiveApproval ? 20 : 50,
            QualitySummary = requiresInteractiveApproval
                ? isSupportRequest ? "Support approval requested" : "Approval requested"
                : "Awaiting socket connection"
        };
    }

    private void HandleDiscoveredDevicesUpdated()
    {
        MergeDiscoveredDevices();
    }

    private void MergeDiscoveredDevices()
    {
        List<DeviceModel> latestDevices = [CreateLocalDeviceModel()];
        latestDevices.AddRange(_localDiscoveryService.GetDiscoveredDevices());

        _devices.Clear();
        foreach (DeviceModel device in latestDevices)
        {
            UpsertDevice(device);
        }

        DevicesChanged?.Invoke();
    }

    public void UpdateDeviceMetadata(string internalGuid, string? customName, string? customDescription)
    {
        if (string.IsNullOrWhiteSpace(internalGuid)) return;

        var meta = new DevicePreferenceStore.DeviceMetadata(customName, customDescription);
        _deviceMetadataMap[internalGuid] = meta;
        
        if (_devices.TryGetValue(internalGuid, out DeviceModel? device))
        {
            if (!string.IsNullOrWhiteSpace(customName)) device.Name = customName;
            if (!string.IsNullOrWhiteSpace(customDescription)) device.Description = customDescription;
        }
        
        PersistPreferences();
        DevicesChanged?.Invoke();
    }

    public void RegisterManualDevice(string ip, int port)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;

        // MD5 해시를 이용한 수동 등록 장치용 고유 ID 생성
        using var md5 = System.Security.Cryptography.MD5.Create();
        byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes($"{ip}:{port}"));
        string internalGuid = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

        var device = new DeviceModel
        {
            Name = $"Manual: {ip}",
            DeviceId = $"M-{internalGuid[..8].ToUpperInvariant()}",
            DeviceCode = $"M-{internalGuid[..8].ToUpperInvariant()}",
            InternalGuid = internalGuid,
            Description = $"수동으로 등록된 장치 ({ip}:{port})",
            Status = DeviceStatus.Online,
            IsFavorite = false,
            Endpoints =
            [
                new DeviceEndpoint { Address = ip, Port = port, Scope = DeviceEndpointScope.Public }
            ],
            Capabilities = ["Manual Connection", "File Transfer", "Screen Control"]
        };

        UpsertDevice(device);
        DevicesChanged?.Invoke();
    }


    public void RemoveDevice(string deviceId)
    {
        if (_devices.TryGetValue(deviceId, out var device))
        {
            _devices.Remove(deviceId);
            // 즐겨찾기에서도 해제
            if (_favoriteDeviceInternalGuids.Contains(device.InternalGuid))
            {
                _favoriteDeviceInternalGuids.Remove(device.InternalGuid);
                PersistPreferences();
            }
            DevicesChanged?.Invoke();
        }
    }

    private DeviceModel CreateLocalDeviceModel()
    {
        return new DeviceModel
        {
            Name = _localIdentity.DeviceName,
            DeviceId = _localIdentity.DeviceCode,
            DeviceCode = _localIdentity.DeviceCode,
            InternalGuid = _localIdentity.InternalGuid,
            Description = "현재 PC에서 브로드캐스트 기반 탐색과 직접 연결을 지원하는 로컬 장치입니다.",
            LastSeenLabel = "Last seen: just now",
            Status = DeviceStatus.Online,
            IsFavorite = true,
            Endpoints =
            [
                new DeviceEndpoint
                {
                    Address = IPAddress.Loopback.ToString(),
                    Port = 9999,
                    Scope = DeviceEndpointScope.Local
                }
            ],
            Capabilities = ["Screen", "Input", "UDP Discovery"]
        };
    }

    private async Task TryReconnectAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastRequestedIdentifier) && _lastKnownConnection is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _isReconnectInProgress, 1, 0) != 0)
        {
            return;
        }

        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _reconnectCts.Token;
        DateTime reconnectStartedAt = DateTime.UtcNow;

        try
        {
            for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan delay = GetReconnectDelay(attempt);
                string targetLabel = _lastRequestedIdentifier ?? _lastKnownConnection?.DeviceCode ?? "unknown target";
                PublishSnapshot(new ConnectionSnapshot
                {
                    SessionTitle = "Reconnecting",
                    SessionDetail = $"{targetLabel} 장치에 대해 자동 재연결을 시도합니다. {attempt}/{MaxReconnectAttempts}회, 대기 {delay.TotalSeconds:0}초",
                    Status = "Reconnecting",
                    QualityPercent = 15,
                    QualitySummary = $"Reconnect attempt {attempt}/{MaxReconnectAttempts}"
                });
                PublishLog("Reconnect Attempt", $"Attempting reconnect #{attempt} for {targetLabel} after {delay.TotalSeconds:0}s backoff.", $"Reconnect / Delay={delay.TotalSeconds:0}s");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                if (DateTime.UtcNow - reconnectStartedAt > MaxReconnectWindow)
                {
                    PublishLog("Reconnect Exhausted", $"Reconnect window exceeded for {targetLabel}.", "Reconnect / WindowExpired");
                    PublishSnapshot(new ConnectionSnapshot
                    {
                        SessionTitle = "Reconnect failed",
                        SessionDetail = $"{targetLabel} 장치에 대한 자동 재연결 제한 시간을 초과했습니다. 수동 연결을 진행해 주세요.",
                        Status = "Failed",
                        QualityPercent = 0,
                        QualitySummary = "Reconnect window expired"
                    });
                    return;
                }

                var reconnectTarget = ResolveReconnectTarget();
                if (reconnectTarget is null)
                {
                    PublishLog("Reconnect Failed", $"Unable to resolve reconnect target for {targetLabel}.", "Reconnect / ResolutionFailed");
                    continue;
                }

                _lastRequestedDevice = reconnectTarget.Value.Device ?? _lastRequestedDevice;
                _lastRequestedAddress = reconnectTarget.Value.Address;
                _lastRequestedPort = reconnectTarget.Value.Port;

                await ConnectToTargetAsync(
                    reconnectTarget.Value.Address,
                    reconnectTarget.Value.Port,
                    reconnectTarget.Value.Device ?? _lastRequestedDevice,
                    _lastApprovalMode,
                    isReconnect: true,
                    cancellationToken).ConfigureAwait(false);

                if (_currentSession is not null)
                {
                    PublishLog("Reconnect Succeeded", $"Reconnected to {targetLabel} via {reconnectTarget.Value.Reason}.", $"Reconnect / Source={reconnectTarget.Value.Reason}");
                    return;
                }
            }

            string exhaustedTarget = _lastRequestedIdentifier ?? _lastKnownConnection?.DeviceCode ?? "unknown target";
            PublishLog("Reconnect Exhausted", $"Reconnect attempts exceeded for {exhaustedTarget}.", "Reconnect / AttemptsExceeded");
            PublishSnapshot(new ConnectionSnapshot
            {
                SessionTitle = "Reconnect failed",
                SessionDetail = $"{exhaustedTarget} 장치에 대한 자동 재연결 시도가 모두 실패했습니다. 네트워크 상태를 확인한 뒤 다시 연결해 주세요.",
                Status = "Failed",
                QualityPercent = 0,
                QualitySummary = "Reconnect attempts exhausted"
            });
        }
        catch (OperationCanceledException)
        {
            PublishLog("Reconnect Cancelled", "Reconnect workflow was cancelled.", "Reconnect");
            PublishSnapshot(new ConnectionSnapshot
            {
                SessionTitle = "Reconnect cancelled",
                SessionDetail = "자동 재연결이 취소되었습니다. 사용자가 세션을 종료했거나 새 연결 흐름이 시작되었습니다.",
                Status = "Idle",
                QualityPercent = 0,
                QualitySummary = "Reconnect cancelled"
            });
        }
        finally
        {
            Interlocked.Exchange(ref _isReconnectInProgress, 0);
        }
    }

    private static TimeSpan GetReconnectDelay(int attempt)
    {
        int seconds = (int)Math.Min(Math.Pow(2, attempt - 1), MaxReconnectDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private Int32Rect? GetSelectedViewerBounds()
    {
        if (string.IsNullOrWhiteSpace(_selectedViewerDisplayId))
        {
            return null;
        }

        var selectedViewerDisplay = _captureService
            .GetAvailableDisplays()
            .FirstOrDefault(display => display.DisplayId == _selectedViewerDisplayId);

        if (selectedViewerDisplay is null)
        {
            return null;
        }

        return _captureService.GetOutputBounds(selectedViewerDisplay.OutputIndex);
    }

    private string GetSelectedCaptureDisplayLabel()
    {
        var selectedCaptureDisplay = _captureService
            .GetAvailableDisplays()
            .FirstOrDefault(display => display.DisplayId == _selectedCaptureDisplayId);

        return selectedCaptureDisplay?.Label ?? "Auto";
    }

    private string GetSelectedViewerDisplayLabel()
    {
        if (string.IsNullOrWhiteSpace(_selectedViewerDisplayId))
        {
            return _keepViewerOnSafeDisplay ? "Auto (Safe Display)" : "Auto";
        }

        var selectedViewerDisplay = _captureService
            .GetAvailableDisplays()
            .FirstOrDefault(display => display.DisplayId == _selectedViewerDisplayId);

        return selectedViewerDisplay?.Label ?? "Auto";
    }

    private string GetCompressionLabel()
    {
        return _compressionEncodingMode == JpegEncoding ? $"JPEG {_jpegQuality}" : "Raw BGRA";
    }

    private void RefreshViewerWindowStatus()
    {
        Application.Current?.Dispatcher.Invoke(() =>
            _rdpWindow?.SetStatusDetails(
                GetSelectedCaptureDisplayLabel(),
                GetSelectedViewerDisplayLabel(),
                GetCompressionLabel()));
    }

    private void HandleViewerWindowClosed(object? sender, EventArgs e)
    {
        if (_isClosingViewerWindow)
        {
            return;
        }

        _viewerClosedByUser = true;
        _userInitiatedDisconnect = true;

        if (_rdpWindow is not null)
        {
            _rdpWindow.Closed -= HandleViewerWindowClosed;
            _rdpWindow.OnMouseInputCaptured -= HandleClientMouseInput;
            _rdpWindow.OnKeyboardInputCaptured -= HandleClientKeyboardInput;
            _rdpWindow = null;
        }

        _captureCts?.Cancel();
        _captureCts?.Dispose();
        _captureCts = null;
        StopClipboardSyncLoop();

        var sessionToClose = _currentSession;
        _currentSession = null;
        sessionToClose?.Dispose();

        PublishLog("Remote Window Closed", "Viewer window closed by operator. Active remote session terminated.", "Viewer");
        PublishSnapshot(new ConnectionSnapshot
        {
            SessionTitle = "Viewer closed",
            SessionDetail = "원격 뷰어 창이 닫혀 현재 세션이 종료되었습니다.",
            Status = "Idle",
            QualityPercent = 0,
            QualitySummary = "Viewer closed"
        });
    }

    private void CloseViewerWindowInternal()
    {
        if (_rdpWindow is null)
        {
            return;
        }

        _isClosingViewerWindow = true;
        try
        {
            _rdpWindow.Closed -= HandleViewerWindowClosed;
            _rdpWindow.OnMouseInputCaptured -= HandleClientMouseInput;
            _rdpWindow.OnKeyboardInputCaptured -= HandleClientKeyboardInput;
            _rdpWindow.Close();
            _rdpWindow = null;
        }
        finally
        {
            _isClosingViewerWindow = false;
        }
    }

    private static byte[] EncodeFrame(ReadOnlyMemory<byte> frameData, int width, int height, byte encodingMode, long jpegQuality)
    {
        if (encodingMode != JpegEncoding)
        {
            return frameData.ToArray();
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        BitmapData bitmapData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = width * 4;
            byte[] rawBytes = frameData.ToArray();
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(rawBytes, y * stride, IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride), stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        using var stream = new MemoryStream();
        ImageCodecInfo jpegCodec = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Clamp(jpegQuality, 1, 100));
        bitmap.Save(stream, jpegCodec, parameters);
        return stream.ToArray();
    }

    private void HandleFileChunkReceived(ReadOnlyMemory<byte> data)
    {
        string destination = string.IsNullOrWhiteSpace(_receivingFilePath) ? GetFallbackDownloadPath() : _receivingFilePath;
        _ = _fileTransferService.ReceiveFileChunkAsync(destination, data, _fileTransferCts?.Token ?? default);
        _receivedBytes += data.Length;
        if (_receivingFileSize > 0)
        {
            double progress = (double)_receivedBytes / _receivingFileSize;
            FileTransferProgressChanged?.Invoke(progress);
            if (_receivedBytes >= _receivingFileSize)
            {
                PublishLog("File Received", $"파일 수신 완료: {Path.GetFileName(destination)}", "File / Inbound");
                _fileTransferCts?.Dispose();
                _fileTransferCts = null;
            }
        }
    }

    private void HandleIncomingFileMeta(ReadOnlyMemory<byte> data)
    {
        try
        {
            string json = Encoding.UTF8.GetString(data.Span);
            var meta = JsonSerializer.Deserialize<FileMetadataPacket>(json);
            if (meta == null) return;
            _receivingFilePath = ResolveUniqueDownloadPath(meta.FileName);
            _receivingFileSize = meta.FileSize;
            _receivedBytes = 0;
            _fileTransferCts?.Cancel();
            _fileTransferCts = new CancellationTokenSource();
            PublishLog("File Transfer Started", $"파일 수신 시작: {meta.FileName} ({meta.FileSize / 1024} KB)", "File / Inbound");
            FileTransferProgressChanged?.Invoke(0.0);
        }
        catch { }
    }

    private void HandleFileSystemListRequest(ReadOnlyMemory<byte> data, TcpSession session)
    {
        string path = Encoding.UTF8.GetString(data.Span);
        _ = Task.Run(async () =>
        {
            var json = _fileSystemService.GetDirectoryListingJson(path);
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            await session.SendAsync(Combine(new byte[] { FileSystemListResponsePacketType }, jsonBytes)).ConfigureAwait(false);
        });
    }

    private record FileMetadataPacket(string FileName, long FileSize);

    private async Task SendConnectionSetupAsync(TcpSession session)
    {
        try
        {
            var setup = new {
                DeviceName = _localIdentity.DeviceName,
                DeviceCode = _localIdentity.DeviceCode,
                InternalGuid = _localIdentity.InternalGuid,
                RequestedApprovalMode = _lastApprovalMode
            };
            string json = JsonSerializer.Serialize(setup);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            await session.SendAsync(Combine(new byte[] { ConnectionSetupPacketType }, jsonBytes)).ConfigureAwait(false);
            PublishLog("Handshake Sent", "원격지로 연결 설정 및 승인 정책 정보를 전송했습니다.", "Security");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Session] Send ConnectionSetup Error: {ex.Message}");
        }
    }

    private async Task HandleIncomingConnectionSetupAsync(TcpSession session, ReadOnlyMemory<byte> payload)
    {
        try
        {
            string json = Encoding.UTF8.GetString(payload.Span);
            var setup = JsonSerializer.Deserialize<Models.ConnectionSetupData>(json);
            if (setup == null) return;

            PublishLog("Handshake Received", $"원격 장치({setup.DeviceName})로부터 {setup.RequestedApprovalMode} 정책으로 연결 요청이 들어왔습니다.", "Security");

            var decision = await _approvalService.RequestApprovalAsync(
                new DeviceModel { Name = setup.DeviceName, DeviceCode = setup.DeviceCode, InternalGuid = setup.InternalGuid },
                setup.RequestedApprovalMode,
                isReconnect: false,
                CancellationToken.None).ConfigureAwait(false);

            byte result = decision == ApprovalDecision.Approved ? (byte)1 : (byte)0;
            await session.SendAsync(new byte[] { ConnectionSetupResponsePacketType, result }).ConfigureAwait(false);

            if (decision == ApprovalDecision.Approved)
            {
                PublishLog("Connection Approved", $"사용자가 {setup.DeviceName}의 연결을 승인했습니다.", "Security / Approved");
                StartActiveSessionLogic(session);
            }
            else
            {
                PublishLog("Connection Denied", $"사용자가 {setup.DeviceName}의 연결을 거부했습니다.", "Security / Denied");
                session.Dispose();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Session] Handle ConnectionSetup Error: {ex.Message}");
            session.Dispose();
        }
    }

    private void HandleIncomingConnectionSetupResponse(TcpSession session, byte result)
    {
        if (result == 1)
        {
            PublishLog("Handshake Approved", "원격지로부터 연결 승인을 받았습니다.", "Security / Response");
            StartActiveSessionLogic(session);
        }
        else
        {
            PublishLog("Handshake Denied", "원격지가 연결을 거부했습니다.", "Security / Response");
            PublishSnapshot(new ConnectionSnapshot
            {
                SessionTitle = "Connection Denied",
                SessionDetail = "원격 호스트가 연결을 명시적으로 거부했거나 상호 작용 없이 세션이 종료되었습니다.",
                Status = "Denied"
            });
            session.Dispose();
        }
    }

    private void StartActiveSessionLogic(TcpSession session)
    {
        _isSessionApproved = true;
        _telemetryCts?.Cancel();
        _telemetryCts = new CancellationTokenSource();
        _ = RunTelemetryLoopAsync(_telemetryCts.Token);
    }

    private async Task RunTelemetryLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _currentSession != null && _isSessionApproved)
        {
            try
            {
                long ticks = DateTime.UtcNow.Ticks;
                byte[] tickBytes = BitConverter.GetBytes(ticks);
                await _currentSession.SendAsync(Combine(new byte[] { PingPacketType }, tickBytes)).ConfigureAwait(false);
            }
            catch { }
            await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
        }
    }

    private void HandleIncomingPong(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < 8) return;
        long sentTicks = BitConverter.ToInt64(payload.Span);
        long rttTicks = DateTime.UtcNow.Ticks - sentTicks;
        _lastMeasuredRttMs = rttTicks / TimeSpan.TicksPerMillisecond;
        UpdateConnectionQualityFromRtt(_lastMeasuredRttMs);
        PublishSnapshot(CreateConnectionSnapshot());
    }

    private void UpdateConnectionQualityFromRtt(long rttMs)
    {
        if (rttMs < 0) return;
        if (rttMs < 30) { _connectionQualityPercent = 100; _connectionQualitySummary = "Excellent"; }
        else if (rttMs < 80) { _connectionQualityPercent = 95; _connectionQualitySummary = "Good"; }
        else if (rttMs < 150) { _connectionQualityPercent = 80; _connectionQualitySummary = "Fair"; }
        else { _connectionQualityPercent = 50; _connectionQualitySummary = "Poor"; }
    }

    private ConnectionSnapshot CreateConnectionSnapshot()
    {
        return new ConnectionSnapshot
        {
            SessionTitle = _activeSnapshot.SessionTitle,
            SessionDetail = _activeSnapshot.SessionDetail,
            Status = _activeSnapshot.Status,
            QualityPercent = _connectionQualityPercent,
            QualitySummary = _connectionQualitySummary
        };
    }

    private static byte[] Combine(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        byte[] result = new byte[first.Length + second.Length];
        first.CopyTo(result);
        second.CopyTo(result.AsSpan(first.Length));
        return result;
    }
}
