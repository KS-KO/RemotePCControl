using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using RemotePCControl.App.Infrastructure;
using RemotePCControl.App.Models;
using RemotePCControl.App.Services;

namespace RemotePCControl.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IRemoteSessionService _remoteSessionService;
    private readonly RelayCommand _quickConnectCommand;
    private readonly RelayCommand _requestRemoteSupportCommand;
    private readonly RelayCommand _disconnectCommand;
    private readonly RelayCommand _copyFileCommand;
    private readonly RelayCommand _uploadFileCommand;
    private readonly RelayCommand _toggleLocalDriveCommand;
    private DeviceModel? _selectedDevice;
    private string _quickConnectDeviceId = string.Empty;
    private string _selectedApprovalMode = "User approval";
    private string _activeSessionTitle = "No active session";
    private string _activeSessionDetail = "Select a device or enter a device ID to start a remote connection flow.";
    private string _activeSessionStatus = "Idle";
    private string _lastActionSummary = "Waiting for operator input";
    private string _statusMessage = "Session services ready.";
    private string _transferSummary = "No file operation has started yet.";
    private bool _isClipboardSyncEnabled = true;
    private bool _isCtrlCopyEnabled = true;
    private bool _isLocalDriveRedirectEnabled = true;
    private bool _isReconnectEnabled = true;
    private int _connectionQualityPercent;
    private string _connectionQualitySummary = "No active connection";

    public MainViewModel(IRemoteSessionService remoteSessionService)
    {
        _remoteSessionService = remoteSessionService;
        Devices = new ObservableCollection<DeviceModel>(_remoteSessionService.GetDevices());
        SessionLogs = new ObservableCollection<SessionLogEntry>(_remoteSessionService.GetSeedLogs().OrderByDescending(log => log.Timestamp));
        ApprovalModes = ["User approval", "Pre-approved device", "Support request"];

        _quickConnectCommand = new RelayCommand(QuickConnect, CanConnect);
        _requestRemoteSupportCommand = new RelayCommand(RequestRemoteSupport, () => SelectedDevice is not null);
        _disconnectCommand = new RelayCommand(Disconnect, () => ActiveSessionStatus is not "Idle");
        _copyFileCommand = new RelayCommand(CopyFile, () => SelectedDevice is not null && IsCtrlCopyEnabled);
        _uploadFileCommand = new RelayCommand(UploadFile, () => ActiveSessionStatus is not "Idle");
        _toggleLocalDriveCommand = new RelayCommand(ToggleLocalDrive, () => SelectedDevice is not null);

        SelectedDevice = Devices.FirstOrDefault();
        QuickConnectDeviceId = SelectedDevice?.DeviceId ?? string.Empty;
    }

    public ObservableCollection<DeviceModel> Devices { get; }

    public ObservableCollection<SessionLogEntry> SessionLogs { get; }

    public IReadOnlyList<string> ApprovalModes { get; }

    public RelayCommand QuickConnectCommand => _quickConnectCommand;

    public RelayCommand RequestRemoteSupportCommand => _requestRemoteSupportCommand;

    public RelayCommand DisconnectCommand => _disconnectCommand;

    public RelayCommand CopyFileCommand => _copyFileCommand;

    public RelayCommand UploadFileCommand => _uploadFileCommand;

    public RelayCommand ToggleLocalDriveCommand => _toggleLocalDriveCommand;

    public string BuildProfile => "Framework: .NET 9";

    public string ArchitectureProfile => "Target: win-x64 only";

    public string UiArchitecture => "Pattern: MVVM";

    public string GitVersionLabel
    {
        get
        {
            var attributes = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>();
            string count = attributes.FirstOrDefault(a => a.Key == "GitCommitCount")?.Value ?? "0";
            string hash = attributes.FirstOrDefault(a => a.Key == "GitCommitHash")?.Value ?? "Unknown";
            return $"Commits: {count} | Hash: {hash}";
        }
    }

    public int DeviceCount => Devices.Count;

    public int OnlineDeviceCount => Devices.Count(device => device.Status == DeviceStatus.Online);

    public DeviceModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value) && value is not null)
            {
                QuickConnectDeviceId = value.DeviceId;
                NotifyCommandStates();
            }
        }
    }

    public string QuickConnectDeviceId
    {
        get => _quickConnectDeviceId;
        set
        {
            if (SetProperty(ref _quickConnectDeviceId, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string SelectedApprovalMode
    {
        get => _selectedApprovalMode;
        set => SetProperty(ref _selectedApprovalMode, value);
    }

    public string ActiveSessionTitle
    {
        get => _activeSessionTitle;
        private set => SetProperty(ref _activeSessionTitle, value);
    }

    public string ActiveSessionDetail
    {
        get => _activeSessionDetail;
        private set => SetProperty(ref _activeSessionDetail, value);
    }

    public string ActiveSessionStatus
    {
        get => _activeSessionStatus;
        private set
        {
            if (SetProperty(ref _activeSessionStatus, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string LastActionSummary
    {
        get => _lastActionSummary;
        private set => SetProperty(ref _lastActionSummary, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string TransferSummary
    {
        get => _transferSummary;
        private set => SetProperty(ref _transferSummary, value);
    }

    public bool IsClipboardSyncEnabled
    {
        get => _isClipboardSyncEnabled;
        set => SetProperty(ref _isClipboardSyncEnabled, value);
    }

    public bool IsCtrlCopyEnabled
    {
        get => _isCtrlCopyEnabled;
        set
        {
            if (SetProperty(ref _isCtrlCopyEnabled, value))
            {
                NotifyCommandStates();
                TransferSummary = BuildTransferSummary();
            }
        }
    }

    public bool IsLocalDriveRedirectEnabled
    {
        get => _isLocalDriveRedirectEnabled;
        set
        {
            if (SetProperty(ref _isLocalDriveRedirectEnabled, value))
            {
                TransferSummary = BuildTransferSummary();
            }
        }
    }

    public bool IsReconnectEnabled
    {
        get => _isReconnectEnabled;
        set => SetProperty(ref _isReconnectEnabled, value);
    }

    public int ConnectionQualityPercent
    {
        get => _connectionQualityPercent;
        set => SetProperty(ref _connectionQualityPercent, value);
    }

    public string ConnectionQualitySummary
    {
        get => _connectionQualitySummary;
        private set => SetProperty(ref _connectionQualitySummary, value);
    }

    private bool CanConnect() => !string.IsNullOrWhiteSpace(QuickConnectDeviceId);

    private void QuickConnect()
    {
        var device = ResolveTargetDevice();
        var snapshot = _remoteSessionService.CreateQuickConnection(device, SelectedApprovalMode);
        ApplySnapshot(snapshot);
        AddLog("Quick Connect", $"{device?.Name ?? QuickConnectDeviceId} 장치로 빠른 연결 흐름을 시작했습니다.", $"Approval: {SelectedApprovalMode}");
    }

    private void RequestRemoteSupport()
    {
        var snapshot = _remoteSessionService.CreateSupportSession(SelectedDevice);
        ApplySnapshot(snapshot);
        AddLog("Support Request", $"{SelectedDevice?.Name ?? "Unknown Device"} 장치에 승인 기반 지원 세션을 요청했습니다.", "Mode: Support request");
    }

    private void Disconnect()
    {
        ActiveSessionTitle = "No active session";
        ActiveSessionDetail = "세션이 종료되었습니다. 다른 장치를 선택하거나 Quick Connect를 다시 시작할 수 있습니다.";
        ActiveSessionStatus = "Idle";
        ConnectionQualityPercent = 0;
        ConnectionQualitySummary = "No active connection";
        LastActionSummary = "Disconnected current session";
        StatusMessage = "Remote session closed gracefully.";
        TransferSummary = BuildTransferSummary();
        AddLog("Session Closed", "사용자가 현재 원격 세션을 종료했습니다.", "Termination: user initiated");
    }

    private void CopyFile()
    {
        var target = SelectedDevice?.Name ?? QuickConnectDeviceId;
        TransferSummary = $"Ctrl+C / Ctrl+V 기반 파일 복사 준비 완료. 대상 장치: {target}";
        LastActionSummary = "Prepared clipboard file copy";
        StatusMessage = "Clipboard-assisted transfer pathway is active.";
        AddLog("Clipboard File Copy", "클립보드 기반 파일 복사/붙여넣기 흐름을 활성화했습니다.", $"Target: {target}");
    }

    private async void UploadFile()
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog();
        openFileDialog.Title = "Select File to Upload to Remote PC";
        if (openFileDialog.ShowDialog() == true)
        {
            try
            {
                string filePath = openFileDialog.FileName;
                TransferSummary = $"Uploading {System.IO.Path.GetFileName(filePath)}...";
                await _remoteSessionService.UploadFileAsync(filePath);
                TransferSummary = "File uploaded successfully.";
                AddLog("File Transfer", $"파일 전송 완료: {System.IO.Path.GetFileName(filePath)}", "Direction: Upload");
            }
            catch (System.Exception ex)
            {
                TransferSummary = $"Upload error: {ex.Message}";
            }
        }
    }

    private void ToggleLocalDrive()
    {
        IsLocalDriveRedirectEnabled = !IsLocalDriveRedirectEnabled;
        LastActionSummary = IsLocalDriveRedirectEnabled ? "Enabled local drive redirect" : "Disabled local drive redirect";
        StatusMessage = IsLocalDriveRedirectEnabled
            ? "Local drives are exposed to the remote session."
            : "Local drives are hidden from the remote session.";
        AddLog("Drive Redirect", LastActionSummary, $"Target: {SelectedDevice?.Name ?? QuickConnectDeviceId}");
    }

    private DeviceModel? ResolveTargetDevice()
    {
        var matched = Devices.FirstOrDefault(device => device.DeviceId.Equals(QuickConnectDeviceId, StringComparison.OrdinalIgnoreCase));
        if (matched is not null)
        {
            SelectedDevice = matched;
        }

        return matched ?? SelectedDevice;
    }

    private void ApplySnapshot(ConnectionSnapshot snapshot)
    {
        ActiveSessionTitle = snapshot.SessionTitle;
        ActiveSessionDetail = snapshot.SessionDetail;
        ActiveSessionStatus = snapshot.Status;
        ConnectionQualityPercent = snapshot.QualityPercent;
        ConnectionQualitySummary = snapshot.QualitySummary;
        LastActionSummary = snapshot.SessionTitle;
        StatusMessage = snapshot.SessionDetail;
        TransferSummary = BuildTransferSummary();
    }

    private string BuildTransferSummary()
    {
        return $"Clipboard sync: {(IsClipboardSyncEnabled ? "On" : "Off")} / Ctrl copy: {(IsCtrlCopyEnabled ? "On" : "Off")} / Local drive: {(IsLocalDriveRedirectEnabled ? "On" : "Off")}";
    }

    private void AddLog(string title, string message, string meta)
    {
        SessionLogs.Insert(0, _remoteSessionService.CreateLog(title, message, meta));
    }

    private void NotifyCommandStates()
    {
        _quickConnectCommand.NotifyCanExecuteChanged();
        _requestRemoteSupportCommand.NotifyCanExecuteChanged();
        _disconnectCommand.NotifyCanExecuteChanged();
        _copyFileCommand.NotifyCanExecuteChanged();
        _uploadFileCommand.NotifyCanExecuteChanged();
        _toggleLocalDriveCommand.NotifyCanExecuteChanged();
        RaisePropertyChanged(nameof(OnlineDeviceCount));
        RaisePropertyChanged(nameof(DeviceCount));
    }
}
