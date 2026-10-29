using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MediaColor = System.Windows.Media.Color;

namespace CHZMFRAMEUNLOCK1;

public partial class OsdWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;

    private readonly DispatcherTimer _hideTimer;
    private bool _shownOnce;

    public OsdWindow()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            HideAnimated();
        };

        SourceInitialized += (_, _) => MakeNoActivate();
    }

    public void ShowMessage(string title, string body, MediaColor? accent = null)
    {
        TitleText.Text = title;
        BodyText.Text = body;
        RootBorder.BorderBrush = new SolidColorBrush(Colors.White);

        PositionTopRight();

        if (!_shownOnce)
        {
            _shownOnce = true;
            Show();
        }
        else if (!IsVisible)
        {
            Show();
        }

        Topmost = true;
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void PositionTopRight()
    {
        Rect area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left + 8, area.Right - Width - 24);
        Top = area.Top + 24;
    }

    private void HideAnimated()
    {
        var animation = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180));
        animation.Completed += (_, _) =>
        {
            try { Hide(); } catch { }
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, animation);
    }

    private void MakeNoActivate()
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(handle, GwlExStyle);
            style |= WsExNoActivate | WsExToolWindow | WsExTransparent;
            SetWindowLong(handle, GwlExStyle, style);
        }
        catch
        {
            // ignore
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
