using System;
using System.Threading;
using System.Windows;

namespace RemotePCControl.App;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string appMutexName = "Global\\RemotePCControl_App_SingleInstance";
        
        // Mutex 획득 시도 (Global 접두사로 다른 세션의 동일 사용자 포함 커버 가능)
        _mutex = new Mutex(true, appMutexName, out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show("프로그램이 이미 실행 중입니다.\n중복 실행을 방지하기 위해 앱을 종료합니다.", "Remote PC Control", MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex != null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
        }
        
        base.OnExit(e);
    }
}
