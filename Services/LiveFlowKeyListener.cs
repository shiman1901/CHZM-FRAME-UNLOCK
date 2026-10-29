using System;
using System.Runtime.InteropServices;
using System.Threading;
using Timer = System.Threading.Timer;

namespace CHZMFRAMEUNLOCK1.Services;

/// <summary>
/// Простой перехватчик одной клавиши для LIVE FLOW (по умолчанию F9).
/// Работает глобально, поэтому срабатывает и когда игра на переднем плане.
/// </summary>
public sealed class LiveFlowKeyListener : IDisposable
{
    private readonly Timer _timer;
    private readonly object _gate = new();

    private int _virtualKey = 0x78; // F9
    private bool _wasDown;
    private DateTime _lastFiredUtc = DateTime.MinValue;
    private bool _disposed;

    public LiveFlowKeyListener(int virtualKey = 0x78)
    {
        _virtualKey = virtualKey;
        _timer = new Timer(_ => Poll(), null, 45, 45);
    }

    /// <summary>Дополнительное условие срабатывания (например, не идёт запись клавиши).</summary>
    public Func<bool>? CanFire { get; set; }

    /// <summary>Клавиша нажата (один раз на нажатие).</summary>
    public event Action? Pressed;

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

            if ((DateTime.UtcNow - _lastFiredUtc).TotalMilliseconds < 350) return;
            if (CanFire is not null && !CanFire()) return;

            _lastFiredUtc = DateTime.UtcNow;

            try { Pressed?.Invoke(); } catch { }
        }
        catch
        {
            // перехватчик не должен ронять приложение
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
}
