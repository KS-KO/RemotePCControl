#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RemotePCControl.App.Infrastructure.Input;

public sealed class InputInjectionService : IDisposable
{
    private bool _isDisposed;

    // --- Win32 구조체 및 상수 정의 ---
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;
    private const int INPUT_HARDWARE = 2;

    [Flags]
    public enum MouseEventFlags : uint
    {
        Move = 0x0001,
        LeftDown = 0x0002,
        LeftUp = 0x0004,
        RightDown = 0x0008,
        RightUp = 0x0010,
        MiddleDown = 0x0020,
        MiddleUp = 0x0040,
        XDown = 0x0080,
        XUp = 0x0100,
        Wheel = 0x0800,
        VariableDPI = 0x4000,
        VirtualDesk = 0x4000,
        Absolute = 0x8000
    }

    [Flags]
    public enum KeyEventFlags : uint
    {
        KeyDown = 0x0000,
        ExtendedKey = 0x0001,
        KeyUp = 0x0002,
        Unicode = 0x0004,
        ScanCode = 0x0008
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;

        public static int Size => Marshal.SizeOf(typeof(INPUT));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    // --- 주입 API ---

    public void InjectMouse(int x, int y, MouseEventFlags flags, int mouseData = 0)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(InputInjectionService));

        try
        {
            // Absolute 위치 전송인 경우 화면 해상도 기준으로 정규화 (0~65535)
            int normalizedX = x;
            int normalizedY = y;

            if (flags.HasFlag(MouseEventFlags.Absolute))
            {
                int screenWidth = GetSystemMetrics(SM_CXSCREEN);
                int screenHeight = GetSystemMetrics(SM_CYSCREEN);

                if (screenWidth > 0 && screenHeight > 0)
                {
                    normalizedX = (int)Math.Round(x * 65535.0 / (screenWidth - 1));
                    normalizedY = (int)Math.Round(y * 65535.0 / (screenHeight - 1));
                }
            }

            // GC 부담을 없애기 위해 배열(new INPUT[]) 대신 로컬 단일 변수 ref 전달을 사용합니다.
            INPUT input = new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = normalizedX,
                        dy = normalizedY,
                        mouseData = (uint)mouseData,
                        dwFlags = (uint)flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            uint result = SendInput(1, ref input, INPUT.Size);
            if (result == 0)
            {
                int error = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[InputInjection] InjectMouse failed with error code: {error}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InputInjection] Mouse injection exception: {ex.Message}");
        }
    }

    public void InjectKeyboard(ushort virtualKey, KeyEventFlags flags)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(InputInjectionService));

        try
        {
            INPUT input = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = virtualKey,
                        wScan = 0, // 스캔 코드 대신 Virtual Key 사용 (기본)
                        dwFlags = (uint)flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            uint result = SendInput(1, ref input, INPUT.Size);
            if (result == 0)
            {
                int error = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[InputInjection] InjectKeyboard failed with error code: {error}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InputInjection] Keyboard injection exception: {ex.Message}");
        }
    }

    public bool LockSession()
    {
        try
        {
            return LockWorkStation();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InputInjection] LockWorkStation failed: {ex.Message}");
            return false;
        }
    }

    public bool SetInputBlock(bool blocked)
    {
        try
        {
            // BlockInput은 관리자 권한이 필요할 수 있습니다.
            return BlockInput(blocked);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InputInjection] BlockInput failed: {ex.Message}");
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool BlockInput(bool fBlockIt);

    [DllImport("user32.dll")]
    private static extern int EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll")]
    private static extern int ChangeDisplaySettings(ref DEVMODE devMode, int flags);

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CDS_UPDATEREGISTRY = 0x01;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public short dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;

        public static DEVMODE Create()
        {
            return new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
        }
    }

    private const int DM_PELSWIDTH = 0x00080000;
    private const int DM_PELSHEIGHT = 0x00100000;

    public bool ChangeDisplayResolution(int width, int height)
    {
        try
        {
            DEVMODE dm = DEVMODE.Create();
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) != 0)
            {
                dm.dmPelsWidth = width;
                dm.dmPelsHeight = height;
                dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;

                int result = ChangeDisplaySettings(ref dm, 0); // 0: 테스트/일시 적용
                return result == DISP_CHANGE_SUCCESSFUL;
            }
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[InputInjection] ChangeDisplayResolution failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        // P/Invoke 관련 핸들 등 외부 리소스가 추가된다면 이곳에서 해제합니다.
        if (_isDisposed) return;
        _isDisposed = true;

        // 현재 상태에서는 별도 해제할 Unmanaged 리소스가 없으나, IDisposable 강제 규칙 준수
    }
}
