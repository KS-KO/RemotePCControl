using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace RemotePCControl.App;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private static readonly object LogSync = new();
    private static readonly string DiagnosticsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemotePCControl",
        "Diagnostics");
    private static readonly string StartupLogPath = Path.Combine(DiagnosticsDirectory, "startup-diagnostics.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        const string appMutexName = "Global\\RemotePCControl_App_SingleInstance";

        Directory.CreateDirectory(DiagnosticsDirectory);
        RegisterGlobalExceptionHandlers();
        WriteStartupLog("Startup", "Application startup entered.");

        // Mutex 획득 시도 (Global 접두사로 다른 세션의 동일 사용자 포함 커버 가능)
        _mutex = new Mutex(true, appMutexName, out bool createdNew);
        _ownsMutex = createdNew;
        WriteStartupLog("Mutex", $"Mutex acquired state: createdNew={createdNew}");

        if (!createdNew)
        {
            WriteStartupLog("SingleInstance", "Existing instance detected. Showing duplicate-instance notice and shutting down.");
            System.Windows.MessageBox.Show("프로그램이 이미 실행 중입니다.\n중복 실행을 방지하기 위해 앱을 종료합니다.", "Remote PC Control", MessageBoxButton.OK, MessageBoxImage.Information);
            System.Windows.Application.Current.Shutdown();
            return;
        }

        base.OnStartup(e);
        WriteStartupLog("Startup", "Base OnStartup completed.");

        try
        {
            WriteStartupLog("MainWindow", "MainWindow construction starting.");
            MainWindow mainWindow = new();
            MainWindow = mainWindow;
            WriteStartupLog("MainWindow", "MainWindow constructed successfully.");
            mainWindow.Show();
            WriteStartupLog("MainWindow", "MainWindow shown successfully.");
        }
        catch (Exception ex)
        {
            WriteStartupLog("StartupFailure", "MainWindow startup failed.", ex);
            System.Windows.MessageBox.Show(
                $"앱 시작 중 오류가 발생했습니다.\n진단 로그: {StartupLogPath}",
                "Remote PC Control",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        WriteStartupLog("Exit", $"Application exit entered. ExitCode={e.ApplicationExitCode}");
        if (_mutex != null)
        {
            if (_ownsMutex)
            {
                _mutex.ReleaseMutex();
                _ownsMutex = false;
                WriteStartupLog("Mutex", "Mutex released by owning process.");
            }

            _mutex.Dispose();
            _mutex = null;
            WriteStartupLog("Mutex", "Mutex disposed.");
        }

        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException -= HandleDispatcherUnhandledException;
        DispatcherUnhandledException += HandleDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException -= HandleCurrentDomainUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += HandleCurrentDomainUnhandledException;

        TaskScheduler.UnobservedTaskException -= HandleUnobservedTaskException;
        TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
    }

    private void HandleDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteStartupLog("DispatcherUnhandledException", "Unhandled dispatcher exception captured.", e.Exception);
    }

    private void HandleCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        Exception? exception = e.ExceptionObject as Exception;
        WriteStartupLog(
            "AppDomainUnhandledException",
            $"Unhandled exception captured. IsTerminating={e.IsTerminating}",
            exception);
    }

    private void HandleUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteStartupLog("UnobservedTaskException", "Unobserved task exception captured.", e.Exception);
    }

    private static void WriteStartupLog(string stage, string message, Exception? exception = null)
    {
        lock (LogSync)
        {
            Directory.CreateDirectory(DiagnosticsDirectory);
            using StreamWriter writer = new(StartupLogPath, append: true);
            writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{stage}] {message}");
            if (exception is not null)
            {
                writer.WriteLine(exception);
            }
        }
    }
}
