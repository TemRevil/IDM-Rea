using System.Runtime.InteropServices;
namespace IDMRea.Engine.Windows;

/// <summary>The Win32 calls the engine needs: a message-only window, the notification-area icon, its menu, the Recycle Bin and working-set trim.</summary>
internal static class Native
{
    public const int WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_COMMAND = 0x0111, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B, WM_APP = 0x8000;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    public const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800, TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;
    public static readonly IntPtr HWND_MESSAGE = new(-3);
    public const int IDI_APPLICATION = 32512;

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo; public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle;
    }

    public const uint FO_DELETE = 3;
    public const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool UnregisterClassW(string className, IntPtr hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] public static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern uint ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
}
