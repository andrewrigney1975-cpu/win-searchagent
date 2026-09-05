using System.Runtime.InteropServices;

namespace Delve.Services;

/// System-wide Shift+Win+D hotkey via the classic RegisterHotKey Win32 API. WinUI 3 has no
/// managed global-hotkey surface, and RegisterHotKey needs an HWND with a message loop pumping
/// its thread's message queue - so this creates a hidden, message-only native window on its
/// own dedicated thread (its own GetMessage/DispatchMessage loop) rather than trying to piggy-
/// back on WinUI's own message pump, whose internals aren't a stable place to hook a raw
/// WndProc. WM_HOTKEY firings are marshalled back to the caller's thread via the supplied
/// callback's own synchronization (the caller is expected to re-dispatch to the UI thread).
///
/// Ctrl+Win+D was the original choice but is claimed by Windows itself (create new virtual
/// desktop) at a level RegisterHotKey can't override - confirmed by hands-on testing, the
/// popup never appeared because Explorer's own shell hotkey consumed it first. Shift+Win+D
/// isn't a reserved Windows shortcut.
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint WM_APP_ENABLE = 0x8001;
    private const uint WM_APP_DISABLE = 0x8002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint VK_D = 0x44;
    private const int HotkeyId = 1;

    private Thread? _thread;
    private IntPtr _hwnd;
    private uint _threadId;
    private WndProcDelegate? _wndProcDelegate; // kept alive: GC must not collect a delegate a native pointer references
    private readonly ManualResetEventSlim _ready = new(false);
    private Action? _onPressed;

    public void Start(Action onPressed)
    {
        _onPressed = onPressed;
        _thread = new Thread(RunMessageLoop) { IsBackground = true, Name = "Delve.GlobalHotkey" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    private void RunMessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _wndProcDelegate = WndProc;

        var className = "DelveHotkeyWindow_" + Guid.NewGuid().ToString("N");
        var wndClass = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            lpszClassName = className,
        };
        RegisterClass(ref wndClass);

        _hwnd = CreateWindowEx(0, className, string.Empty, 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        // Starts disabled: the caller (DocketAvailabilityService via App) calls SetEnabled(true)
        // once Docket's index is confirmed available.
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _onPressed?.Invoke();
            return IntPtr.Zero;
        }

        // RegisterHotKey/UnregisterHotKey must run on the thread that owns _hwnd (this WndProc's
        // thread), so SetEnabled posts here rather than calling the API directly from whatever
        // thread DocketAvailabilityService's poll timer happens to fire on.
        if (msg == WM_APP_ENABLE)
        {
            RegisterHotKey(hwnd, HotkeyId, MOD_SHIFT | MOD_WIN, VK_D);
            return IntPtr.Zero;
        }

        if (msg == WM_APP_DISABLE)
        {
            UnregisterHotKey(hwnd, HotkeyId);
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// Registers or unregisters the Shift+Win+D hotkey. Safe to call repeatedly with the same
    /// value (RegisterHotKey on an already-registered id/window just fails harmlessly, and
    /// UnregisterHotKey on a not-currently-registered one does too).
    public void SetEnabled(bool enabled)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        PostMessage(_hwnd, enabled ? WM_APP_ENABLE : WM_APP_DISABLE, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            DestroyWindow(_hwnd);
        }

        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        _ready.Dispose();
    }

    private const uint WM_QUIT = 0x0012;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
