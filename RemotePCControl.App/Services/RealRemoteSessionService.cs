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
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RemotePCControl.App.Infrastructure.Input;
using RemotePCControl.App.Infrastructure.Network;
using RemotePCControl.App.Models;

namespace RemotePCControl.App.Services;

public sealed class RealRemoteSessionService : IRemoteSessionService, IDisposable
{
    private const byte ScreenFramePacketType = 0x00;
    private const byte MouseInputPacketType = 0x01;
    private const byte KeyboardInputPacketType = 0x02;
    private const byte FileChunkPacketType = 0x03;
    private const byte ClipboardTextPacketType = 0x04;
    private const byte RawBgraEncoding = 0x00;
    private const byte JpegEncoding = 0x01;

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
    private readonly Dictionary<string, DeviceModel> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoriteDeviceInternalGuids = new(StringComparer.OrdinalIgnoreCase);
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
    private bool _isReconnectInProgress;
    private DeviceModel? _lastRequestedDevice;
    private string? _lastRequestedIdentifier;
    private string _lastApprovalMode = "User approval";
    private CancellationTokenSource? _reconnectCts;
    private CancellationTokenSource? _clipboardSyncCts;
    private string _lastSentClipboardText = string.Empty;
    private string _lastAppliedClipboardText = string.Empty;
    private bool _isDisposed;

    public RealRemoteSessionService()
    {
        _tcpManager = new TcpConnectionManager();
        _captureService = new ScreenCaptureService();
        _inputService = new InputInjectionService();
        _fileTransferService = new FileTransferService();
        _clipboardSyncService = new ClipboardSyncService();
        _deviceIdentityStore = new DeviceIdentityStore();
        _devicePreferenceStore = new DevicePreferenceStore();
        _localDiscoveryService = new LocalDiscoveryService();
        _connectionResolutionService = new ConnectionResolutionService();
        _approvalService = new ApprovalService();
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

        if (_devices.TryGetValue(internalGuid, out DeviceModel? device))
        {
            device.IsFavorite = _favoriteDeviceInternalGuids.Contains(internalGuid);
        }

        SavePersistedPreferences();
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

    public void SetAutoReconnect(bool enabled)
    {
        _autoReconnectEnabled = enabled;
        PublishLog("Auto Reconnect", enabled ? "Auto reconnect enabled." : "Auto reconnect disabled.", "Reconnect");
    }

    public void SetClipboardSyncEnabled(bool enabled)
    {
        _isClipboardSyncEnabled = enabled;
        PublishLog("Clipboard Sync", enabled ? "Clipboard text sync enabled." : "Clipboard text sync disabled.", "Clipboard");

        if (!enabled)
        {
            StopClipboardSyncLoop();
            return;
        }

        TryStartClipboardSyncLoop();
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

    public IReadOnlyList<SessionLogEntry> GetSeedLogs() =>
    [
        CreateLog("Engine Started", "RealRemoteSessionService initialized with Network, Capture, Discovery, and Input engines.", "System Ready"),
        CreateLog("Local Device Ready", $"{_localIdentity.DeviceName} / {_localIdentity.DeviceCode}", $"GUID: {_localIdentity.InternalGuid[..8]}"),
        _duplicateCheckResult.IsDuplicate
            ? CreateLog("Duplicate Identifier Warning", "같은 로컬 네트워크에서 중복 장치 이름 또는 장치 번호가 감지되었습니다.", $"Conflicts: {_duplicateCheckResult.Conflicts.Count}")
            : CreateLog("Duplicate Identifier Check", "로컬 네트워크 기준 중복 장치 식별자가 발견되지 않았습니다.", "Broadcast probe complete")
    ];

    public ConnectionSnapshot CreateQuickConnection(DeviceModel? device, string approvalMode)
    {
        _lastRequestedDevice = device;
        _lastRequestedIdentifier = device?.DeviceCode ?? device?.DeviceId;
        _lastApprovalMode = approvalMode;
        _userInitiatedDisconnect = false;

        if (approvalMode == "Pre-approved device" && !(device?.IsFavorite ?? false))
        {
            ConnectionSnapshot deniedSnapshot = CreateApprovalDeniedSnapshot(
                "Approval denied",
                "Pre-approved device policy rejected this connection because the target is not marked as trusted.",
                "Approval denied");
            PublishLog("Connection Denied", $"{device?.Name ?? "Unknown Device"} is not in the pre-approved device set.", "Approval");
            PublishSnapshot(deniedSnapshot);
            return deniedSnapshot;
        }

        DeviceEndpoint? selectedEndpoint = device?.Endpoints.FirstOrDefault(endpoint => endpoint.Scope == DeviceEndpointScope.Local)
            ?? device?.Endpoints.FirstOrDefault();
        string targetAddress = selectedEndpoint?.Address ?? IPAddress.Loopback.ToString();
        int targetPort = selectedEndpoint?.Port ?? 9999;

        _viewerClosedByUser = false;
        _ = ConnectToTargetAsync(targetAddress, targetPort, device, approvalMode, isReconnect: false, CancellationToken.None);
        ConnectionSnapshot pendingSnapshot = CreatePendingApprovalSnapshot(targetAddress, targetPort, approvalMode);
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
        if (_currentSession == null || _isDisposed)
        {
            return Task.CompletedTask;
        }

        return _fileTransferService.SendFileAsync(filePath, _currentSession, default);
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
            ApprovalDecision approvalDecision = await _approvalService
                .RequestApprovalAsync(device, approvalMode, isReconnect, cancellationToken)
                .ConfigureAwait(false);
            if (approvalDecision == ApprovalDecision.Denied)
            {
                ConnectionSnapshot deniedSnapshot = CreateApprovalDeniedSnapshot(
                    "Approval denied",
                    $"Connection to {device?.Name ?? $"{ip}:{port}"} was denied by policy {approvalMode}.",
                    "Approval denied");
                PublishLog("Connection Denied", $"Connection to {device?.Name ?? $"{ip}:{port}"} was denied by policy {approvalMode}.", "Approval");
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
                PublishLog("Connection Cancelled", $"Connection to {device?.Name ?? $"{ip}:{port}"} was cancelled by the operator.", "Approval");
                PublishSnapshot(cancelledSnapshot);
                return;
            }

            var session = await _tcpManager.ConnectAsync(ip, port).ConfigureAwait(false);
            PublishLog("Connection Established", $"Connected to {ip}:{port}. Session ID: {session.SessionId[..8]}", "TCP Connected");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealRemoteSessionService] Connection to {ip}:{port} failed: {ex.Message}");
            PublishLog("Connection Failed", $"Unable to connect to {ip}:{port}. {ex.Message}", "TCP Error");
            PublishSnapshot(new ConnectionSnapshot
            {
                SessionTitle = "Connection failed",
                SessionDetail = $"Unable to connect to {device?.Name ?? $"{ip}:{port}"}. {ex.Message}",
                Status = "Failed",
                QualityPercent = 0,
                QualitySummary = "TCP error"
            });
        }
    }

    private void OnSessionConnected(TcpSession session)
    {
        _currentSession = session;
        _receivedFrameCount = 0;
        _viewerClosedByUser = false;
        _userInitiatedDisconnect = false;
        session.OnMessageReceived += HandleIncomingMessage;
        PublishLog("Session Connected", $"Session {session.SessionId[..8]} is active.", "Network");
        PublishSnapshot(new ConnectionSnapshot
        {
            SessionTitle = $"Connected to {_lastRequestedDevice?.Name ?? session.SessionId[..8]}",
            SessionDetail = "승인과 네트워크 연결이 완료되어 원격 세션이 활성화되었습니다.",
            Status = "Connected",
            QualityPercent = 85,
            QualitySummary = "Session active"
        });
        RecordRecentConnection(_lastRequestedDevice);
        TryStartClipboardSyncLoop();

        if (_captureService.Initialize())
        {
            _captureCts = new CancellationTokenSource();
            _ = _captureService.CaptureLoopAsync(
                (frameData, width, height) => OnFrameCapturedAsync(session, frameData, width, height),
                _captureCts.Token);
            PublishLog("Capture Started", "Screen capture loop started.", "Capture");
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
            _ = _fileTransferService.ReceiveFileChunkAsync("C:\\Temp\\ReceivedFile.dat", chunkData, default);
        }
        else if (packetType == ClipboardTextPacketType)
        {
            HandleIncomingClipboardText(payload.Slice(1));
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

        if (_autoReconnectEnabled && !_userInitiatedDisconnect && !_isDisposed)
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
        foreach (string internalGuid in snapshot.FavoriteDeviceInternalGuids)
        {
            _favoriteDeviceInternalGuids.Add(internalGuid);
        }

        _recentConnections.Clear();
        _recentConnections.AddRange(snapshot.RecentConnections.OrderByDescending(entry => entry.LastConnectedAt));
    }

    private void SavePersistedPreferences()
    {
        _devicePreferenceStore.Save(
            new DevicePreferenceStore.DevicePreferenceSnapshot(
                _favoriteDeviceInternalGuids.ToArray(),
                _recentConnections.ToArray()));
    }

    private void ApplyPersistedDeviceState(DeviceModel device)
    {
        if (_favoriteDeviceInternalGuids.Contains(device.InternalGuid))
        {
            device.IsFavorite = true;
        }
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

        SavePersistedPreferences();
        RecentConnectionsChanged?.Invoke();
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
                if (!string.IsNullOrWhiteSpace(clipboardText) &&
                    !string.Equals(clipboardText, _lastSentClipboardText, StringComparison.Ordinal) &&
                    !string.Equals(clipboardText, _lastAppliedClipboardText, StringComparison.Ordinal))
                {
                    await SendClipboardTextAsync(session, clipboardText, cancellationToken).ConfigureAwait(false);
                    _lastSentClipboardText = clipboardText;
                    PublishLog("Clipboard Synced", $"텍스트 클립보드가 {_lastRequestedDevice?.Name ?? session.SessionId[..8]} 세션으로 전송되었습니다.", "Clipboard Outbound");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealRemoteSessionService] Clipboard sync loop error: {ex.Message}");
            PublishLog("Clipboard Sync Error", $"클립보드 동기화 루프 중 오류가 발생했습니다. {ex.Message}", "Clipboard");
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
            PublishLog("Clipboard Sync Error", $"수신 클립보드 텍스트를 해석하지 못했습니다. {ex.Message}", "Clipboard");
            return;
        }

        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            return;
        }

        string currentClipboardText = _clipboardSyncService.GetText();
        if (string.Equals(currentClipboardText, clipboardText, StringComparison.Ordinal))
        {
            _lastAppliedClipboardText = clipboardText;
            return;
        }

        _clipboardSyncService.SetText(clipboardText);
        _lastAppliedClipboardText = clipboardText;
        _lastSentClipboardText = clipboardText;
        PublishLog("Clipboard Received", $"텍스트 클립보드가 {_lastRequestedDevice?.Name ?? "현재 세션"}에서 동기화되었습니다.", "Clipboard Inbound");
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
        bool requiresInteractiveApproval = approvalMode is "User approval" or "Support request";
        return new ConnectionSnapshot
        {
            SessionTitle = requiresInteractiveApproval ? "Waiting for approval" : "Network Handshake",
            SessionDetail = requiresInteractiveApproval
                ? $"{approvalMode} 정책에 따라 세션 승인을 기다리는 중입니다. 대상: {targetAddress}:{targetPort}"
                : $"TCP socket connection requested: {targetAddress}:{targetPort}",
            Status = requiresInteractiveApproval ? "Pending Approval" : "Connecting",
            QualityPercent = requiresInteractiveApproval ? 20 : 50,
            QualitySummary = requiresInteractiveApproval ? "Approval requested" : "Awaiting socket connection"
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

    private void UpsertDevice(DeviceModel device)
    {
        ApplyPersistedDeviceState(device);
        _devices[device.InternalGuid] = device;
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
        if (_isReconnectInProgress || string.IsNullOrWhiteSpace(_lastRequestedIdentifier))
        {
            return;
        }

        _isReconnectInProgress = true;
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _reconnectCts.Token;

        try
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PublishLog("Reconnect Attempt", $"Attempting reconnect #{attempt} for {_lastRequestedIdentifier}.", "Reconnect");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken).ConfigureAwait(false);

                DeviceResolutionResult resolution = ResolveDevice(_lastRequestedIdentifier);
                if (resolution.Status != DeviceResolutionStatus.SingleMatch || resolution.ResolvedDevice is null)
                {
                    PublishLog("Reconnect Failed", $"Unable to resolve {_lastRequestedIdentifier} during reconnect.", "Reconnect");
                    continue;
                }

                _lastRequestedDevice = resolution.ResolvedDevice;
                DeviceEndpoint? selectedEndpoint = resolution.ResolvedDevice.Endpoints.FirstOrDefault(endpoint => endpoint.Scope == DeviceEndpointScope.Local)
                    ?? resolution.ResolvedDevice.Endpoints.FirstOrDefault();
                if (selectedEndpoint is null)
                {
                    continue;
                }

                await ConnectToTargetAsync(
                    selectedEndpoint.Address,
                    selectedEndpoint.Port,
                    resolution.ResolvedDevice,
                    _lastApprovalMode,
                    isReconnect: true,
                    cancellationToken).ConfigureAwait(false);

                if (_currentSession is not null)
                {
                    PublishLog("Reconnect Succeeded", $"Reconnected to {_lastRequestedIdentifier}.", "Reconnect");
                    return;
                }
            }

            PublishLog("Reconnect Exhausted", $"Reconnect attempts exceeded for {_lastRequestedIdentifier}.", "Reconnect");
        }
        catch (OperationCanceledException)
        {
            PublishLog("Reconnect Cancelled", "Reconnect workflow was cancelled.", "Reconnect");
        }
        finally
        {
            _isReconnectInProgress = false;
        }
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
}
