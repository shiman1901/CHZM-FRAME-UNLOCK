using System;
using System.Threading;
using System.Runtime.InteropServices;
using Timer = System.Threading.Timer;

namespace CHZMFRAMEUNLOCK1.Services;

/// <summary>
/// Лёгкий фоновый перехватчик клавиши меню (по умолчанию F8).
/// Когда игра на переднем плане, отправляет в неё VK_INSERT (0x2D, scan 0x52),
/// поэтому оверлей RTX Encore открывается даже на 65% клавиатуре без Insert.
/// </summary>
public sealed class MenuHotkey : IDisposable
{
    private const int VkInsert = 0x2D;
    private const ushort ScanInsert = 0x52;
    private const uint KeyeventfExtendedKey = 0x0001;
    private const uint KeyeventfKeyUp = 0x0002;
    private const uint InputKeyboard = 1;

    private readonly Timer _timer;
    private readonly object _gate = new();

    private int _virtualKey = 0x77; // F8
    private bool _wasDown;
    private DateTime _lastSentUtc = DateTime.MinValue;
    private bool _disposed;

    public MenuHotkey(int virtualKey = 0x77)
    {
        _virtualKey = virtualKey;
        _timer = new Timer(_ => Poll(), null, 45, 45);
    }

    /// <summary>Дополнительное условие: движок установлен и включён.</summary>
    public Func<bool>? CanFire { get; set; }

    public int VirtualKey
    {
        get { lock (_gate) return _virtualKey; }
    }

    public void SetKey(int virtualKey)
    {
        lock (_gate)
        {
            _virtualKey = virtualKey;
            _wasDown = false;
        }
    }

    private void Poll()
    {
        if (_disposed) return;

        try
        {
            int vk;
            lock (_gate) vk = _virtualKey;

            if (vk <= 0) return;

            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;

            if (!down)
            {
                _wasDown = false;
                return;
            }

            if (_wasDown) return;
            _wasDown = true;

            if ((DateTime.UtcNow - _lastSentUtc).TotalMilliseconds < 300) return;
            if (CanFire is not null && !CanFire()) return;
            if (!IsGameForeground()) return;

            if (SendInsert())
                _lastSentUtc = DateTime.UtcNow;
        }
        catch
        {
            // перехватчик не должен ронять приложение
        }
    }

    private static bool IsGameForeground()
    {
        try
        {
            return TensorAutoService.GetActiveGame() is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Отправить в активное окно нажатие/отпускание Insert.
    /// wVk = 0x2D, wScan = 0x52, KEYEVENTF_EXTENDEDKEY — как просили.
    /// </summary>
    public static bool SendInsert()
    {
        try
        {
            var down = new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = VkInsert,
                        wScan = ScanInsert,
                        dwFlags = KeyeventfExtendedKey,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            var up = new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = VkInsert,
                        wScan = ScanInsert,
                        dwFlags = KeyeventfExtendedKey | KeyeventfKeyUp,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            uint sentDown = SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
            Thread.Sleep(12);
            uint sentUp = SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());

            return sentDown == 1 && sentUp == 1;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _timer.Dispose(); } catch { }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
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
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
