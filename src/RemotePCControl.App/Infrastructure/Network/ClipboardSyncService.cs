#nullable enable
using System;
using System.Diagnostics;
using System.Windows;

namespace RemotePCControl.App.Infrastructure.Network;

public sealed class ClipboardSyncService
{
    // WPF 환경에서는 System.Windows.Clipboard 접근 시 STA 스레드 주기(UI)가 필수
    public string GetText()
    {
        string text = string.Empty;
        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (Clipboard.ContainsText())
                {
                    text = Clipboard.GetText();
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardSync] GetText Error: {ex.Message}");
        }
        return text;
    }

    public void SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                Clipboard.SetText(text);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardSync] SetText Error: {ex.Message}");
        }
    }
}
