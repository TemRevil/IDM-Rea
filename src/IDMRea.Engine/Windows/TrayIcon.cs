using System.Runtime.InteropServices;
namespace IDMRea.Engine.Windows;

/// <summary>
/// The notification-area icon, on its own thread with a hidden message-only window.
/// Commands are raised on that thread; handlers should hand work off quickly.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = Native.WM_APP + 1, TooltipMessage = Native.WM_APP + 2, IconMessage = Native.WM_APP + 3;
    private readonly Action<TrayChoice> onCommand;
    private readonly Func<TrayState> state;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private readonly Native.WndProc proc; // kept alive for as long as the window exists
    private readonly string className = "IDMRea.Tray." + Environment.ProcessId;
    private IntPtr window, icon;
    private bool ownsIcon;
    private uint taskbarCreated;
    private volatile string tooltip = "IDM-Rea - idle";
    private volatile string? iconFile;

    public TrayIcon(Action<TrayChoice> onCommand, Func<TrayState> state)
    {
        this.onCommand = onCommand; this.state = state;
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

    /// <summary>Shows the icon from an .ico file (the one drawn in the accent colour). Safe from any thread.</summary>
    public void SetIcon(string path)
    {
        iconFile = path;
        if (window != IntPtr.Zero) Native.PostMessageW(window, IconMessage, IntPtr.Zero, IntPtr.Zero);
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
        FollowDarkMode();
        icon = LoadOwnIcon();
        Add();
        ready.Set();
        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0) { Native.TranslateMessage(ref msg); Native.DispatchMessageW(ref msg); }
        Native.UnregisterClassW(className, instance);
    }

    private IntPtr LoadFileIcon() => iconFile is { } path
        ? Native.LoadImageW(IntPtr.Zero, path, Native.IMAGE_ICON, Native.GetSystemMetrics(Native.SM_CXSMICON), Native.GetSystemMetrics(Native.SM_CYSMICON), Native.LR_LOADFROMFILE)
        : IntPtr.Zero;

    private IntPtr LoadOwnIcon()
    {
        if (LoadFileIcon() is var drawn && drawn != IntPtr.Zero) { ownsIcon = true; return drawn; }
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
                    case Native.WM_LBUTTONDBLCLK: Raise(new(TrayCommand.Open)); break;
                    case Native.WM_RBUTTONUP or Native.WM_CONTEXTMENU: ShowMenu(); break;
                }
                return IntPtr.Zero;
            case TooltipMessage:
                var data = Data(Native.NIF_TIP | Native.NIF_SHOWTIP); Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref data);
                return IntPtr.Zero;
            case IconMessage:
                var next = LoadFileIcon();
                if (next == IntPtr.Zero) return IntPtr.Zero;
                var (old, ownedOld) = (icon, ownsIcon);
                icon = next; ownsIcon = true;
                var changed = Data(Native.NIF_ICON); Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref changed);
                if (ownedOld) Native.DestroyIcon(old);
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
        TrayState now;
        try { now = state(); } catch (Exception) { now = new(0, 0, true, true, 0); }
        var menu = Build(TrayMenu.Build(now));
        Native.GetCursorPos(out var at);
        // Without this the menu does not close when clicking elsewhere.
        Native.SetForegroundWindow(window);
        var chosen = Native.TrackPopupMenu(menu, Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NONOTIFY, at.X, at.Y, 0, window, IntPtr.Zero);
        Native.DestroyMenu(menu); // also destroys the submenus
        if (TrayMenu.Decode(chosen) is { } choice) Raise(choice);
    }

    private static IntPtr Build(IReadOnlyList<TrayMenuItem> items)
    {
        var menu = Native.CreatePopupMenu();
        foreach (var item in items)
        {
            if (item.Separator) { Native.AppendMenuW(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null); continue; }
            var flags = (item.Enabled ? 0 : Native.MF_GRAYED) | (item.Checked ? Native.MF_CHECKED : 0);
            if (item.Items is { } children) Native.AppendMenuW(menu, flags | Native.MF_POPUP, (UIntPtr)(ulong)Build(children), item.Text);
            else Native.AppendMenuW(menu, flags | Native.MF_STRING, (UIntPtr)(uint)item.Id, item.Text);
            if (item.Default) Native.SetMenuDefaultItem(menu, (uint)item.Id, 0);
        }
        return menu;
    }

    /// <summary>
    /// Menus follow Windows' dark mode only when the process opts in. The switch is uxtheme's SetPreferredAppMode
    /// (ordinal 135, Windows 10 1903 and later), which has no documented name; without it the menu stays light.
    /// </summary>
    private static void FollowDarkMode()
    {
        var uxtheme = Native.LoadLibraryW("uxtheme.dll");
        if (uxtheme == IntPtr.Zero) return;
        var setMode = Native.GetProcAddress(uxtheme, 135);
        var flush = Native.GetProcAddress(uxtheme, 136); // FlushMenuThemes
        if (setMode == IntPtr.Zero) return;
        Marshal.GetDelegateForFunctionPointer<SetPreferredAppMode>(setMode)(1); // 1 = follow Windows
        if (flush != IntPtr.Zero) Marshal.GetDelegateForFunctionPointer<FlushMenuThemes>(flush)();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SetPreferredAppMode(int mode);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void FlushMenuThemes();

    private void Raise(TrayChoice choice) => ThreadPool.QueueUserWorkItem(_ => onCommand(choice));

    public void Dispose()
    {
        if (window != IntPtr.Zero) Native.PostMessageW(window, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(5));
        ready.Dispose();
    }
}
