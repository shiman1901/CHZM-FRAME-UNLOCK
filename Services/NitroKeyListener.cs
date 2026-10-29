using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Timer = System.Threading.Timer;

namespace CHZMFRAMEUNLOCK1.Services;

/// <summary>
/// NitroSense / Nitro key listener: VK = 0xFF.
/// Uses both WH_KEYBOARD_LL and GetAsyncKeyState polling every 40 ms,
/// and raises LongPressed once after the key has been held for 600 ms.
/// </summary>
public sealed class NitroKeyListener : IDisposable
{
    public const int NitroVirtualKey = 0xFF;
    public const int DefaultHoldMilliseconds = 600;

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly LowLevelKeyboardProc _hookProc;
    private readonly object _gate = new();
    private readonly Timer _pollTimer;

    private IntPtr _hook;
    private bool _down;
    private bool _fired;
    private DateTime _downAtUtc;
    private bool _disposed;

    public NitroKeyListener()
    {
        _hookProc = HookCallback;

        try
        {
            _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
        }
        catch
        {
            _hook = IntPtr.Zero;
        }

        _pollTimer = new Timer(_ => Poll(), null, 40, 40);
    }

    public int HoldMilliseconds { get; set; } = DefaultHoldMilliseconds;

    /// <summary>Raised once per long press (on the UI/hook or timer thread).</summary>
    public event Action? LongPressed;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (data.vkCode == NitroVirtualKey)
                {
                    int message = wParam.ToInt32();
                    if (message is WmKeyDown or WmSysKeyDown)
                        OnDown();
                    else if (message is WmKeyUp or WmSysKeyUp)
                        OnUp();
                }
            }
        }
        catch
        {
            // never break the global hook chain
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Poll()
    {
        if (_disposed) return;

        try
        {
            bool down = (GetAsyncKeyState(NitroVirtualKey) & 0x8000) != 0;

            if (down) OnDown();
            else OnUp();

            CheckLongPress();
        }
        catch
        {
            // ignore
        }
    }

    private void OnDown()
    {
        lock (_gate)
        {
            if (_down) return;
            _down = true;
            _fired = false;
            _downAtUtc = DateTime.UtcNow;
        }

        CheckLongPress();
    }

    private void OnUp()
    {
        lock (_gate)
        {
            _down = false;
            _fired = false;
        }
    }

    private void CheckLongPress()
    {
        bool fire = false;

        lock (_gate)
        {
            if (_down && !_fired &&
                (DateTime.UtcNow - _downAtUtc).TotalMilliseconds >= HoldMilliseconds)
            {
                _fired = true;
                fire = true;
            }
        }

        if (!fire) return;

        try { LongPressed?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _pollTimer.Dispose(); } catch { }

        try
        {
            if (_hook != IntPtr.Zero)
                UnhookWindowsHookEx(_hook);
        }
        catch { }

        _hook = IntPtr.Zero;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback,
        IntPtr hInstance, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hHook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hHook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
