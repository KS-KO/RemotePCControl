#nullable enable
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace RemotePCControl.App.Services;

/// <summary>
/// 시스템 트레이 아이콘 관리 및 윈도우 최소화 연동 서비스
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Action _restoreAction;
    private bool _isDisposed;

    public TrayIconService(Action restoreAction)
    {
        _restoreAction = restoreAction ?? throw new ArgumentNullException(nameof(restoreAction));

        _notifyIcon = new NotifyIcon();
        
        // 아이콘 설정 (새로운 해바라기 아이콘 로드)
        try
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "AppIcon.png");
            if (File.Exists(iconPath))
            {
                using var bitmap = new Bitmap(iconPath);
                _notifyIcon.Icon = Icon.FromHandle(bitmap.GetHicon());
            }
            else
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load AppIcon.png: {ex.Message}");
            _notifyIcon.Icon = SystemIcons.Application;
        }

        _notifyIcon.Text = "Remote PC Control";
        _notifyIcon.Visible = true;

        // 더블 클릭 시 창 복원
        _notifyIcon.DoubleClick += (s, e) => _restoreAction();

        // 컨텍스트 메뉴 설정
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("열기", null, (s, e) => _restoreAction());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("종료", null, (s, e) => 
        {
            // UI 스레드에서 안전하게 종료 호출
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        });

        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    /// <summary>
    /// 트레이 아이콘에 알림 표시
    /// </summary>
    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (_isDisposed) return;
        
        // WinForms NotifyIcon은 UI 스레드에서 동작해야 함
        if (_notifyIcon.ContextMenuStrip?.InvokeRequired == true)
        {
            _notifyIcon.ContextMenuStrip.Invoke(new Action(() => _notifyIcon.ShowBalloonTip(3000, title, message, icon)));
        }
        else
        {
            _notifyIcon.ShowBalloonTip(3000, title, message, icon);
        }
    }

    /// <summary>
    /// 트레이 아이콘 텍스트(툴팁) 업데이트
    /// </summary>
    public void UpdateStatus(string status)
    {
        if (_isDisposed) return;
        
        string newText = $"Remote PC Control - {status}";
        // NotifyIcon.Text는 최대 63자 제한이 있음
        if (newText.Length >= 64)
        {
            newText = newText.Substring(0, 60) + "...";
        }
        _notifyIcon.Text = newText;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        
        _notifyIcon.Visible = false;
        
        // 메뉴 리소스 해제
        if (_notifyIcon.ContextMenuStrip != null)
        {
            _notifyIcon.ContextMenuStrip.Dispose();
        }
        
        _notifyIcon.Dispose();
        _isDisposed = true;
    }
}
