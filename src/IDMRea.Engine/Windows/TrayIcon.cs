using System.Runtime.InteropServices;
namespace IDMRea.Engine.Windows;

public enum TrayCommand { Open = 1, AddFromClipboard, PauseAll, ResumeAll, Quit }

/// <summary>
/// The notification-area icon, on its own thread with a hidden message-only window.
/// Commands are raised on that thread; handlers should hand work off quickly.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = Native.WM_APP + 1, TooltipMessage = Native.WM_APP + 2;
    private readonly Action<TrayCommand> onCommand;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private readonly Native.WndProc proc; // kept alive for as long as the window exists
    private readonly string className = "IDMRea.Tray." + Environment.ProcessId;
    private IntPtr window, icon;
    private bool ownsIcon;
    private uint taskbarCreated;
    private volatile string tooltip = "IDM-Rea - idle";

    public TrayIcon(Action<TrayCommand> onCommand)
    {
        this.onCommand = onCommand;
        proc = WindowProc;
        thread = new Thread(Run) { IsBackground = true, Name = "Tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(10));
    }

    /// <summary>Updates the hover text. Safe from any thread.</summary>
    public void SetTooltip(string text)
    {
        if (text == tooltip) return;
        tooltip = text;
        if (window != IntPtr.Zero) Native.PostMessageW(window, TooltipMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        var instance = Native.GetModuleHandleW(null);
        var wc = new Native.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = instance, lpszClassName = className,
        };
        Native.RegisterClassExW(ref wc);
        window = Native.CreateWindowExW(0, className, "IDM-Rea", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
        taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
        icon = LoadOwnIcon();
        Add();
        ready.Set();
        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0) { Native.TranslateMessage(ref msg); Native.DispatchMessageW(ref msg); }
        Native.UnregisterClassW(className, instance);
    }

    private IntPtr LoadOwnIcon()
    {
        var small = new IntPtr[1];
        if (Environment.ProcessPath is { } exe && Native.ExtractIconExW(exe, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero) { ownsIcon = true; return small[0]; }
        return Native.LoadIconW(IntPtr.Zero, Native.IDI_APPLICATION);
    }

    private Native.NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATAW>(), hWnd = window, uID = 1, uFlags = flags,
        uCallbackMessage = CallbackMessage, hIcon = icon, szTip = tooltip, szInfo = "", szInfoTitle = "",
    };

    private void Add() { var data = Data(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP); Native.Shell_NotifyIconW(Native.NIM_ADD, ref data); }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case CallbackMessage:
                switch ((int)lParam & 0xFFFF)
                {
                    case Native.WM_LBUTTONDBLCLK: Raise(TrayCommand.Open); break;
                    case Native.WM_RBUTTONUP or Native.WM_CONTEXTMENU: ShowMenu(); break;
                }
                return IntPtr.Zero;
            case TooltipMessage:
                var data = Data(Native.NIF_TIP | Native.NIF_SHOWTIP); Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref data);
                return IntPtr.Zero;
            case Native.WM_CLOSE:
                var gone = Data(0); Native.Shell_NotifyIconW(Native.NIM_DELETE, ref gone);
                Native.DestroyWindow(hWnd); return IntPtr.Zero;
            case Native.WM_DESTROY:
                if (ownsIcon) Native.DestroyIcon(icon);
                Native.PostQuitMessage(0); return IntPtr.Zero;
        }
        // Explorer restarted: the icon has to be added again.
        if (msg == taskbarCreated && taskbarCreated != 0) { Add(); return IntPtr.Zero; }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = Native.CreatePopupMenu();
        Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)(uint)TrayCommand.Open, "Open IDM-Rea");
        Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)(uint)TrayCommand.AddFromClipboard, "Add from clipboard");
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
        Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)(uint)TrayCommand.PauseAll, "Pause all");
        Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)(uint)TrayCommand.ResumeAll, "Resume all");
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
        Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)(uint)TrayCommand.Quit, "Quit");
        Native.GetCursorPos(out var at);
        // Without this the menu does not close when clicking elsewhere.
        Native.SetForegroundWindow(window);
        var chosen = Native.TrackPopupMenu(menu, Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NONOTIFY, at.X, at.Y, 0, window, IntPtr.Zero);
        Native.DestroyMenu(menu);
        if (chosen > 0) Raise((TrayCommand)chosen);
    }

    private void Raise(TrayCommand command) => ThreadPool.QueueUserWorkItem(_ => onCommand(command));

    public void Dispose()
    {
        if (window != IntPtr.Zero) Native.PostMessageW(window, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(5));
        ready.Dispose();
    }
}
