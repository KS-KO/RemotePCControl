#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace RemotePCControl.App.Infrastructure.Input;

public sealed class CursorCaptureService
{
    private IntPtr _lastCursorHandle = IntPtr.Zero;
    private static readonly Dictionary<IntPtr, string> _standardCursorMap = new();

    static CursorCaptureService()
    {
        // IDC constants
        int[] idcList = { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651 };
        string[] names = { "Arrow", "IBeam", "Wait", "Cross", "UpArrow", "SizeNWSE", "SizeNESW", "SizeWE", "SizeNS", "SizeAll", "No", "Hand", "AppStarting", "Help" };

        for (int i = 0; i < idcList.Length; i++)
        {
            IntPtr handle = LoadCursor(IntPtr.Zero, idcList[i]);
            if (handle != IntPtr.Zero)
            {
                _standardCursorMap[handle] = names[i];
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(out CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    public CursorInfoData? CaptureCurrentCursor()
    {
        CURSORINFO ci = new CURSORINFO();
        ci.cbSize = Marshal.SizeOf(ci);
        if (!GetCursorInfo(out ci)) return null;

        if (ci.hCursor == _lastCursorHandle) return null; // No change
        _lastCursorHandle = ci.hCursor;

        if (ci.flags == 0) // Cursor hidden
        {
            return new CursorInfoData { IsVisible = false, CursorName = "None" };
        }

        if (_standardCursorMap.TryGetValue(ci.hCursor, out string? name))
        {
            return new CursorInfoData
            {
                IsVisible = true,
                CursorName = name
            };
        }

        // Fallback to Arrow if unknown but visible
        return new CursorInfoData
        {
            IsVisible = true,
            CursorName = "Arrow"
        };
    }
}

public sealed class CursorInfoData
{
    public bool IsVisible { get; set; }
    public string CursorName { get; set; } = "Arrow";
}
