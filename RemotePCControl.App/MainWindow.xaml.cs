using System.Windows;
using RemotePCControl.App.Services;
using RemotePCControl.App.ViewModels;
using RemotePCControl.App.Views;

namespace RemotePCControl.App;

public partial class MainWindow : Window
{
    private readonly RealRemoteSessionService _sessionService;
    private readonly ResourceMonitorService _resourceMonitorService;
    private readonly TrayIconService _trayIconService;
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        
        // 실제 원격 서버-클라이언트 엔진 주입 (PRD 통합 규격)
        _sessionService = new RealRemoteSessionService();
        _resourceMonitorService = new ResourceMonitorService();
        _trayIconService = new TrayIconService(RestoreWindow);
        _viewModel = new MainViewModel(_sessionService, _resourceMonitorService);
        DataContext = _viewModel;
        _resourceMonitorService.Start();
        
        // 메모리 누수 방지: 윈도우 종료 시 소켓, 캡처 API, 리소스 동시 해제 (IDisposable 강제 룰)
        Closed += (s, e) =>
        {
            _viewModel.Dispose();
            _resourceMonitorService.Dispose();
            _sessionService.Dispose();
            _trayIconService.Dispose();
        };

        // 종료 시 확인 창 표시 (Modern UI 스타일)
        Closing += (s, e) =>
        {
            var dialog = new ExitConfirmDialog { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                e.Cancel = true;
            }
        };

        // 트레이 아이콘 상태 연동 (ViewModel의 상태 메시지 관찰)
        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(_viewModel.ActiveSessionStatus))
            {
                _trayIconService.UpdateStatus(_viewModel.ActiveSessionStatus);
            }
        };
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            this.Hide();
            _trayIconService.ShowNotification("알림", "서비스가 백그라운드에서 실행 중입니다.");
        }
    }

    private void RestoreWindow()
    {
        this.Show();
        this.WindowState = WindowState.Normal;
        this.Activate();
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && int.TryParse(element.Tag?.ToString(), out int index))
        {
            _viewModel.CurrentMenuIndex = index;
        }
    }
}
