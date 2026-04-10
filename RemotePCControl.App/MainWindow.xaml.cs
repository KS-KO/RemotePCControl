using System.Windows;
using RemotePCControl.App.Services;
using RemotePCControl.App.ViewModels;

namespace RemotePCControl.App;

public partial class MainWindow : Window
{
    private readonly RealRemoteSessionService _sessionService;
    private readonly ResourceMonitorService _resourceMonitorService;
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        
        // 실제 원격 서버-클라이언트 엔진 주입 (PRD 통합 규격)
        _sessionService = new RealRemoteSessionService();
        _resourceMonitorService = new ResourceMonitorService();
        _viewModel = new MainViewModel(_sessionService, _resourceMonitorService);
        DataContext = _viewModel;
        _resourceMonitorService.Start();
        
        // 메모리 누수 방지: 윈도우 종료 시 소켓, 캡처 API, 리소스 동시 해제 (IDisposable 강제 룰)
        Closed += (s, e) =>
        {
            _viewModel.Dispose();
            _resourceMonitorService.Dispose();
            _sessionService.Dispose();
        };
    }
}
