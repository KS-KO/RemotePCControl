#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private const byte RawBgraEncoding = 0x00;
    private const byte JpegEncoding = 0x01;

    private readonly TcpConnectionManager _tcpManager;
    private readonly ScreenCaptureService _captureService;
    private readonly InputInjectionService _inputService;
    private readonly FileTransferService _fileTransferService;
    private readonly ClipboardSyncService _clipboardSyncService;
    private readonly List<DeviceModel> _devices =
    [
        new DeviceModel
        {
            DeviceId = "local-loopback-01",
            Name = "Local Host (Mock Target)",
            Description = "Local loopback target for remote session verification.",
            LastSeenLabel = "Last seen: just now",
            Status = DeviceStatus.Online,
            IsFavorite = true,
            Capabilities = ["Screen", "Input"]
        }
    ];

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
    private bool _isDisposed;

    public RealRemoteSessionService()
    {
        _tcpManager = new TcpConnectionManager();
        _captureService = new ScreenCaptureService();
        _inputService = new InputInjectionService();
        _fileTransferService = new FileTransferService();
        _clipboardSyncService = new ClipboardSyncService();

        _tcpManager.OnSessionConnected += OnSessionConnected;
        _tcpManager.OnSessionDisconnected += OnSessionDisconnected;
        _tcpManager.StartListening(9999);
    }

    public event Action<SessionLogEntry>? SessionLogAdded;

    public IReadOnlyList<DeviceModel> GetDevices() => _devices;

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
        CreateLog("Engine Started", "RealRemoteSessionService initialized with Network, Capture, and Input engines.", "System Ready")
    ];

    public ConnectionSnapshot CreateQuickConnection(DeviceModel? device, string approvalMode)
    {
        _viewerClosedByUser = false;
        _ = ConnectToTargetAsync("127.0.0.1", 9999);
        return new ConnectionSnapshot
        {
            SessionTitle = "Network Handshake",
            SessionDetail = "TCP socket connection requested: 127.0.0.1:9999",
            Status = "Connecting",
            QualityPercent = 50,
            QualitySummary = "Awaiting socket connection"
        };
    }

    public ConnectionSnapshot CreateSupportSession(DeviceModel? device)
    {
        return new ConnectionSnapshot
        {
            SessionTitle = "Waiting for Approval",
            SessionDetail = "Waiting for the remote user to approve the support session.",
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
        _tcpManager.Dispose();
        _captureService.Dispose();
        _inputService.Dispose();
        _captureCts?.Dispose();
    }

    private async Task ConnectToTargetAsync(string ip, int port)
    {
        try
        {
            var session = await _tcpManager.ConnectAsync(ip, port).ConfigureAwait(false);
            PublishLog("Connection Established", $"Connected to {ip}:{port}. Session ID: {session.SessionId[..8]}", "TCP Connected");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RealRemoteSessionService] Connection to {ip}:{port} failed: {ex.Message}");
            PublishLog("Connection Failed", $"Unable to connect to {ip}:{port}. {ex.Message}", "TCP Error");
        }
    }

    private void OnSessionConnected(TcpSession session)
    {
        _currentSession = session;
        _receivedFrameCount = 0;
        _viewerClosedByUser = false;
        session.OnMessageReceived += HandleIncomingMessage;
        PublishLog("Session Connected", $"Session {session.SessionId[..8]} is active.", "Network");

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
        Application.Current?.Dispatcher.Invoke(CloseViewerWindowInternal);
        PublishLog(
            "Session Disconnected",
            ex is null ? $"Session {session.SessionId[..8]} closed." : $"Session {session.SessionId[..8]} closed with error: {ex.Message}",
            ex is null ? "Network Closed" : "Network Error");
    }

    private void PublishLog(string title, string message, string meta)
    {
        SessionLogAdded?.Invoke(CreateLog(title, message, meta));
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

        var sessionToClose = _currentSession;
        _currentSession = null;
        sessionToClose?.Dispose();

        PublishLog("Remote Window Closed", "Viewer window closed by operator. Active remote session terminated.", "Viewer");
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
