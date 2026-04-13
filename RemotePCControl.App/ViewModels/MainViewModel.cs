using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.IO;
using RemotePCControl.App.Infrastructure;
using RemotePCControl.App.Models;
using RemotePCControl.App.Services;

namespace RemotePCControl.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IRemoteSessionService _remoteSessionService;
    private readonly ResourceMonitorService _resourceMonitorService;
    private readonly RelayCommand _quickConnectCommand;
    private readonly RelayCommand _requestRemoteSupportCommand;
    private readonly RelayCommand _disconnectCommand;
    private readonly RelayCommand _copyFileCommand;
    private readonly IRelayCommand _uploadFileCommand;
    private readonly RelayCommand _downloadFileCommand;
    private readonly RelayCommand _browseRedirectedDrivesCommand;
    private readonly RelayCommand _browseRemoteFilesCommand;
    private readonly RelayCommand<FileEntry> _navigateIntoFolderCommand;
    private readonly RelayCommand<BreadcrumbItem> _navigateToBreadcrumbCommand;
    private readonly RelayCommand<FileEntry> _downloadSelectedFileCommand;
    private readonly IRelayCommand _startRelayHostCommand;
    private readonly IRelayCommand _connectViaRelayCommand;
    private readonly RelayCommand _lockRemoteSessionCommand;
    private readonly RelayCommand _toggleRemoteInputBlockCommand;
    private readonly IRelayCommand _pasteRemoteClipboardCommand;
    private readonly RelayCommand _cancelTransferCommand;
    private readonly RelayCommand _browseDownloadPathCommand;
    private readonly RelayCommand _toggleLocalDriveCommand;
    private readonly RelayCommand _toggleFavoriteCommand;
    private readonly RelayCommand _useRecentConnectionCommand;
    private readonly RelayCommand _updateDeviceMetadataCommand;
    private readonly RelayCommand _registerManualDeviceCommand;
    private readonly RelayCommand<DeviceModel> _removeDeviceCommand;
    private DeviceModel? _selectedDevice;
    private RecentConnectionEntry? _selectedRecentConnection;
    private CaptureDisplayOption? _selectedCaptureDisplay;
    private CaptureDisplayOption? _selectedViewerDisplay;
    private CaptureRateOption? _selectedCaptureRate;
    private CompressionOption? _selectedCompression;
    private string _quickConnectDeviceId = string.Empty;
    private string _duplicateWarningMessage = string.Empty;
    private string _deviceLookupSummary = "장치 이름 또는 장치 번호를 입력해 연결 대상을 찾을 수 있습니다.";
    private string _selectedApprovalMode = "User approval";
    private string _activeSessionTitle = "No active session";
    private string _activeSessionDetail = "Select a device or enter a device ID to start a remote connection flow.";
    private string _activeSessionStatus = "Idle";
    private string _lastActionSummary = "Waiting for operator input";
    private string _statusMessage = "Session services ready.";
    private string _transferSummary = "No file operation has started yet.";
    private bool _isClipboardSyncEnabled = true;
    private bool _isCtrlCopyEnabled = true;
    private bool _isViewerPinnedToSafeDisplay = true;
    private bool _isLocalDriveRedirectEnabled = true;
    private bool _isReconnectEnabled = true;
    private int _connectionQualityPercent;
    private string _connectionQualitySummary = "No active connection";
    private string _cpuUsageText = "CPU: collecting...";
    private string _memoryUsageText = "Memory: collecting...";
    private string _currentRemotePath = string.Empty;
    private string _downloadPath = string.Empty;
    private double _downloadProgress;
    private bool _isTransferActive;
    private bool _isRemoteBrowserBusy;
    private string _remoteBrowserStatus = "원격 파일 브라우저를 열면 현재 경로와 탐색 상태가 여기에 표시됩니다.";
    private string _pendingRemoteBrowsePath = string.Empty;
    private string _editDeviceName = string.Empty;
    private string _editDeviceDescription = string.Empty;
    private string _manualRegisterIP = "127.0.0.1";
    private int _manualRegisterPort = 9999;
    private bool _isEditPanelVisible;
    private bool _isRemoteInputBlocked;
    private int _currentMenuIndex;
    private Views.RemoteFileBrowserWindow? _remoteFileBrowserWindow;
    
    public int CurrentMenuIndex
    {
        get => _currentMenuIndex;
        set => SetProperty(ref _currentMenuIndex, value);
    }

    public MainViewModel(IRemoteSessionService remoteSessionService, ResourceMonitorService resourceMonitorService)
    {
        _remoteSessionService = remoteSessionService;
        _resourceMonitorService = resourceMonitorService;
        Devices = new ObservableCollection<DeviceModel>(_remoteSessionService.GetDevices());
        CaptureDisplays = new ObservableCollection<CaptureDisplayOption>(_remoteSessionService.GetCaptureDisplays());
        ViewerDisplays = new ObservableCollection<CaptureDisplayOption>(CreateViewerDisplayOptions(_remoteSessionService.GetViewerDisplays()));
        CaptureRates = new ObservableCollection<CaptureRateOption>(_remoteSessionService.GetCaptureRates());
        CompressionOptions = new ObservableCollection<CompressionOption>(_remoteSessionService.GetCompressionOptions());
        SessionLogs = new ObservableCollection<SessionLogEntry>(_remoteSessionService.GetSeedLogs().OrderByDescending(log => log.Timestamp));
        RecentConnections = new ObservableCollection<RecentConnectionEntry>(_remoteSessionService.GetRecentConnections());
        _remoteSessionService.SessionLogAdded += HandleSessionLogAdded;
        _remoteSessionService.DevicesChanged += HandleDevicesChanged;
        _remoteSessionService.RecentConnectionsChanged += HandleRecentConnectionsChanged;
        _remoteSessionService.SessionSnapshotChanged += HandleSessionSnapshotChanged;
        _remoteSessionService.FileSystemListReceived += HandleFileSystemListReceived;
        _remoteSessionService.FileTransferProgressChanged += HandleFileTransferProgressChanged;
        _resourceMonitorService.SnapshotUpdated += HandleResourceSnapshotUpdated;
        ApprovalModes = ["User approval", "Pre-approved device", "Support request"];

        _quickConnectCommand = new RelayCommand(QuickConnect, CanConnect);
        _requestRemoteSupportCommand = new RelayCommand(RequestRemoteSupport, () => SelectedDevice is not null);
        _disconnectCommand = new RelayCommand(Disconnect, () => ActiveSessionStatus is not "Idle");
        _copyFileCommand = new RelayCommand(CopyFile, () => SelectedDevice is not null && IsCtrlCopyEnabled);
        _uploadFileCommand = new AsyncRelayCommand(UploadFileAsync, () => ActiveSessionStatus is not "Idle");
        _downloadFileCommand = new RelayCommand(DownloadFile, () => ActiveSessionStatus is not "Idle");
        _browseRedirectedDrivesCommand = new RelayCommand(BrowseRedirectedDrives, () => ActiveSessionStatus is not "Idle" && !IsRemoteBrowserBusy);
        _browseRemoteFilesCommand = new RelayCommand(BrowseRemoteFiles, () => ActiveSessionStatus is not "Idle" && !IsRemoteBrowserBusy);
        _navigateIntoFolderCommand = new RelayCommand<FileEntry>(NavigateIntoFolder, entry => entry?.IsDirectory == true && !IsRemoteBrowserBusy);
        _navigateToBreadcrumbCommand = new RelayCommand<BreadcrumbItem>(NavigateToBreadcrumb, item => item is not null && !IsRemoteBrowserBusy);
        _downloadSelectedFileCommand = new RelayCommand<FileEntry>(DownloadSelectedFile, entry => entry is not null && !entry.IsDirectory && !IsTransferActive && !IsRemoteBrowserBusy);
        _startRelayHostCommand = new AsyncRelayCommand(StartRelayHostAsync);
        _connectViaRelayCommand = new AsyncRelayCommand(ConnectViaRelayAsync);
        _toggleLocalDriveCommand = new RelayCommand(ToggleLocalDrive, () => SelectedDevice is not null);
        _toggleFavoriteCommand = new RelayCommand(ToggleFavorite, () => SelectedDevice is not null);
        _useRecentConnectionCommand = new RelayCommand(UseRecentConnection, () => SelectedRecentConnection is not null);
        _updateDeviceMetadataCommand = new RelayCommand(UpdateDeviceMetadata, () => SelectedDevice is not null);
        _registerManualDeviceCommand = new RelayCommand(RegisterManualDevice, () => !string.IsNullOrWhiteSpace(ManualRegisterIP));
        _lockRemoteSessionCommand = new RelayCommand(LockRemoteSession, () => ActiveSessionStatus != "Idle");
        _toggleRemoteInputBlockCommand = new RelayCommand(ToggleRemoteInputBlock, () => ActiveSessionStatus != "Idle");
        _pasteRemoteClipboardCommand = new AsyncRelayCommand(PasteRemoteClipboardAsync, () => ActiveSessionStatus == "Connected" && IsCtrlCopyEnabled);
        _cancelTransferCommand = new RelayCommand(CancelTransfer, () => IsTransferActive);
        _browseDownloadPathCommand = new RelayCommand(BrowseDownloadPath);
        _removeDeviceCommand = new RelayCommand<DeviceModel>(RemoveDevice);

        DownloadPath = _remoteSessionService.GetDownloadPath();

        SelectedDevice = Devices.FirstOrDefault();
        SelectedCaptureDisplay = CaptureDisplays.FirstOrDefault();
        SelectedViewerDisplay = ViewerDisplays.FirstOrDefault();
        SelectedCaptureRate = CaptureRates.LastOrDefault();
        SelectedCompression = CompressionOptions.FirstOrDefault(option => option.EncodingMode == 0x01 && option.Quality == 85) ?? CompressionOptions.FirstOrDefault();
        QuickConnectDeviceId = SelectedDevice?.DeviceId ?? string.Empty;

        DuplicateCheckResult duplicateCheckResult = _remoteSessionService.GetDuplicateCheckResult();
        if (duplicateCheckResult.IsDuplicate)
        {
            DuplicateWarningMessage = $"경고: 동일 로컬 네트워크에서 중복 장치 식별자가 {duplicateCheckResult.Conflicts.Count}건 감지되었습니다.";
        }

        _remoteSessionService.SetAutoReconnect(_isReconnectEnabled);
        _remoteSessionService.SetClipboardSyncEnabled(_isClipboardSyncEnabled);
        _remoteSessionService.SetCtrlCopyEnabled(_isCtrlCopyEnabled);
    }

    public ObservableCollection<DeviceModel> Devices { get; }

    public ObservableCollection<CaptureDisplayOption> CaptureDisplays { get; }

    public ObservableCollection<CaptureDisplayOption> ViewerDisplays { get; }

    public ObservableCollection<CaptureRateOption> CaptureRates { get; }

    public ObservableCollection<CompressionOption> CompressionOptions { get; }

    public ObservableCollection<SessionLogEntry> SessionLogs { get; }

    public ObservableCollection<RecentConnectionEntry> RecentConnections { get; }
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [new BreadcrumbItem("Root", string.Empty)];
    public ObservableCollection<FileEntry> RemoteFiles { get; } = [];

    private string _relayCode = string.Empty;
    public string RelayCode
    {
        get => _relayCode;
        set => SetProperty(ref _relayCode, value);
    }

    private string _relayIp = "127.0.0.1"; // Default for local test
    public string RelayIp
    {
        get => _relayIp;
        set => SetProperty(ref _relayIp, value);
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        set
        {
            if (SetProperty(ref _downloadProgress, value))
            {
                IsTransferActive = value > 0 && value < 100;
            }
        }
    }

    public bool IsTransferActive
    {
        get => _isTransferActive;
        set
        {
            if (SetProperty(ref _isTransferActive, value))
            {
                _cancelTransferCommand.NotifyCanExecuteChanged();
                _downloadSelectedFileCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsRemoteBrowserBusy
    {
        get => _isRemoteBrowserBusy;
        set
        {
            if (SetProperty(ref _isRemoteBrowserBusy, value))
            {
                _browseRedirectedDrivesCommand.NotifyCanExecuteChanged();
                _browseRemoteFilesCommand.NotifyCanExecuteChanged();
                _navigateIntoFolderCommand.NotifyCanExecuteChanged();
                _navigateToBreadcrumbCommand.NotifyCanExecuteChanged();
                _downloadSelectedFileCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string RemoteBrowserStatus
    {
        get => _remoteBrowserStatus;
        set => SetProperty(ref _remoteBrowserStatus, value);
    }

    public string DownloadPath
    {
        get => _downloadPath;
        set
        {
            if (SetProperty(ref _downloadPath, value))
            {
                _remoteSessionService.SetDownloadPath(value);
            }
        }
    }

    public string CurrentRemotePath
    {
        get => _currentRemotePath;
        set
        {
            if (SetProperty(ref _currentRemotePath, value))
            {
                UpdateBreadcrumbs(value);
            }
        }
    }

    public IReadOnlyList<string> ApprovalModes { get; }

    public RelayCommand QuickConnectCommand => _quickConnectCommand;

    public RelayCommand RequestRemoteSupportCommand => _requestRemoteSupportCommand;

    public RelayCommand DisconnectCommand => _disconnectCommand;

    public RelayCommand CopyFileCommand => _copyFileCommand;

    public IRelayCommand UploadFileCommand => _uploadFileCommand;

    public RelayCommand DownloadFileCommand => _downloadFileCommand;

    public RelayCommand BrowseRedirectedDrivesCommand => _browseRedirectedDrivesCommand;

    public RelayCommand BrowseRemoteFilesCommand => _browseRemoteFilesCommand;
    public RelayCommand<FileEntry> NavigateIntoFolderCommand => _navigateIntoFolderCommand;
    public RelayCommand<BreadcrumbItem> NavigateToBreadcrumbCommand => _navigateToBreadcrumbCommand;
    public RelayCommand<FileEntry> DownloadSelectedFileCommand => _downloadSelectedFileCommand;
    public IRelayCommand StartRelayHostCommand => _startRelayHostCommand;
    public IRelayCommand ConnectViaRelayCommand => _connectViaRelayCommand;
    public RelayCommand<DeviceModel> RemoveDeviceCommand => _removeDeviceCommand;
    public RelayCommand ToggleLocalDriveCommand => _toggleLocalDriveCommand;

    public RelayCommand ToggleFavoriteCommand => _toggleFavoriteCommand;
    public RelayCommand UseRecentConnectionCommand => _useRecentConnectionCommand;
    public RelayCommand UpdateDeviceMetadataCommand => _updateDeviceMetadataCommand;
    public RelayCommand RegisterManualDeviceCommand => _registerManualDeviceCommand;
    public RelayCommand LockRemoteSessionCommand => _lockRemoteSessionCommand;
    public RelayCommand ToggleRemoteInputBlockCommand => _toggleRemoteInputBlockCommand;
    public IRelayCommand PasteRemoteClipboardCommand => _pasteRemoteClipboardCommand;
    public RelayCommand CancelTransferCommand => _cancelTransferCommand;
    public RelayCommand BrowseDownloadPathCommand => _browseDownloadPathCommand;

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

    public string SelectedDeviceFavoriteLabel => SelectedDevice?.IsFavorite == true ? "Remove Favorite" : "Add Favorite";

    public string CpuUsageText
    {
        get => _cpuUsageText;
        private set => SetProperty(ref _cpuUsageText, value);
    }

    public string MemoryUsageText
    {
        get => _memoryUsageText;
        private set => SetProperty(ref _memoryUsageText, value);
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
                RaisePropertyChanged(nameof(SelectedDeviceFavoriteLabel));
                
                if (_selectedDevice != null)
                {
                    EditDeviceName = _selectedDevice.Name;
                    EditDeviceDescription = _selectedDevice.Description;
                }
                
                QuickConnectDeviceId = value.DeviceId;
                NotifyCommandStates();
            }
        }
    }

    public RecentConnectionEntry? SelectedRecentConnection
    {
        get => _selectedRecentConnection;
        set
        {
            if (SetProperty(ref _selectedRecentConnection, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public CaptureDisplayOption? SelectedCaptureDisplay
    {
        get => _selectedCaptureDisplay;
        set
        {
            if (SetProperty(ref _selectedCaptureDisplay, value) && value is not null)
            {
                _remoteSessionService.SetCaptureDisplay(value.DisplayId);
                TransferSummary = BuildTransferSummary();
            }
        }
    }

    public CaptureDisplayOption? SelectedViewerDisplay
    {
        get => _selectedViewerDisplay;
        set
        {
            if (SetProperty(ref _selectedViewerDisplay, value))
            {
                _remoteSessionService.SetViewerDisplay(value?.DisplayId);
                TransferSummary = BuildTransferSummary();
            }
        }
    }

    public CaptureRateOption? SelectedCaptureRate
    {
        get => _selectedCaptureRate;
        set
        {
            if (SetProperty(ref _selectedCaptureRate, value) && value is not null)
            {
                _remoteSessionService.SetCaptureRate(value.FramesPerSecond);
                TransferSummary = BuildTransferSummary();
            }
        }
    }

    public CompressionOption? SelectedCompression
    {
        get => _selectedCompression;
        set
        {
            if (SetProperty(ref _selectedCompression, value) && value is not null)
            {
                _remoteSessionService.SetCompression(value.EncodingMode, value.Quality);
                TransferSummary = BuildTransferSummary();
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

    public string DuplicateWarningMessage
    {
        get => _duplicateWarningMessage;
        private set => SetProperty(ref _duplicateWarningMessage, value);
    }

    public string DeviceLookupSummary
    {
        get => _deviceLookupSummary;
        private set => SetProperty(ref _deviceLookupSummary, value);
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
                RaisePropertyChanged(nameof(ActiveSessionStatusColor));
            }
        }
    }

    public string ActiveSessionStatusColor => ActiveSessionStatus switch
    {
        "Connected" => "SuccessBrush",
        "Connecting" => "WarningBrush",
        "Pending" => "WarningBrush",
        "Idle" => "SubtleTextBrush",
        _ => "ErrorBrush"
    };

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

    public string EditDeviceName
    {
        get => _editDeviceName;
        set => SetProperty(ref _editDeviceName, value);
    }

    public string EditDeviceDescription
    {
        get => _editDeviceDescription;
        set => SetProperty(ref _editDeviceDescription, value);
    }

    public string ManualRegisterIP
    {
        get => _manualRegisterIP;
        set => SetProperty(ref _manualRegisterIP, value);
    }

    public int ManualRegisterPort
    {
        get => _manualRegisterPort;
        set => SetProperty(ref _manualRegisterPort, value);
    }

    public bool IsEditPanelVisible
    {
        get => _isEditPanelVisible;
        set => SetProperty(ref _isEditPanelVisible, value);
    }

    public bool IsRemoteInputBlocked
    {
        get => _isRemoteInputBlocked;
        set => SetProperty(ref _isRemoteInputBlocked, value);
    }

    public bool IsClipboardSyncEnabled
    {
        get => _isClipboardSyncEnabled;
        set
        {
            if (SetProperty(ref _isClipboardSyncEnabled, value))
            {
                _remoteSessionService.SetClipboardSyncEnabled(value);
                TransferSummary = BuildTransferSummary();
            }
        }
    }

    public bool IsCtrlCopyEnabled
    {
        get => _isCtrlCopyEnabled;
        set
        {
            if (SetProperty(ref _isCtrlCopyEnabled, value))
            {
                _remoteSessionService.SetCtrlCopyEnabled(value);
                NotifyCommandStates();
                TransferSummary = BuildTransferSummary();
            }
        }
    }

    public bool IsViewerPinnedToSafeDisplay
    {
        get => _isViewerPinnedToSafeDisplay;
        set
        {
            if (SetProperty(ref _isViewerPinnedToSafeDisplay, value))
            {
                _remoteSessionService.SetKeepViewerOnSafeDisplay(value);
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
                _remoteSessionService.SetLocalDriveRedirectEnabled(value);
                TransferSummary = BuildTransferSummary();
            }
        }
    }

    public bool IsReconnectEnabled
    {
        get => _isReconnectEnabled;
        set
        {
            if (SetProperty(ref _isReconnectEnabled, value))
            {
                _remoteSessionService.SetAutoReconnect(value);
            }
        }
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
        DeviceResolutionResult resolution = _remoteSessionService.ResolveDevice(QuickConnectDeviceId);
        DeviceModel? device = resolution.ResolvedDevice;

        if (resolution.Status == DeviceResolutionStatus.NotFound)
        {
            ActiveSessionTitle = "Device not found";
            ActiveSessionDetail = $"'{QuickConnectDeviceId}' 식별자에 해당하는 장치를 찾지 못했습니다.";
            ActiveSessionStatus = "Not Found";
            ConnectionQualityPercent = 0;
            ConnectionQualitySummary = "No matching device";
            LastActionSummary = "Device lookup failed";
            StatusMessage = "브로드캐스트 탐색 결과와 등록 목록에서 일치하는 장치를 찾지 못했습니다.";
            DeviceLookupSummary = "일치하는 장치가 없습니다.";
            AddLog("Quick Connect Failed", $"{QuickConnectDeviceId} 식별자에 해당하는 장치를 찾지 못했습니다.", "Resolution: not found");
            return;
        }

        if (resolution.Status == DeviceResolutionStatus.MultipleMatches)
        {
            DeviceLookupSummary = $"동일 식별자 후보 {resolution.CandidateDevices.Count}건이 발견되었습니다. 목록에서 장치를 선택해 주세요.";
            DuplicateWarningMessage = "동일한 장치 이름 또는 번호가 여러 대에서 발견되었습니다.";
            RefreshDevices(resolution.CandidateDevices);
            AddLog("Quick Connect Deferred", $"{QuickConnectDeviceId} 식별자에 대해 여러 장치가 발견되었습니다.", $"Candidates: {resolution.CandidateDevices.Count}");
            return;
        }

        if (device is not null)
        {
            SelectedDevice = device;
            DeviceLookupSummary = $"{device.Name} ({device.DeviceCode}) 장치로 연결을 시도합니다.";
            if (!_remoteSessionService.GetDuplicateCheckResult().IsDuplicate)
            {
                DuplicateWarningMessage = string.Empty;
            }
        }

        if (SelectedCaptureDisplay is not null)
        {
            _remoteSessionService.SetCaptureDisplay(SelectedCaptureDisplay.DisplayId);
        }

        _remoteSessionService.SetViewerDisplay(SelectedViewerDisplay?.DisplayId);
        if (SelectedCaptureRate is not null)
        {
            _remoteSessionService.SetCaptureRate(SelectedCaptureRate.FramesPerSecond);
        }

        if (SelectedCompression is not null)
        {
            _remoteSessionService.SetCompression(SelectedCompression.EncodingMode, SelectedCompression.Quality);
        }

        ConnectionSnapshot snapshot = _remoteSessionService.CreateQuickConnection(device, SelectedApprovalMode);
        ApplySnapshot(snapshot);
        AddLog("Quick Connect", $"{device?.Name ?? QuickConnectDeviceId} 장치로 빠른 연결 흐름을 시작했습니다.", $"Approval: {SelectedApprovalMode} / Target: {device?.DeviceCode ?? QuickConnectDeviceId}");
    }

    private void RequestRemoteSupport()
    {
        ConnectionSnapshot snapshot = _remoteSessionService.CreateSupportSession(SelectedDevice);
        ApplySnapshot(snapshot);
        AddLog("Support Request", $"{SelectedDevice?.Name ?? "Unknown Device"} 장치에 승인 기반 지원 세션을 요청했습니다.", "Mode: Support request");
    }

    private void Disconnect()
    {
        _remoteSessionService.DisconnectCurrentSession();
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
        string target = SelectedDevice?.Name ?? QuickConnectDeviceId;
        TransferSummary = $"Ctrl+C / Ctrl+V 기반 파일 복사 준비 완료. 대상 장치: {target}";
        LastActionSummary = "Prepared clipboard file copy";
        StatusMessage = "Clipboard-assisted transfer pathway is active.";
        AddLog("Clipboard File Copy", "클립보드 기반 파일 복사/붙여넣기 흐름을 활성화했습니다.", $"Target: {target}");
    }

    private async Task UploadFileAsync()
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog();
        openFileDialog.Title = "Select File to Upload to Remote PC";
        if (openFileDialog.ShowDialog() == true)
        {
            try
            {
                string filePath = openFileDialog.FileName;
                TransferSummary = $"Uploading {System.IO.Path.GetFileName(filePath)}...";
                await _remoteSessionService.UploadFileAsync(filePath).ConfigureAwait(false);
                TransferSummary = "File uploaded successfully.";
                StatusMessage = "선택한 파일을 원격지로 업로드했습니다.";
                AddLog("File Transfer", $"파일 전송 완료: {System.IO.Path.GetFileName(filePath)}", "Direction: Upload");
            }
            catch (Exception ex)
            {
                TransferSummary = $"Upload error: {ex.Message}";
                StatusMessage = $"파일 업로드 중 오류가 발생했습니다. {ex.Message}";
                AddLog("File Upload Error", $"파일 업로드에 실패했습니다. {ex.Message}", $"Source: {openFileDialog.FileName}");
            }
        }
    }

    private void DownloadFile()
    {
        try
        {
            RequestRemoteFileListing(
                string.Empty,
                "원격 파일 브라우저를 열고 루트 목록을 요청했습니다. 다운로드할 파일을 선택해 주세요.",
                "원격 파일 다운로드 대상 탐색을 시작했습니다.",
                "File Download Browser Opened",
                "하드코딩된 테스트 경로 대신 원격 파일 브라우저를 통해 다운로드 대상을 선택하도록 전환했습니다.",
                "Direction: Download Selection");
        }
        catch (Exception ex)
        {
            TransferSummary = $"Download request error: {ex.Message}";
        }
    }

    private void BrowseRedirectedDrives()
    {
        try
        {
            RequestRemoteFileListing(
                string.Empty,
                "리디렉션된 드라이브 루트 목록을 요청했습니다.",
                "상대방의 리디렉션된 드라이브 목록을 요청했습니다.",
                "Drive Browse Requested",
                "상대방의 리디렉션된 드라이브 목록을 요청했습니다.",
                "Remote FS");
        }
        catch (Exception ex)
        {
            TransferSummary = $"FS List error: {ex.Message}";
        }
    }

    private void BrowseRemoteFiles()
    {
        try
        {
            RequestRemoteFileListing(
                string.Empty,
                "원격 파일 브라우저를 열고 루트 목록을 요청했습니다.",
                "원격 파일 브라우저가 루트 목록을 요청 중입니다.",
                "Remote Explorer",
                "원격 장치의 파일 탐색기 창을 열었습니다.",
                "Remote FS");
        }
        catch (Exception ex)
        {
            TransferSummary = $"Explorer error: {ex.Message}";
        }
    }

    private void NavigateIntoFolder(FileEntry? entry)
    {
        if (entry == null || !entry.IsDirectory) return;
        RequestRemoteFileListing(
            entry.Path,
            $"폴더를 여는 중입니다: {entry.Name}",
            $"원격 폴더 `{entry.Name}` 목록을 요청했습니다.",
            "Remote Explorer Navigate",
            $"원격 폴더 목록 요청: {entry.Path}",
            "Remote FS / Folder");
    }

    private void UpdateBreadcrumbs(string path)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Breadcrumbs.Clear();
            Breadcrumbs.Add(new BreadcrumbItem("Root", string.Empty));
            if (string.IsNullOrWhiteSpace(path)) return;

            string[] parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            string cumulativePath = string.Empty;
            
            // 윈도우 드라이브 대응 (C:)
            bool isFirst = true;

            foreach (var part in parts)
            {
                if (isFirst && part.Contains(':'))
                {
                    cumulativePath = part + "\\";
                    isFirst = false;
                }
                else
                {
                    cumulativePath = Path.Combine(cumulativePath, part);
                }
                Breadcrumbs.Add(new BreadcrumbItem(part, cumulativePath));
            }
        });
    }

    private void NavigateToBreadcrumb(BreadcrumbItem? item)
    {
        if (item == null) return;
        RequestRemoteFileListing(
            item.Path,
            string.IsNullOrWhiteSpace(item.Path) ? "루트 경로로 이동 중입니다." : $"경로 이동 중: {item.Path}",
            string.IsNullOrWhiteSpace(item.Path) ? "원격 브라우저가 루트 경로를 다시 요청했습니다." : $"원격 브라우저가 `{item.Path}` 경로를 요청했습니다.",
            "Remote Explorer Breadcrumb",
            string.IsNullOrWhiteSpace(item.Path) ? "원격 브라우저가 루트 경로로 이동했습니다." : $"원격 브라우저가 `{item.Path}` 경로로 이동했습니다.",
            "Remote FS / Breadcrumb");
    }

    private void DownloadSelectedFile(FileEntry? entry)
    {
        _ = DownloadSelectedFileAsync(entry);
    }

    private async Task DownloadSelectedFileAsync(FileEntry? entry)
    {
        if (entry == null || entry.IsDirectory) return;
        if (IsTransferActive)
        {
            TransferSummary = "다른 파일 전송이 진행 중입니다. 현재 전송이 끝난 뒤 다시 시도해 주세요.";
            RemoteBrowserStatus = "파일 다운로드 요청을 보류했습니다. 진행 중인 전송이 끝나야 새 다운로드를 시작할 수 있습니다.";
            StatusMessage = "동시에 여러 다운로드를 시작하지 않도록 차단했습니다.";
            AddLog("File Download Deferred", $"전송 중인 작업 때문에 `{entry.Name}` 다운로드를 보류했습니다.", "File / Inbound / Busy");
            return;
        }

        try
        {
            DownloadProgress = 0;
            IsTransferActive = true;
            TransferSummary = $"다운로드 요청 중: {entry.Name}";
            RemoteBrowserStatus = $"선택한 파일 `{entry.Name}` 다운로드를 요청했습니다.";
            StatusMessage = "원격 파일 메타데이터를 기다리는 중입니다.";
            await _remoteSessionService.DownloadFileAsync(entry.Path).ConfigureAwait(false);
            LastActionSummary = $"Download started: {entry.Name}";
            AddLog("File Download", $"원격 파일 다운로드 시작: {entry.Name}", $"Source: {entry.Path}");
        }
        catch (Exception ex)
        {
            TransferSummary = $"Download error: {ex.Message}";
            RemoteBrowserStatus = $"다운로드 시작 실패: {entry.Name}";
            StatusMessage = $"파일 다운로드 요청을 시작하지 못했습니다. {ex.Message}";
            IsTransferActive = false;
            DownloadProgress = 0;
        }
    }

    private void ToggleLocalDrive()
    {
        IsLocalDriveRedirectEnabled = !IsLocalDriveRedirectEnabled;
        LastActionSummary = IsLocalDriveRedirectEnabled ? "Enabled local drive redirect" : "Disabled local drive redirect";
        StatusMessage = IsLocalDriveRedirectEnabled
            ? "Local drives are exposed to the remote session."
            : "Local drives are hidden from the remote session.";
        if (!IsLocalDriveRedirectEnabled)
        {
            IsRemoteBrowserBusy = false;
            _pendingRemoteBrowsePath = string.Empty;
            RemoteBrowserStatus = "로컬 드라이브 리디렉션이 꺼져 있어 원격 파일 브라우저가 비활성화되었습니다.";
            TransferSummary = "로컬 드라이브 리디렉션이 비활성화되어 원격 파일 탐색을 중지했습니다.";
        }
        else
        {
            RemoteBrowserStatus = "로컬 드라이브 리디렉션이 활성화되었습니다. 원격 파일 브라우저를 다시 열 수 있습니다.";
        }
        AddLog("Drive Redirect", LastActionSummary, $"Target: {SelectedDevice?.Name ?? QuickConnectDeviceId}");
    }

    private async Task PasteRemoteClipboardAsync()
    {
        await _remoteSessionService.DownloadClipboardFilesAsync().ConfigureAwait(false);
    }

    private void BrowseDownloadPath()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        dialog.Title = "Select File Download Destination";
        if (dialog.ShowDialog() == true)
        {
            DownloadPath = dialog.FolderName;
        }
    }

    private void CancelTransfer()
    {
        _remoteSessionService.CancelCurrentFileTransfer();
        DownloadProgress = 0;
        IsTransferActive = false;
        TransferSummary = "File transfer cancelled by user.";
        RemoteBrowserStatus = "사용자가 현재 파일 전송을 취소했습니다.";
    }

    private void ToggleFavorite()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        _remoteSessionService.ToggleFavorite(SelectedDevice.InternalGuid);
        LastActionSummary = SelectedDevice.IsFavorite ? "Marked device as favorite" : "Removed device from favorites";
        StatusMessage = SelectedDevice.IsFavorite
            ? $"{SelectedDevice.Name} 장치를 즐겨찾기로 저장했습니다."
            : $"{SelectedDevice.Name} 장치를 즐겨찾기에서 제거했습니다.";
        RaisePropertyChanged(nameof(SelectedDeviceFavoriteLabel));
        AddLog("Favorite Updated", StatusMessage, $"Target: {SelectedDevice.DeviceCode}");
    }

    private void UpdateDeviceMetadata()
    {
        if (SelectedDevice is null) return;
        _remoteSessionService.UpdateDeviceMetadata(SelectedDevice.InternalGuid, EditDeviceName, EditDeviceDescription);
        LastActionSummary = "Updated device information";
        AddLog("Device Info Updated", $"장치 정보가 사용자에 의해 수정되었습니다: {SelectedDevice.Name}", $"GUID: {SelectedDevice.InternalGuid}");
    }

    private void RegisterManualDevice()
    {
        if (string.IsNullOrWhiteSpace(ManualRegisterIP)) return;
        _remoteSessionService.RegisterManualDevice(ManualRegisterIP, ManualRegisterPort);
        LastActionSummary = $"Manually registered device: {ManualRegisterIP}";
        AddLog("Manual Registration", $"장치를 수동으로 등록했습니다: {ManualRegisterIP}", $"Port: {ManualRegisterPort}");
    }

    private void LockRemoteSession()
    {
        _remoteSessionService.LockRemoteSession();
        LastActionSummary = "Requested remote screen lock";
        AddLog("Screen Lock", "원격 화면 잠금을 요청했습니다.", "Security");
    }

    private void ToggleRemoteInputBlock()
    {
        IsRemoteInputBlocked = !IsRemoteInputBlocked;
        _remoteSessionService.SetRemoteInputBlocked(IsRemoteInputBlocked);
        LastActionSummary = IsRemoteInputBlocked ? "Requested input block" : "Requested input unblock";
        AddLog("Input Block", IsRemoteInputBlocked ? "원격 입력 차단을 요청했습니다." : "원격 입력 허용을 요청했습니다.", "Security");
    }

    private void UseRecentConnection()
    {
        if (SelectedRecentConnection is null)
        {
            return;
        }

        DeviceModel? device = Devices.FirstOrDefault(item =>
            string.Equals(item.InternalGuid, SelectedRecentConnection.DeviceInternalGuid, StringComparison.OrdinalIgnoreCase));
        if (device is not null)
        {
            SelectedDevice = device;
            QuickConnectDeviceId = device.DeviceCode;
            SelectedApprovalMode = SelectedRecentConnection.LastApprovalMode;
            DeviceLookupSummary = $"{device.Name} 최근 연결 기록을 불러왔습니다.";
            LastActionSummary = "Loaded recent connection";
            StatusMessage = $"{device.Name} 장치의 최근 연결 설정을 빠른 연결 입력값으로 복원했습니다.";
            return;
        }

        QuickConnectDeviceId = SelectedRecentConnection.DeviceCode;
        SelectedApprovalMode = SelectedRecentConnection.LastApprovalMode;
        DeviceLookupSummary = $"{SelectedRecentConnection.DeviceName} 최근 연결 기록을 입력값으로 복원했습니다.";
        LastActionSummary = "Loaded recent connection";
        StatusMessage = "현재 목록에서 장치를 찾지 못해 장치 코드만 복원했습니다.";
    }

    private async Task StartRelayHostAsync()
    {
        if (string.IsNullOrWhiteSpace(RelayCode))
        {
            StatusMessage = "릴레이 코드를 입력해 주세요.";
            return;
        }

        try
        {
            await _remoteSessionService.StartRelayHostAsync(RelayIp, 9000, RelayCode).ConfigureAwait(false);
            StatusMessage = "릴레이 서버에서 대기 중입니다...";
            AddLog("Relay Host Ready", $"릴레이 코드 `{RelayCode}` 로 호스트 대기를 시작했습니다.", $"Relay: {RelayIp}:9000");
        }
        catch (Exception ex)
        {
            StatusMessage = $"릴레이 호스트 대기 시작에 실패했습니다. {ex.Message}";
            AddLog("Relay Host Error", $"릴레이 호스트 대기 시작 실패: {ex.Message}", $"Relay: {RelayIp}:9000 / Code: {RelayCode}");
        }
    }

    private async Task ConnectViaRelayAsync()
    {
        if (string.IsNullOrWhiteSpace(RelayCode))
        {
            StatusMessage = "연결할 릴레이 코드를 입력해 주세요.";
            return;
        }

        try
        {
            await _remoteSessionService.ConnectViaRelayAsync(RelayIp, 9000, RelayCode).ConfigureAwait(false);
            StatusMessage = "릴레이 서버를 통해 연결 시도 중...";
            AddLog("Relay Connect Requested", $"릴레이 코드 `{RelayCode}` 로 연결을 시도했습니다.", $"Relay: {RelayIp}:9000");
        }
        catch (Exception ex)
        {
            StatusMessage = $"릴레이 연결 시작에 실패했습니다. {ex.Message}";
            AddLog("Relay Connect Error", $"릴레이 연결 시작 실패: {ex.Message}", $"Relay: {RelayIp}:9000 / Code: {RelayCode}");
        }
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

    private void HandleFileSystemListReceived(string json)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                IsRemoteBrowserBusy = false;
                _pendingRemoteBrowsePath = string.Empty;
                FileSystemListResponse? response = JsonSerializer.Deserialize<FileSystemListResponse>(json);
                if (response is null)
                {
                    TransferSummary = "파일 시스템 응답을 읽을 수 없습니다.";
                    RemoteBrowserStatus = "원격 파일 목록 응답을 해석하지 못했습니다.";
                    return;
                }

                if (!response.IsSuccess)
                {
                    TransferSummary = $"파일 시스템 요청 실패: {response.ErrorMessage}";
                    StatusMessage = response.ErrorMessage ?? "원격 파일 시스템 요청이 실패했습니다.";
                    RemoteBrowserStatus = string.IsNullOrWhiteSpace(response.CurrentPath)
                        ? $"루트 목록을 불러오지 못했습니다. {response.ErrorMessage}"
                        : $"`{response.CurrentPath}` 경로를 열지 못했습니다. {response.ErrorMessage}";
                    AddLog("FS List Failed", response.ErrorMessage ?? "원격 파일 시스템 요청이 실패했습니다.", $"Remote FS / Path: {response.CurrentPath}");
                    return;
                }

                RemoteFiles.Clear();
                foreach (var entry in response.Entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name))
                {
                    RemoteFiles.Add(entry);
                }

                CurrentRemotePath = response.CurrentPath;
                TransferSummary = $"Remote directory listing updated. ({RemoteFiles.Count} items)";
                RemoteBrowserStatus = string.IsNullOrWhiteSpace(response.CurrentPath)
                    ? $"루트 목록을 불러왔습니다. 항목 {RemoteFiles.Count}개"
                    : $"현재 경로: {response.CurrentPath} / 항목 {RemoteFiles.Count}개";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainViewModel] FS List processing error: {ex.Message}");
                TransferSummary = $"Error parsing FS list: {ex.Message}";
                RemoteBrowserStatus = $"원격 파일 목록 처리 실패: {ex.Message}";
                IsRemoteBrowserBusy = false;
                _pendingRemoteBrowsePath = string.Empty;
            }
        });
    }

    private void HandleFileTransferProgressChanged(double progress)
    {
        DownloadProgress = progress;
        if (progress >= 100)
        {
            IsTransferActive = false;
            RemoteBrowserStatus = "파일 전송이 완료되었습니다.";
        }
        else if (progress > 0)
        {
            RemoteBrowserStatus = $"파일 전송 진행 중: {progress:0}%";
        }
    }

    private void OpenOrActivateRemoteBrowser()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_remoteFileBrowserWindow is { IsLoaded: true })
            {
                if (_remoteFileBrowserWindow.WindowState == WindowState.Minimized)
                {
                    _remoteFileBrowserWindow.WindowState = WindowState.Normal;
                }

                _remoteFileBrowserWindow.Activate();
                return;
            }

            _remoteFileBrowserWindow = new Views.RemoteFileBrowserWindow
            {
                DataContext = this
            };
            _remoteFileBrowserWindow.Closed += (_, _) => _remoteFileBrowserWindow = null;
            _remoteFileBrowserWindow.Show();
        });
    }

    private void RequestRemoteFileListing(
        string path,
        string transferSummary,
        string statusMessage,
        string logTitle,
        string logMessage,
        string logMeta)
    {
        if (!IsLocalDriveRedirectEnabled)
        {
            TransferSummary = "원격 파일 탐색을 열 수 없습니다. Local drive redirect가 꺼져 있습니다.";
            LastActionSummary = "Drive redirect is disabled";
            StatusMessage = "Local drive redirect 옵션을 켠 뒤 다시 시도해 주세요.";
            RemoteBrowserStatus = "로컬 드라이브 리디렉션이 비활성화되어 원격 파일 탐색 요청을 차단했습니다.";
            AddLog("Remote Explorer Blocked", "리디렉션이 비활성화되어 원격 파일 브라우저를 열지 않았습니다.", "Remote FS");
            return;
        }

        if (IsRemoteBrowserBusy)
        {
            string targetPath = string.IsNullOrWhiteSpace(_pendingRemoteBrowsePath) ? "Root" : _pendingRemoteBrowsePath;
            TransferSummary = $"이전 탐색 요청 응답을 기다리는 중입니다: {targetPath}";
            RemoteBrowserStatus = $"`{targetPath}` 경로 응답이 아직 도착하지 않아 새 요청을 잠시 보류했습니다.";
            StatusMessage = "원격 파일 시스템 응답이 도착한 뒤 다시 시도해 주세요.";
            return;
        }

        OpenOrActivateRemoteBrowser();
        IsRemoteBrowserBusy = true;
        _pendingRemoteBrowsePath = path;
        TransferSummary = transferSummary;
        StatusMessage = statusMessage;
        RemoteBrowserStatus = string.IsNullOrWhiteSpace(path)
            ? "루트 목록을 불러오는 중입니다."
            : $"경로를 불러오는 중입니다: {path}";
        _remoteSessionService.RequestFileSystemList(path);
        AddLog(logTitle, logMessage, logMeta);
    }
    private string BuildTransferSummary()
    {
        return $"Clipboard sync: {(IsClipboardSyncEnabled ? "On" : "Off")} / Ctrl copy: {(IsCtrlCopyEnabled ? "On" : "Off")} / Viewer safe display: {(IsViewerPinnedToSafeDisplay ? "On" : "Off")} / Viewer display: {SelectedViewerDisplay?.Label ?? "Auto"} / Capture rate: {SelectedCaptureRate?.Label ?? "Default"} / Compression: {SelectedCompression?.Label ?? "Default"} / Local drive: {(IsLocalDriveRedirectEnabled ? "On" : "Off")} / Capture display: {SelectedCaptureDisplay?.Label ?? "Not selected"}";
    }

    private static IReadOnlyList<CaptureDisplayOption> CreateViewerDisplayOptions(IReadOnlyList<CaptureDisplayOption> displays)
    {
        List<CaptureDisplayOption> viewerDisplays =
        [
            new CaptureDisplayOption
            {
                DisplayId = string.Empty,
                Label = "Auto (Safe Display)",
                OutputIndex = -1,
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0
            }
        ];

        viewerDisplays.AddRange(displays);
        return viewerDisplays;
    }

    private void AddLog(string title, string message, string meta)
    {
        SessionLogs.Insert(0, _remoteSessionService.CreateLog(title, message, meta));
    }

    private void HandleSessionLogAdded(SessionLogEntry logEntry)
    {
        App.Current.Dispatcher.Invoke(() => SessionLogs.Insert(0, logEntry));
    }

    private void HandleSessionSnapshotChanged(ConnectionSnapshot snapshot)
    {
        App.Current.Dispatcher.Invoke(() => ApplySnapshot(snapshot));
    }

    private void HandleRecentConnectionsChanged()
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            RecentConnections.Clear();
            foreach (RecentConnectionEntry recentConnection in _remoteSessionService.GetRecentConnections())
            {
                RecentConnections.Add(recentConnection);
            }
        });
    }

    private void HandleDevicesChanged()
    {
        App.Current.Dispatcher.Invoke(() => RefreshDevices(_remoteSessionService.GetDevices()));
    }

    private void HandleResourceSnapshotUpdated(ResourceUsageSnapshot snapshot)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            CpuUsageText = $"CPU: {snapshot.CpuUsagePercent:F1}%";
            MemoryUsageText = $"Memory: {snapshot.UsedMemoryGb:F1} / {snapshot.TotalMemoryGb:F1} GB ({snapshot.MemoryUsagePercent:F1}%)";
        });
    }

    private void RefreshDevices(IReadOnlyList<DeviceModel> devices)
    {
        Devices.Clear();
        foreach (DeviceModel device in devices.OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase))
        {
            Devices.Add(device);
        }

        SelectedDevice ??= Devices.FirstOrDefault();
        RaisePropertyChanged(nameof(SelectedDeviceFavoriteLabel));
        RaisePropertyChanged(nameof(DeviceCount));
        RaisePropertyChanged(nameof(OnlineDeviceCount));
    }

    private void RemoveDevice(DeviceModel? device)
    {
        if (device == null) return;
        
        var result = System.Windows.MessageBox.Show($"정말로 장치 '{device.Name}'를 삭제하시겠습니까?", "장치 삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            _remoteSessionService.RemoveDevice(device.DeviceId);
            Devices.Remove(device);
            if (SelectedDevice == device)
            {
                SelectedDevice = Devices.FirstOrDefault();
            }
            AddLog("Device Removed", $"장치 '{device.Name}'가 목록에서 삭제되었습니다.", "Infrastructure");
            NotifyCommandStates();
        }
    }

    private void NotifyCommandStates()
    {
        _quickConnectCommand.NotifyCanExecuteChanged();
        _requestRemoteSupportCommand.NotifyCanExecuteChanged();
        _disconnectCommand.NotifyCanExecuteChanged();
        _copyFileCommand.NotifyCanExecuteChanged();
        _uploadFileCommand.NotifyCanExecuteChanged();
        _downloadFileCommand.NotifyCanExecuteChanged();
        _browseRedirectedDrivesCommand.NotifyCanExecuteChanged();
        _browseRemoteFilesCommand.NotifyCanExecuteChanged();
        _toggleLocalDriveCommand.NotifyCanExecuteChanged();
        _toggleFavoriteCommand.NotifyCanExecuteChanged();
        _useRecentConnectionCommand.NotifyCanExecuteChanged();
        _navigateIntoFolderCommand.NotifyCanExecuteChanged();
        _navigateToBreadcrumbCommand.NotifyCanExecuteChanged();
        _downloadSelectedFileCommand.NotifyCanExecuteChanged();
        RaisePropertyChanged(nameof(SelectedDeviceFavoriteLabel));
        RaisePropertyChanged(nameof(OnlineDeviceCount));
        RaisePropertyChanged(nameof(DeviceCount));
    }

    public void Dispose()
    {
        _remoteSessionService.SessionLogAdded -= HandleSessionLogAdded;
        _remoteSessionService.DevicesChanged -= HandleDevicesChanged;
        _remoteSessionService.RecentConnectionsChanged -= HandleRecentConnectionsChanged;
        _remoteSessionService.SessionSnapshotChanged -= HandleSessionSnapshotChanged;
        _remoteSessionService.FileSystemListReceived -= HandleFileSystemListReceived;
        _remoteSessionService.FileTransferProgressChanged -= HandleFileTransferProgressChanged;
        _resourceMonitorService.SnapshotUpdated -= HandleResourceSnapshotUpdated;
    }
}

public class ProgressToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is double progress)
        {
            return progress > 0 && progress < 100 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }
        return System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public record BreadcrumbItem(string Name, string Path);
