#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace RemotePCControl.App.Infrastructure.Network;

public sealed class ClipboardSyncService
{
    // WPF 환경에서는 System.Windows.Clipboard 접근 시 STA 스레드 주기(UI)가 필수
    public string GetText()
    {
        string text = string.Empty;
        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    text = System.Windows.Clipboard.GetText();
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
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                System.Windows.Clipboard.SetText(text);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardSync] SetText Error: {ex.Message}");
        }
    }

    public byte[]? GetImageAsPng()
    {
        byte[]? result = null;
        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (System.Windows.Clipboard.ContainsImage())
                {
                    BitmapSource image = System.Windows.Clipboard.GetImage();
                    using var ms = new MemoryStream();
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(ms);
                    result = ms.ToArray();
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardSync] GetImage Error: {ex.Message}");
        }
        return result;
    }

    public void SetImageFromPng(byte[] pngData)
    {
        if (pngData == null || pngData.Length == 0) return;

        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                using var ms = new MemoryStream(pngData);
                var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                System.Windows.Clipboard.SetImage(decoder.Frames[0]);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardSync] SetImage Error: {ex.Message}");
        }
    }

    public string[]? GetFileDropList()
    {
        string[]? result = null;
        try
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (System.Windows.Clipboard.ContainsFileDropList())
                {
                    var files = System.Windows.Clipboard.GetFileDropList();
                    if (files != null && files.Count > 0)
                    {
                        result = new string[files.Count];
                        files.CopyTo(result, 0);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClipboardSync] GetFileDropList Error: {ex.Message}");
        }
        return result;
    }
}
