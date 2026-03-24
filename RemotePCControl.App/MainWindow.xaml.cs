using System.Windows;
using RemotePCControl.App.Services;
using RemotePCControl.App.ViewModels;

namespace RemotePCControl.App;

public partial class MainWindow : Window
{
    private readonly RealRemoteSessionService _sessionService;

    public MainWindow()
    {
        InitializeComponent();
        
        // 실제 원격 서버-클라이언트 엔진 주입 (PRD 통합 규격)
        _sessionService = new RealRemoteSessionService();
        DataContext = new MainViewModel(_sessionService);
        
        // 메모리 누수 방지: 윈도우 종료 시 소켓, 캡처 API, 리소스 동시 해제 (IDisposable 강제 룰)
        Closed += (s, e) => _sessionService.Dispose();
    }
}
