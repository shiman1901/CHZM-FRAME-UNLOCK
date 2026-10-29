using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;
using ShapeRect = System.Windows.Shapes.Rectangle;
using CHZMFRAMEUNLOCK1.Services;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Path = System.IO.Path;

namespace CHZMFRAMEUNLOCK1;

public partial class MainWindow : Window
{
    private static readonly Color OnColor = Color.FromRgb(0x22, 0xC5, 0x5E);
    private static readonly Color OffColor = Color.FromRgb(0x8A, 0x8A, 0x90);
    private static readonly Color ErrorColor = Color.FromRgb(0xFF, 0x6B, 0x6B);
    private static readonly Color LiveColor = Color.FromRgb(0x38, 0xBD, 0xF8);

    private readonly TensorAutoService _tensor = new();
    private readonly LiveFlowBridge _liveFlow = new();
    private readonly GameLibraryScanner _scanner;
    private readonly MenuHotkey _menuHotkey;
    private readonly LiveFlowKeyListener _liveFlowKey;
    private readonly NitroKeyListener _nitroKey;
    private readonly OsdWindow _osd;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _frameGenWatch;
    private readonly Dictionary<string, TextBlock> _badges = new(StringComparer.OrdinalIgnoreCase);

    private List<GameEntry> _games = new();
    private GameEntry? _selected;
    private EngineMode? _manualEngine;
    private bool _forceLiveFlow;
    private bool _busy;
    private bool _scanning;
    private bool _capturingMenuKey;
    private bool _capturingLiveFlowKey;
    private bool _trayHintShown;
    private bool _pendingLiveFlow;
    private bool _autoPausedByAltTab;
    private bool _warmupAttempted;
    private int _tick;
    private CancellationTokenSource? _launchCts;

    public MainWindow()
    {
        InitializeComponent();

        _scanner = new GameLibraryScanner(_tensor);
        _osd = new OsdWindow();

        _menuHotkey = new MenuHotkey(_tensor.MenuKey)
        {
            CanFire = () => !_busy && !_capturingMenuKey && !_capturingLiveFlowKey && _selected is not null
        };

        _liveFlowKey = new LiveFlowKeyListener(_tensor.LiveFlowKey)
        {
            CanFire = () => !_busy && !_capturingMenuKey && !_capturingLiveFlowKey
        };

        _liveFlowKey.Pressed += () =>
            Dispatcher.BeginInvoke(new Action(() => _ = ToggleLiveFlowHotkeyAsync()));

        _nitroKey = new NitroKeyListener();
        _nitroKey.LongPressed += () =>
            Dispatcher.BeginInvoke(new Action(() => _ = ToggleLiveFlowHotkeyAsync()));

        _trayIcon = CreateTrayIcon();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();

        _frameGenWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _frameGenWatch.Tick += (_, _) => WatchLiveFlow();

        

        EncoreMenuToggle.IsChecked = _tensor.ShowEncoreMenu;
        EacToggle.IsChecked = _tensor.IgnoreAntiCheatWarnings;

        UpdateMenuKeyUi();
        UpdateLiveFlowLabels();
        UpdateSourceText();

        Loaded += async (_, _) =>
        {
            Refresh();
            _timer.Start();
            _frameGenWatch.Start();
            await LoadLibraryAsync();
        };

        StateChanged += (_, _) =>
        {
            UpdateMaximizeGlyph();

            if (WindowState == WindowState.Minimized)
                HideToTray();
        };

        Closed += (_, _) =>
        {
            _timer.Stop();
            _frameGenWatch.Stop();

            try { _launchCts?.Cancel(); } catch { }
            try { _trayIcon.Visible = false; _trayIcon.Dispose(); } catch { }
            try { _nitroKey.Dispose(); } catch { }
            try { _liveFlowKey.Dispose(); } catch { }
            try { _menuHotkey.Dispose(); } catch { }
            try { _osd.Close(); } catch { }
            try { _liveFlow.Dispose(); } catch { }
            try { _tensor.Dispose(); } catch { }
        };
    }

    // ===================== БИБЛИОТЕКА =====================

    private async Task LoadLibraryAsync()
    {
        if (_scanning) return;

        _scanning = true;
        RefreshButton.IsEnabled = false;
        LibraryCountText.Text = "SCANNING // TARGETS...";

        try
        {
            string? keep = _selected?.ExecutablePath;

            List<GameEntry> games = await Task.Run(() => _scanner.Scan());

            _games = games;
            _badges.Clear();
            RenderCards();

            LibraryCountText.Text = games.Count == 0
                ? "NO TARGETS // USE + ADD"
                : $"{games.Count} TARGETS // STEAM + EPIC + GOG + FOLDERS";

            GameEntry? selected = games.FirstOrDefault(g =>
                string.Equals(g.ExecutablePath, keep, StringComparison.OrdinalIgnoreCase));

            SelectGame(selected ?? games.FirstOrDefault());
        }
        catch (Exception ex)
        {
            LibraryCountText.Text = "SCAN ERROR // CHECK PATHS";
            ShowOsd("СКАНЕР ИГР", ex.Message, ErrorColor);
        }
        finally
        {
            _scanning = false;
            RefreshButton.IsEnabled = true;
            UpdateSourceText();
        }
    }

    private void RenderCards()
    {
        GamesPanel.Children.Clear();
        _badges.Clear();

        if (_games.Count == 0)
        {
            GamesPanel.Children.Add(new TextBlock
            {
                Text = "NO GAMES // USE + ADD",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 8,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                Width = 300,
                Margin = new Thickness(0, 0, 0, 20)
            });
        }

        for (int i = 0; i < _games.Count; i++)
            GamesPanel.Children.Add(BuildCard(_games[i], i + 1));

        GamesPanel.Children.Add(BuildAddCard());
    }

    private Border BuildCard(GameEntry game, int index)
    {
        bool selected = _selected is not null &&
                        string.Equals(_selected.ExecutablePath, game.ExecutablePath,
                            StringComparison.OrdinalIgnoreCase);

        var card = new Border
        {
            Width = 190,
            Height = 250,
            Margin = new Thickness(0, 0, 28, 28),
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(selected
                ? Colors.White
                : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            Background = new SolidColorBrush(Color.FromRgb(0x03, 0x03, 0x03)),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = game,
            SnapsToDevicePixels = true
        };

        var cover = new Grid { ClipToBounds = true };
        BitmapImage? bitmap = LoadCoverBitmap(game.CoverPath);

        bool smallIcon = bitmap is not null &&
                         bitmap.PixelWidth > 0 && bitmap.PixelWidth <= 96 &&
                         bitmap.PixelHeight > 0 && bitmap.PixelHeight <= 96;

        if (smallIcon)
        {
            cover.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x03, 0x03, 0x03))
            });

            var iconGrid = new Grid
            {
                Width = 64,
                Height = 64,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };

            iconGrid.Children.Add(new ShapeRect
            {
                Width = 64,
                Height = 1,
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });
            iconGrid.Children.Add(new ShapeRect
            {
                Width = 1,
                Height = 64,
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            });
            iconGrid.Children.Add(new System.Windows.Controls.Image
            {
                Source = bitmap,
                Width = 48,
                Height = 48,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });

            cover.Children.Add(iconGrid);
        }
        else if (bitmap is not null)
        {
            cover.Children.Add(new Border
            {
                Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill }
            });

            // лёгкое приглушение контраста/яркости
            cover.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00))
            });
        }
        else
        {
            cover.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x05))
            });

            cover.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(game.Name) ? "?" : game.Name[..1].ToUpperInvariant(),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 28,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });
        }

        // нижняя техническая строка поверх постера
        var labelStrip = new Grid
        {
            Height = 34,
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
            Background = new LinearGradientBrush(
                Color.FromArgb(0x00, 0x00, 0x00, 0x00),
                Color.FromArgb(0xE6, 0x00, 0x00, 0x00),
                90)
        };

        labelStrip.Children.Add(new TextBlock
        {
            Text = $"{index:00} // {game.Name.ToUpperInvariant()} [{ShortSource(game.Source)}]",
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 6.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 26,
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
            Margin = new Thickness(6, 0, 6, 6)
        });

        cover.Children.Add(labelStrip);

        string badge = game.BadgeText;
        if (string.IsNullOrWhiteSpace(badge) && game.IsRunning) badge = "RUNNING";

        if (!string.IsNullOrWhiteSpace(badge))
        {
            var badgeBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x03, 0x03, 0x03)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(4, 2, 4, 2),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Margin = new Thickness(6, 6, 0, 0)
            };

            var badgeText = new TextBlock
            {
                Text = badge.ToUpperInvariant(),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 6,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.White)
            };

            badgeBorder.Child = badgeText;
            cover.Children.Add(badgeBorder);
            _badges[game.ExecutablePath] = badgeText;
        }

        var brackets = new System.Collections.Generic.List<(ShapePath Path, Thickness Expanded)>();
        var bracketLayer = new Grid { IsHitTestVisible = false };
        AddBracket(bracketLayer, brackets, "M 0,8 L 0,0 L 8,0",
            System.Windows.HorizontalAlignment.Left, System.Windows.VerticalAlignment.Top,
            new Thickness(-3, -3, 0, 0), selected);
        AddBracket(bracketLayer, brackets, "M 0,0 L 8,0 L 8,8",
            System.Windows.HorizontalAlignment.Right, System.Windows.VerticalAlignment.Top,
            new Thickness(0, -3, -3, 0), selected);
        AddBracket(bracketLayer, brackets, "M 0,0 L 0,8 L 8,8",
            System.Windows.HorizontalAlignment.Left, System.Windows.VerticalAlignment.Bottom,
            new Thickness(-3, 0, 0, -3), selected);
        AddBracket(bracketLayer, brackets, "M 8,0 L 8,8 L 0,8",
            System.Windows.HorizontalAlignment.Right, System.Windows.VerticalAlignment.Bottom,
            new Thickness(0, 0, -3, -3), selected);

        cover.Children.Add(bracketLayer);
        card.Child = cover;

        card.MouseLeftButtonUp += (_, _) => SelectGame(game);

        card.MouseEnter += (_, _) =>
        {
            AnimateBrackets(brackets, expand: true);
            card.BorderBrush = new SolidColorBrush(Colors.White);
        };

        card.MouseLeave += (_, _) =>
        {
            if (!selected)
            {
                AnimateBrackets(brackets, expand: false);
                card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            }
        };

        return card;
    }

    private static string ShortSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "LOCAL";
        if (source.Contains("Steam", StringComparison.OrdinalIgnoreCase)) return "STEAM";
        if (source.Contains("Epic", StringComparison.OrdinalIgnoreCase)) return "EPIC";
        if (source.Contains("GOG", StringComparison.OrdinalIgnoreCase)) return "GOG";
        if (source.Contains("вручную", StringComparison.OrdinalIgnoreCase)) return "MANUAL";
        return "LOCAL";
    }

    private static void AddBracket(Grid layer,
        System.Collections.Generic.List<(ShapePath Path, Thickness Expanded)> list,
        string data, System.Windows.HorizontalAlignment horizontal,
        System.Windows.VerticalAlignment vertical, Thickness expanded, bool isExpanded)
    {
        var path = new ShapePath
        {
            Data = Geometry.Parse(data),
            Stroke = new SolidColorBrush(isExpanded
                ? Colors.White
                : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1,
            Fill = null,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Margin = isExpanded ? expanded : new Thickness(0),
            SnapsToDevicePixels = true
        };

        layer.Children.Add(path);
        list.Add((path, expanded));
    }

    private static void AnimateBrackets(
        System.Collections.Generic.List<(ShapePath Path, Thickness Expanded)> brackets, bool expand)
    {
        foreach (var bracket in brackets)
        {
            bracket.Path.Stroke = new SolidColorBrush(expand
                ? Colors.White
                : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));

            var animation = new ThicknessAnimation
            {
                To = expand ? bracket.Expanded : new Thickness(0),
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            bracket.Path.BeginAnimation(FrameworkElement.MarginProperty, animation);
        }
    }

    private Border BuildAddCard()
    {
        var card = new Border
        {
            Width = 190,
            Height = 250,
            Margin = new Thickness(0, 0, 28, 28),
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            Background = new SolidColorBrush(Color.FromRgb(0x03, 0x03, 0x03)),
            Cursor = System.Windows.Input.Cursors.Hand,
            SnapsToDevicePixels = true
        };

        var grid = new Grid();

        var plus = new Grid
        {
            Width = 32,
            Height = 32,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new RotateTransform(0)
        };

        plus.Children.Add(new ShapeRect
        {
            Width = 32,
            Height = 1,
            Fill = new SolidColorBrush(Colors.White),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        });
        plus.Children.Add(new ShapeRect
        {
            Width = 1,
            Height = 32,
            Fill = new SolidColorBrush(Colors.White),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        });

        grid.Children.Add(plus);

        var addText = new TextBlock
        {
            Text = "ДОБАВИТЬ",
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 6.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(0, 52, 0, 0),
            Opacity = 0
        };

        grid.Children.Add(addText);
        card.Child = grid;

        var rotation = (RotateTransform)plus.RenderTransform;

        card.MouseLeftButtonUp += (_, _) => AddGame_Click(card, new RoutedEventArgs());

        card.MouseEnter += (_, _) =>
        {
            card.BorderBrush = new SolidColorBrush(Colors.White);
            addText.Opacity = 1;

            var animation = new DoubleAnimation
            {
                To = 90,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            rotation.BeginAnimation(RotateTransform.AngleProperty, animation);
        };

        card.MouseLeave += (_, _) =>
        {
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            addText.Opacity = 0;

            var animation = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            rotation.BeginAnimation(RotateTransform.AngleProperty, animation);
        };

        return card;
    }
    private static BitmapImage? LoadCoverBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            if (!File.Exists(path)) return null;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void SelectGame(GameEntry? game)
    {
        string? previousExecutable = _selected?.ExecutablePath;

        // Смена игры: прогретый движок от прошлой игры больше не нужен.
        if (_liveFlow.IsWarm &&
            (game is null ||
             !string.Equals(_liveFlow.GameExecutable, game.ExecutablePath,
                 StringComparison.OrdinalIgnoreCase)))
        {
            try { _liveFlow.Stop(); } catch { }
            _autoPausedByAltTab = false;
            _warmupAttempted = false;
        }

        _selected = game;

        if (game is null)
        {
            _manualEngine = null;
            _forceLiveFlow = false;
        }
        else
        {
            EngineMode? manual = _tensor.GetManualMode(game.ExecutablePath);
            bool liveRunning = _liveFlow.IsWarm &&
                               string.Equals(_liveFlow.GameExecutable, game.ExecutablePath,
                                   StringComparison.OrdinalIgnoreCase);

            if (game.Profile.HasAntiCheatOrPreloader || game.Profile.ProxyModesBlocked || liveRunning)
            {
                // Античит/запущенный LIVE FLOW: только внешний захват.
                _forceLiveFlow = true;
                _manualEngine = null;
            }
            else if (game.InstalledMode is EngineMode installed)
            {
                // Показываем реально установленный в папке игры мод.
                _forceLiveFlow = false;
                _manualEngine = installed;
            }
            else if (manual is not null)
            {
                _manualEngine = manual;
                _forceLiveFlow = false;
            }
            else if (game.Profile.LiveFlowRecommended)
            {
                _forceLiveFlow = true;
                _manualEngine = null;
            }
            else
            {
                _forceLiveFlow = false;
                _manualEngine = game.Profile.Recommended;
            }

            game.IsRunning = IsGameRunning(game);
            _scanner.RefreshStatus(game);
        }

        RenderCards();
        UpdateSelectedPanel();

        if (!string.Equals(previousExecutable, _selected?.ExecutablePath,
                StringComparison.OrdinalIgnoreCase))
            FlashTelemetry();
    }

    private void UpdateSelectedPanel()
    {
        GameEntry? game = _selected;

        if (game is null)
        {
            SelectedNameText.Text = "NO GAME SELECTED";
            SelectedMetaText.Text = "SELECT A TARGET //";
            HeaderGameText.Text = "NO GAME SELECTED";
            VerdictText.Text = string.Empty;
            VerdictDetailText.Text = string.Empty;
            StatusText.Text = string.Empty;

            CaptureValueText.Text = "—";
            AntiCheatValueText.Text = "—";
            DlssValueText.Text = "—";
            MultiplierValueText.Text = "[ 2X ]";
            HotkeyValueText.Text = $"[ {_tensor.LiveFlowKeyName.ToUpperInvariant()} ]";

            PowerButton.IsEnabled = false;
            LaunchButton.IsEnabled = false;
            ModeClassicButton.IsEnabled = false;
            ModeEncoreButton.IsEnabled = false;
            ModeSmoothButton.IsEnabled = false;
            ModeLiveFlowButton.IsEnabled = false;
            RemoveModButton.IsEnabled = false;

            PowerButton.Content = "ВКЛЮЧИТЬ УСКОРЕНИЕ";
            PowerButton.Background = new SolidColorBrush(Color.FromRgb(0x03, 0x03, 0x03));
            PowerButton.BorderBrush = new SolidColorBrush(Colors.White);
            PowerButton.Foreground = new SolidColorBrush(Colors.White);

            ModeClassicButton.Tag = null;
            ModeEncoreButton.Tag = null;
            ModeSmoothButton.Tag = null;
            ModeLiveFlowButton.Tag = null;
            ClassicQualityPanel.Visibility = Visibility.Collapsed;
            AntiCheatHint.Visibility = Visibility.Collapsed;

            UpdateMethodVisibility();
            return;
        }

        SelectedNameText.Text = game.Name.ToUpperInvariant();
        SelectedMetaText.Text = $"{ShortSource(game.Source)} // {game.Directory.ToUpperInvariant()}";
        HeaderGameText.Text = game.Name.ToUpperInvariant();

        VerdictText.Text = game.Profile.Verdict;
        VerdictDetailText.Text = string.Empty;

        bool liveForGame = _liveFlow.IsWarm &&
                           string.Equals(_liveFlow.GameExecutable, game.ExecutablePath,
                               StringComparison.OrdinalIgnoreCase);

        bool proxyEnabled = game.InstalledMode is not null && game.ModEnabled;
        bool liveSelected = _forceLiveFlow;

        if (liveSelected)
        {
            PowerButton.Content = liveForGame && _liveFlow.IsRunning
                ? "ВЫКЛЮЧИТЬ LIVE FLOW"
                : "ВКЛЮЧИТЬ LIVE FLOW";

            PowerButton.Background = new SolidColorBrush(Color.FromRgb(0x03, 0x03, 0x03));
            PowerButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x00));
            PowerButton.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x00));
        }
        else if (proxyEnabled)
        {
            PowerButton.Content = "ВЫКЛЮЧИТЬ УСКОРЕНИЕ";
            PowerButton.Background = new SolidColorBrush(Color.FromRgb(0x03, 0x03, 0x03));
            PowerButton.BorderBrush = new SolidColorBrush(Colors.White);
            PowerButton.Foreground = new SolidColorBrush(Colors.White);
        }
        else
        {
            PowerButton.Content = "ВКЛЮЧИТЬ УСКОРЕНИЕ";
            PowerButton.Background = new SolidColorBrush(Color.FromRgb(0x03, 0x03, 0x03));
            PowerButton.BorderBrush = new SolidColorBrush(Colors.White);
            PowerButton.Foreground = new SolidColorBrush(Colors.White);
        }

        PowerButton.IsEnabled = !_busy;
        LaunchButton.IsEnabled = !_busy;
        RemoveModButton.IsEnabled = !_busy;

        // В античит-играх внутренние прокси запрещены, но галочка пользователя снимает запрет.
        bool proxyBlocked = (game.Profile.ProxyModesBlocked ||
                             game.Profile.HasAntiCheatOrPreloader) &&
                            !_tensor.IgnoreAntiCheatWarnings;

        ModeClassicButton.IsEnabled = !_busy && !proxyBlocked && game.Profile.HasNativeDlssFg;
        ModeEncoreButton.IsEnabled = !_busy && !proxyBlocked;
        ModeSmoothButton.IsEnabled = !_busy && !proxyBlocked;
        ModeLiveFlowButton.IsEnabled = !_busy && LiveFlowBridge.IsInstalled;

        ClassicSubText.Text = "DLSS FG // GAME SLIDER";

        ModeClassicButton.Tag = !_forceLiveFlow && _manualEngine == EngineMode.ClassicSm86 ? "on" : null;
        ModeEncoreButton.Tag = !_forceLiveFlow && _manualEngine == EngineMode.RtxEncore ? "on" : null;
        ModeSmoothButton.Tag = !_forceLiveFlow && _manualEngine == EngineMode.SmoothMotion ? "on" : null;
        ModeLiveFlowButton.Tag = _forceLiveFlow || liveForGame ? "live" : null;

        UpdateMultiplierChips(game);
        UpdateLiveFlowLabels();
        UpdateMethodVisibility();
        UpdateTelemetry(game, liveForGame, liveSelected);

        if (string.IsNullOrWhiteSpace(StatusText.Text) || !_busy)
        {
            if (liveSelected)
            {
                StatusText.Text = liveForGame
                    ? (_liveFlow.IsRunning ? LiveFlowStatusLine() : "LIVE FLOW // STANDBY")
                    : "LIVE FLOW // EXTERNAL CAPTURE // NO GAME FILES";
            }
            else
            {
                StatusText.Text = game.InstalledMode is null
                    ? "MOD NOT INSTALLED // READY TO DEPLOY"
                    : game.ModEnabled
                        ? "ACTIVE // " + TensorAutoService.EngineDisplayName(game.InstalledMode.Value).ToUpperInvariant()
                        : "INSTALLED // DISABLED";
            }
        }

        if (liveForGame && _liveFlow.IsRunning)
            StatusText.Text = LiveFlowStatusLine();
    }

    private int CurrentMultiplier(GameEntry game)
    {
        if (_forceLiveFlow || _manualEngine == EngineMode.SmoothMotion) return 2;

        if (_manualEngine == EngineMode.ClassicSm86)
            return ClassicFramesToMultiplier(TensorAutoService.ReadClassicMaxFrames(game.Directory));

        if (_manualEngine == EngineMode.RtxEncore)
            return TensorAutoService.ReadEncoreMultiplier(game.Directory);

        return 2;
    }

    private void UpdateTelemetry(GameEntry game, bool liveForGame, bool liveSelected)
    {
        bool antiCheat = game.Profile.HasAntiCheatOrPreloader ||
                         game.Profile.ProxyModesBlocked;

        bool capture = liveSelected || liveForGame;

        CaptureValueText.Text = capture
            ? "DXGI + OPT_FLOW"
            : game.InstalledMode is null ? "STANDBY" : "PROXY // DLL";

        if (!antiCheat)
            AntiCheatValueText.Text = "CLEAR";
        else
            AntiCheatValueText.Text = _tensor.IgnoreAntiCheatWarnings ? "BYPASS" : "DETECTED";

        AntiCheatValueText.Foreground = new SolidColorBrush(antiCheat
            ? Colors.White
            : Color.FromRgb(0x66, 0x66, 0x66));

        DlssValueText.Text = game.Profile.HasNativeDlssFg
            ? "DLSS-G // READY"
            : game.Profile.HasNativeDlss ? "DLSS // READY" : "NONE";

        MultiplierValueText.Text = $"[ {CurrentMultiplier(game)}X ]";
        HotkeyValueText.Text = $"[ {_tensor.LiveFlowKeyName.ToUpperInvariant()} ]";
    }

    private void FlashTelemetry()
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(120),
            FillBehavior = FillBehavior.Stop
        };

        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromPercent(0.0)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.8, KeyTime.FromPercent(0.25)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.2, KeyTime.FromPercent(0.55)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0)));

        RightPanel.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>Показывает только те настройки, которые относятся к выбранному методу.</summary>
    private void UpdateMethodVisibility()
    {
        bool live = _forceLiveFlow;
        bool classic = !live && _manualEngine == EngineMode.ClassicSm86;
        bool encore = !live && _manualEngine == EngineMode.RtxEncore;
        bool smooth = !live && _manualEngine == EngineMode.SmoothMotion;

        // Множители: 2X есть всегда, 3X/4X/6X — только Classic и Encore.
        Frames2.Visibility = Visibility.Visible;
        Frames3.Visibility = classic || encore ? Visibility.Visible : Visibility.Collapsed;
        Frames4.Visibility = classic || encore ? Visibility.Visible : Visibility.Collapsed;
        Frames6.Visibility = classic || encore ? Visibility.Visible : Visibility.Collapsed;

        // Качество Classic: M-INT8 или Off + FG Lite.
        ClassicQualityPanel.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        if (classic && _selected is not null) UpdateClassicProfileChips(_selected);

        // Меню Encore: нужно только для Encore и Smooth Motion.
        Visibility encoreMenu = encore || smooth ? Visibility.Visible : Visibility.Collapsed;
        EncoreMenuToggle.Visibility = encoreMenu;
        MenuKeyButton.Visibility = encoreMenu;
        MenuKeyHint.Visibility = encoreMenu;

        // Бинд клавиши LIVE FLOW виден всегда — это часть понятной панели.
        LiveFlowKeyButton.Visibility = Visibility.Visible;
        LiveFlowKeyHint.Visibility = Visibility.Visible;

        // Понятная причина, почему внутренние методы заблокированы.
        bool antiCheat = _selected is not null &&
                         (_selected.Profile.HasAntiCheatOrPreloader ||
                          _selected.Profile.ProxyModesBlocked);

        if (antiCheat)
        {
            AntiCheatHint.Text = _tensor.IgnoreAntiCheatWarnings
                ? "PROXY BYPASS // RISK ACCEPTED"
                : "PROXY LOCK // ANTI-CHEAT DETECTED";

            AntiCheatHint.Foreground = new SolidColorBrush(_tensor.IgnoreAntiCheatWarnings
                ? Colors.White
                : Color.FromRgb(0x88, 0x88, 0x88));
        }

        AntiCheatHint.Visibility = antiCheat ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMultiplierChips(GameEntry game)
    {
        int multiplier;

        if (_forceLiveFlow || _manualEngine == EngineMode.SmoothMotion)
        {
            multiplier = 2;
        }
        else if (_manualEngine == EngineMode.ClassicSm86)
        {
            multiplier = ClassicFramesToMultiplier(TensorAutoService.ReadClassicMaxFrames(game.Directory));
        }
        else if (_manualEngine == EngineMode.RtxEncore)
        {
            multiplier = TensorAutoService.ReadEncoreMultiplier(game.Directory);
        }
        else
        {
            multiplier = 2;
        }

        SetChip(Frames2, multiplier == 2);
        SetChip(Frames3, multiplier == 3);
        SetChip(Frames4, multiplier == 4);
        SetChip(Frames6, multiplier == 6);
    }

    private static int ClassicFramesToMultiplier(int frames) => Math.Clamp(frames, 0, 5) switch
    {
        1 => 2,
        2 => 3,
        3 => 4,
        4 => 5,
        5 => 6,
        _ => 4
    };

    private void UpdateClassicProfileChips(GameEntry game)
    {
        string sr = TensorAutoService.ReadClassicSuperResolution(game.Directory);
        string preset = TensorAutoService.ReadClassicPreset(game.Directory);

        bool sharp = sr == "M-INT8" && preset != "C";
        bool maxFps = sr == "Off" && preset == "C";

        SetChip(ClassicSharpButton, sharp);
        SetChip(ClassicFpsButton, maxFps);
    }

    private async void ClassicProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _selected is null) return;

        GameEntry game = _selected;
        bool maxFps = string.Equals(button.Uid, "fps", StringComparison.OrdinalIgnoreCase);

        await ExecuteAsync(() =>
        {
            TensorToggleResult first = _tensor.SetClassicSuperResolution(
                maxFps ? "Off" : "M-INT8", game.Directory);

            if (first.Action == TensorToggleAction.Failed) return first;

            return _tensor.SetClassicPreset(maxFps ? "C" : "A", game.Directory);
        }, maxFps ? "включаю Макс. FPS (Off + FG Lite)…" : "включаю Чёткость M-INT8…");

        UpdateClassicProfileChips(game);
    }

    private static void SetChip(Button button, bool on) => button.Tag = on ? "on" : null;

    private void UpdateBadge(GameEntry game)
    {
        if (_badges.TryGetValue(game.ExecutablePath, out TextBlock? text))
        {
            text.Text = game.BadgeText;
            text.Visibility = string.IsNullOrWhiteSpace(game.BadgeText)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    // ===================== ГЛАВНОЕ ДЕЙСТВИЕ =====================

    private async void Power_Click(object sender, RoutedEventArgs e) => await ToggleAccelerationAsync();

    private async Task ToggleAccelerationAsync()
    {
        if (_busy) return;

        GameEntry? game = _selected;

        if (game is null)
        {
            ShowOsd("ИГРА НЕ ВЫБРАНА", "Выберите карточку игры слева", OffColor);
            return;
        }

        // Выбран LIVE FLOW: кнопка отвечает ТОЛЬКО за LIVE FLOW.
        if (_forceLiveFlow)
        {
            await ToggleLiveFlowAsync(game);
            return;
        }

        bool proxyEnabled = game.InstalledMode is not null && game.ModEnabled;

        if (proxyEnabled)
        {
            // Перед выключением прокси гасим внешний захват, если он был на этой игре.
            if (_liveFlow.IsWarm &&
                string.Equals(_liveFlow.GameExecutable, game.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                try { _liveFlow.Stop(); } catch { }
                _autoPausedByAltTab = false;
            }

            EngineMode mode = game.InstalledMode!.Value;
            await ExecuteAsync(
                () => _tensor.ApplyEngineToGamePath(mode, game.Directory, game.ExecutablePath),
                "выключаю мод…");
            return;
        }

        EngineMode? engine = _manualEngine ?? game.Profile.Recommended;

        if (engine is null)
        {
            ShowOsd("НЕТ ПОДХОДЯЩЕГО МЕТОДА",
                "Выберите метод в ручных настройках", ErrorColor);
            return;
        }

        // Если пользователь разрешил моды в античит-играх — не блокируем.
        if ((game.Profile.HasAntiCheatOrPreloader || game.Profile.ProxyModesBlocked) &&
            !_tensor.IgnoreAntiCheatWarnings)
        {
            ShowOsd("🛡 АНТИЧИТ",
                "Внутренние прокси в этой игре запрещены · включите галочку «Разрешить все моды»",
                ErrorColor);
            return;
        }

        EngineMode selected = engine.Value;
        await ExecuteAsync(
            () => _tensor.ApplyEngineToGamePath(selected, game.Directory, game.ExecutablePath),
            "ставлю мод в папку игры…");
    }

    // ===================== LIVE FLOW (МГНОВЕННО) =====================

    /// <summary>Убрать прокси-мод из папки игры, когда выбран LIVE FLOW.</summary>
    private async Task RemoveProxyModForLiveFlowAsync(GameEntry game)
    {
        if (_busy) return;

        _busy = true;
        PowerButton.IsEnabled = false;
        LaunchButton.IsEnabled = false;
        RemoveModButton.IsEnabled = false;
        StatusText.Text = "убираю прокси-мод, оставляю только LIVE FLOW…";

        try
        {
            await Task.Run(() => _tensor.RemoveEverythingFromDirectory(game.Directory));
            _scanner.RefreshStatus(game);
            UpdateBadge(game);
        }
        catch
        {
            // файл мог быть занят игрой — LIVE FLOW всё равно доступен
        }
        finally
        {
            _busy = false;
            StatusText.Text = "Прокси-мод убран · активен только LIVE FLOW.";
            UpdateSelectedPanel();
            UpdateTrayText();
        }
    }

    /// <summary>Прогретый движок: включение/пауза за доли секунды, без убийства процесса.</summary>
    private async Task ToggleLiveFlowAsync(GameEntry game)
    {
        bool liveForGame = _liveFlow.IsWarm &&
                           string.Equals(_liveFlow.GameExecutable, game.ExecutablePath,
                               StringComparison.OrdinalIgnoreCase);

        if (liveForGame && _liveFlow.IsRunning)
        {
            _liveFlow.Deactivate();
            _autoPausedByAltTab = false;
            PlayFrameGenSound(on: false);
            StatusText.Text = "LIVE FLOW на паузе · включится мгновенно";
            ShowOsd("⚡ LIVE FLOW: ВЫКЛ", "движок спит в памяти — включится мгновенно", OffColor);
            UpdateSelectedPanel();
            UpdateTrayText();
            return;
        }

        if (!LiveFlowBridge.IsInstalled)
        {
            ShowOsd("LIVE FLOW НЕ НАЙДЕН", LiveFlowBridge.InstallReport, ErrorColor);
            return;
        }

        if (!IsGameRunning(game))
        {
            _pendingLiveFlow = true;
            StatusText.Text = "LIVE FLOW включится автоматически после запуска игры.";
            ShowOsd("⚡ LIVE FLOW ГОТОВ",
                "Нажмите «ЗАПУСТИТЬ ИГРУ» — захват стартует сам", LiveColor);
            return;
        }

        await StartLiveFlowAsync(game);
    }

    /// <summary>Клавиша LIVE FLOW / долгое удержание [N]: работает для игры на экране.</summary>
    private async Task ToggleLiveFlowHotkeyAsync()
    {
        if (_busy) return;

        GameEntry? running = FindRunningGame();

        if (running is not null && !ReferenceEquals(running, _selected))
            SelectGame(running);

        GameEntry? game = running ?? _selected;

        if (game is null)
        {
            ShowOsd("ИГРА НЕ НАЙДЕНА", "Запустите игру и повторите", OffColor);
            return;
        }

        // Клавиша всегда управляет LIVE FLOW, независимо от выбранного метода.
        if (!_forceLiveFlow)
        {
            _forceLiveFlow = true;
            _manualEngine = null;
            _tensor.ClearManualMode(game.ExecutablePath);
        }

        await ToggleLiveFlowAsync(game);
    }

    private async Task ExecuteAsync(Func<TensorToggleResult> work, string busyText)
    {
        if (_busy) return;

        _busy = true;
        PowerButton.IsEnabled = false;
        LaunchButton.IsEnabled = false;
        RemoveModButton.IsEnabled = false;
        StatusText.Text = busyText;

        try
        {
            TensorToggleResult result = await Task.Run(work);

            if (_selected is not null)
            {
                _scanner.RefreshStatus(_selected);
                UpdateBadge(_selected);
            }

            ShowOsd(result.Title, result.Detail,
                result.Action is TensorToggleAction.Installed or TensorToggleAction.Enabled
                    ? OnColor
                    : result.Action is TensorToggleAction.Failed or TensorToggleAction.BlockedByAntiCheat
                        ? ErrorColor
                        : OffColor);
        }
        catch (Exception ex)
        {
            ShowOsd("ОШИБКА", ex.Message, ErrorColor);
        }
        finally
        {
            _busy = false;
            StatusText.Text = string.Empty;
            UpdateSelectedPanel();
            UpdateTrayText();
        }
    }

    // ===================== ЗАПУСК ИГРЫ =====================

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _busy) return;

        GameEntry game = _selected;
        _pendingLiveFlow = _forceLiveFlow;

        try
        {
            if (!string.IsNullOrWhiteSpace(game.SteamAppId))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "steam://rungameid/" + game.SteamAppId,
                    UseShellExecute = true
                });
            }
            else
            {
                if (!File.Exists(game.ExecutablePath))
                {
                    ShowOsd("ФАЙЛ НЕ НАЙДЕН", game.ExecutablePath, ErrorColor);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = game.ExecutablePath,
                    WorkingDirectory = game.Directory,
                    UseShellExecute = true
                });
            }

            StatusText.Text = "Игра запускается…";

            if (_pendingLiveFlow)
            {
                ShowOsd("⚡ LIVE FLOW", "Жду окно игры — захват стартует автоматически", LiveColor);
                await StartLiveFlowWhenReadyAsync(game);
            }
        }
        catch (Exception ex)
        {
            ShowOsd("НЕ УДАЛОСЬ ЗАПУСТИТЬ", ex.Message, ErrorColor);
        }
    }

    private async Task StartLiveFlowWhenReadyAsync(GameEntry game)
    {
        try { _launchCts?.Cancel(); } catch { }

        var cts = new CancellationTokenSource();
        _launchCts = cts;

        try
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                await Task.Delay(1500, cts.Token);

                if (cts.IsCancellationRequested) return;
                if (_liveFlow.IsRunning) return;

                if (!IsGameRunning(game)) continue;
                if (!IsGameWindowReady(game)) continue;

                await StartLiveFlowAsync(game);

                if (_liveFlow.IsRunning) return;
            }

            if (!cts.IsCancellationRequested)
                ShowOsd("LIVE FLOW", "Окно игры не появилось — нажмите кнопку вручную", OffColor);
        }
        catch (OperationCanceledException)
        {
            // отменено
        }
    }

    private async Task StartLiveFlowAsync(GameEntry game)
    {
        if (_busy) return;

        if (!LiveFlowBridge.IsInstalled)
        {
            ShowOsd("LIVE FLOW НЕ НАЙДЕН", LiveFlowBridge.InstallReport, ErrorColor);
            return;
        }

        // LIVE FLOW и прокси-мод не должны жить одновременно:
        // убираем CLASSIC/ENCORE/SMOOTH из папки игры перед включением.
        try
        {
            if (game.InstalledMode is not null ||
                TensorAutoService.HasInstalledEngine(game.Directory))
            {
                StatusText.Text = "убираю прокси-мод, включаю LIVE FLOW…";

                await Task.Run(() => _tensor.RemoveEverythingFromDirectory(game.Directory));

                _scanner.RefreshStatus(game);
                UpdateBadge(game);
            }
        }
        catch
        {
            // прокси мог быть занят игрой — LIVE FLOW всё равно пробуем
        }

        _busy = true;
        PowerButton.IsEnabled = false;
        LaunchButton.IsEnabled = false;
        StatusText.Text = _liveFlow.IsWarm
            ? "включаю прогретый LIVE FLOW…"
            : "прогреваю LIVE FLOW: DXGI + Optical Flow…";

        try
        {
            LiveFlowOptions options = _tensor.GetLiveFlowOptions();
            var active = new ActiveGameInfo(game.ProcessName, game.ExecutablePath, game.Directory);

            (bool ok, string detail) = await Task.Run(() =>
            {
                bool started = _liveFlow.Activate(active, options, out string message);
                return (started, message);
            });

            if (ok)
            {
                _pendingLiveFlow = false;
                _autoPausedByAltTab = false;
                PlayFrameGenSound(on: true);
                UpdateTrayText();
                ShowOsd("⚡ LIVE FLOW: ВКЛЮЧЕН", detail, OnColor);
            }
            else
            {
                ShowOsd("LIVE FLOW: НЕ ЗАПУСТИЛСЯ", detail, ErrorColor);
            }
        }
        catch (Exception ex)
        {
            ShowOsd("LIVE FLOW: ОШИБКА", ex.Message, ErrorColor);
        }
        finally
        {
            _busy = false;
            StatusText.Text = string.Empty;
            UpdateSelectedPanel();
        }
    }

    private void StopLiveFlow()
    {
        try { _liveFlow.Stop(); } catch { }

        _pendingLiveFlow = false;
        _autoPausedByAltTab = false;
        _warmupAttempted = false;
        ShowOsd("⚡ LIVE FLOW: ВЫКЛЮЧЕН", "движок полностью закрыт", OffColor);

        UpdateSelectedPanel();
        UpdateTrayText();
    }

    private string LiveFlowStatusLine()
    {
        return $"LIVE FLOW 2X · DXGI · Optical Flow · " +
               $"worker {_liveFlow.WorkerPid} · кадров {_liveFlow.Frames}";
    }

    private bool IsGameRunning(GameEntry game)
    {
        try
        {
            foreach (Process process in Process.GetProcessesByName(game.ProcessName))
            {
                using (process)
                {
                    string? path = TryGetPath(process);

                    if (string.IsNullOrWhiteSpace(path) ||
                        string.Equals(path, game.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                        PathBelongsTo(path, game.Directory))
                        return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private bool IsGameWindowReady(GameEntry game)
    {
        try
        {
            foreach (Process process in Process.GetProcessesByName(game.ProcessName))
            {
                using (process)
                {
                    string? path = TryGetPath(process);

                    if (!string.IsNullOrWhiteSpace(path) &&
                        !string.Equals(path, game.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                        !PathBelongsTo(path, game.Directory))
                        continue;

                    if (process.MainWindowHandle != IntPtr.Zero) return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static string? TryGetPath(Process process)
    {
        try { return process.MainModule?.FileName; } catch { return null; }
    }

    private static bool PathBelongsTo(string path, string directory)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ===================== [N] / КЛАВИША / ТРЕЙ =====================

    /// <summary>Раз в 250 мс: авто-пауза при Alt+Tab, автовозобновление, стоп при выходе из игры.</summary>
    private void WatchLiveFlow()
    {
        if (_busy) return;
        if (_liveFlow.State == LiveFlowState.Cold) return;

        // Игра закрылась — полностью глушим движок (окну возвращается рамка).
        if (!string.IsNullOrEmpty(_liveFlow.GameExecutable) &&
            !IsExecutableRunning(_liveFlow.GameExecutable))
        {
            StopLiveFlow();
            return;
        }

        if (_liveFlow.State == LiveFlowState.Active)
        {
            // Свернул игру (браузер, телеграм, рабочий стол) — мгновенная пауза.
            if (!ForegroundBelongsToGame())
            {
                _liveFlow.Deactivate();
                _autoPausedByAltTab = true;
                PlayFrameGenSound(on: false);
                ShowOsd("⏸ LIVE FLOW: ПАУЗА", "вернись в игру — включится сам", LiveColor);
                UpdateSelectedPanel();
                UpdateTrayText();
            }
        }
        else if (_liveFlow.State == LiveFlowState.Warm && _autoPausedByAltTab &&
                 ForegroundBelongsToGame())
        {
            // Вернулась в игру — мгновенно возобновляем без нажатий.
            _ = ResumeLiveFlowAsync();
        }
    }

    private async Task ResumeLiveFlowAsync()
    {
        ActiveGameInfo? game = _liveFlow.LastGame ?? _tensor.ResolveTargetGame();
        if (game is null) return;

        _busy = true;
        try
        {
            LiveFlowOptions options = _tensor.GetLiveFlowOptions();

            (bool ok, string _) = await Task.Run(() =>
            {
                bool started = _liveFlow.Activate(game, options, out string message);
                return (started, message);
            });

            if (ok)
            {
                _autoPausedByAltTab = false;
                PlayFrameGenSound(on: true);
                ShowOsd("⚡ LIVE FLOW: ВКЛ", "автовозобновление", OnColor);
            }
        }
        finally
        {
            _busy = false;
            UpdateSelectedPanel();
            UpdateTrayText();
        }
    }

    /// <summary>Прогрев «под капотом»: как только игра запущена — греем движок в фоне.</summary>
    private void MaybeWarmUpLiveFlow()
    {
        if (_liveFlow.State != LiveFlowState.Cold) return;
        if (_warmupAttempted || _busy) return;
        if (!LiveFlowBridge.IsInstalled) return;

        GameEntry? game = _selected is not null && IsGameRunning(_selected)
            ? _selected
            : _games.FirstOrDefault(IsGameRunning);

        if (game is null) return;

        _warmupAttempted = true;

        var active = new ActiveGameInfo(game.ProcessName, game.ExecutablePath, game.Directory);

        _ = Task.Run(() =>
        {
            try { _liveFlow.WarmUp(active, out _); } catch { }
        });
    }

    /// <summary>Активное окно — это игра (или наше собственное окно)?</summary>
    private bool ForegroundBelongsToGame()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;

            // Наше собственное окно — не считаем уходом из игры.
            if (pid == (uint)Environment.ProcessId) return true;

            string? path = TensorAutoService.TryGetProcessPath((int)pid);
            if (string.IsNullOrWhiteSpace(path)) return true; // не смогли узнать — не мешаем

            if (string.Equals(path, _liveFlow.GameExecutable, StringComparison.OrdinalIgnoreCase))
                return true;

            string gameDir = _liveFlow.GameDirectory;
            if (!string.IsNullOrEmpty(gameDir))
            {
                try
                {
                    return path.StartsWith(
                        Path.GetFullPath(gameDir).TrimEnd('\\') + "\\",
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsExecutableRunning(string executablePath)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(executablePath);

            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    string? path = TryGetPath(process);
                    if (string.IsNullOrWhiteSpace(path) ||
                        string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    /// <summary>Короткий приятный сигнал: вверх при включении, вниз при выключении.</summary>
    private static void PlayFrameGenSound(bool on)
    {
        Task.Run(() =>
        {
            try
            {
                if (on)
                {
                    Console.Beep(600, 80);
                    Console.Beep(900, 80);
                }
                else
                {
                    Console.Beep(800, 80);
                    Console.Beep(500, 80);
                }
            }
            catch
            {
                // звук не критичен
            }
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private GameEntry? FindRunningGame()
    {
        ActiveGameInfo? active = _tensor.ResolveTargetGame();

        if (active is not null)
        {
            GameEntry? match = _games.FirstOrDefault(g =>
                string.Equals(g.ExecutablePath, active.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(g.ProcessName, active.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                PathBelongsTo(active.ExecutablePath, g.Directory));

            if (match is not null) return match;

            var entry = new GameEntry
            {
                Name = active.ProcessName,
                Directory = active.Directory,
                RootDirectory = active.Directory,
                ExecutablePath = active.ExecutablePath,
                ProcessName = active.ProcessName,
                Source = "Запущена сейчас",
                Profile = GameLibraryScanner.BuildProfile(active.Directory)
            };

            _games.Add(entry);
            RenderCards();
            return entry;
        }

        return _games.FirstOrDefault(IsGameRunning);
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/app.ico"));

            if (resource?.Stream is not null)
                return new System.Drawing.Icon(resource.Stream);
        }
        catch
        {
            // остаётся стандартная иконка
        }

        return System.Drawing.SystemIcons.Application;
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("LIVE FLOW включить/выключить", null,
            (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = ToggleLiveFlowHotkeyAsync())));
        menu.Items.Add("Открыть", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApplication());

        var icon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "CHZM Frame Unlock",
            Visible = true,
            ContextMenuStrip = menu
        };

        icon.DoubleClick += (_, _) => RestoreFromTray();
        return icon;
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        _trayIcon.Visible = true;

        if (_trayHintShown) return;
        _trayHintShown = true;

        try
        {
            _trayIcon.ShowBalloonTip(1800, "CHZM Frame Unlock",
                "Свёрнуто в трей · двойной клик откроет окно", Forms.ToolTipIcon.Info);
        }
        catch { }
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        try { _launchCts?.Cancel(); } catch { }
        try { _trayIcon.Visible = false; } catch { }
        Close();
    }

    private void UpdateTrayText()
    {
        try
        {
            string text = _liveFlow.IsRunning
                ? "CHZM · LIVE FLOW 2X ВКЛ"
                : _liveFlow.State == LiveFlowState.Warm
                    ? "CHZM · LIVE FLOW прогрет"
                    : _selected is not null
                        ? $"CHZM · {_selected.Name}"
                        : "CHZM Frame Unlock";

            _trayIcon.Text = text.Length <= 63 ? text : text[..63];
        }
        catch { }
    }

    private void Tick()
    {
        _tick++;

        if (_selected is not null && !_busy)
        {
            bool wasRunning = _selected.IsRunning;
            _selected.IsRunning = IsGameRunning(_selected);
            _scanner.RefreshStatus(_selected);
            UpdateBadge(_selected);

            bool badgeChanged = _selected.BadgeText != _lastBadge;
            _lastBadge = _selected.BadgeText;

            if (wasRunning != _selected.IsRunning || badgeChanged || _tick % 5 == 0)
                UpdateSelectedPanel();
        }

        // Как только игра запущена — тихо прогреваем LIVE FLOW в памяти.
        MaybeWarmUpLiveFlow();

        if (_liveFlow.IsRunning && _selected is not null &&
            string.Equals(_liveFlow.GameExecutable, _selected.ExecutablePath,
                StringComparison.OrdinalIgnoreCase))
            StatusText.Text = LiveFlowStatusLine();

        UpdateTrayText();
    }

    private string _lastBadge = string.Empty;

    // ===================== РУЧНЫЕ НАСТРОЙКИ =====================

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _selected is null) return;

        if (button.IsEnabled == false) return;

        if (string.Equals(button.Uid, "LiveFlow", StringComparison.OrdinalIgnoreCase))
        {
            _forceLiveFlow = true;
            _manualEngine = null;
            _tensor.ClearManualMode(_selected.ExecutablePath);

            // Один метод на игру: при выборе LIVE FLOW сразу убираем прокси-мод.
            if (_selected.InstalledMode is not null ||
                TensorAutoService.HasInstalledEngine(_selected.Directory))
            {
                _ = RemoveProxyModForLiveFlowAsync(_selected);
            }

            StatusText.Text = "Выбран LIVE FLOW — файлы игры не трогаются.";
            UpdateSelectedPanel();
            return;
        }

        if (!Enum.TryParse(button.Uid, out EngineMode mode)) return;

        if (mode == EngineMode.ClassicSm86 && !_selected.Profile.HasNativeDlssFg)
        {
            ShowOsd("CLASSIC НЕДОСТУПЕН", "В игре нет нативного DLSS-G", ErrorColor);
            return;
        }

        // Античит-игра: внутренние методы недоступны, если пользователь не разрешил.
        if ((_selected.Profile.HasAntiCheatOrPreloader || _selected.Profile.ProxyModesBlocked) &&
            !_tensor.IgnoreAntiCheatWarnings)
        {
            ShowOsd("🛡 АНТИЧИТ", "В этой игре доступен только внешний LIVE FLOW", ErrorColor);
            return;
        }

        // Переключились на прокси-мод — внешний захват больше не нужен.
        if (_liveFlow.IsWarm)
        {
            try { _liveFlow.Stop(); } catch { }
            _autoPausedByAltTab = false;
            _warmupAttempted = false;
        }

        _forceLiveFlow = false;
        _manualEngine = mode;
        _tensor.SelectEngineManual(mode, _selected.ExecutablePath);
        StatusText.Text = $"Выбран {TensorAutoService.EngineDisplayName(mode)}.";
        UpdateSelectedPanel();
    }

    private async void Multiplier_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _selected is null) return;
        if (!int.TryParse(button.Uid, out int multiplier)) return;

        GameEntry game = _selected;

        if (_forceLiveFlow || _manualEngine == EngineMode.SmoothMotion)
        {
            // LIVE FLOW на посте и Smooth Motion всегда честные 2X.
            UpdateMultiplierChips(game);
            StatusText.Text = _forceLiveFlow
                ? "LIVE FLOW: 2X · Quality · B"
                : "Smooth Motion всегда работает в 2X.";
            return;
        }

        if (_manualEngine == EngineMode.RtxEncore)
        {
            TensorToggleResult result = _tensor.SetEncoreMultiplier(multiplier, game.Directory);
            ShowOsd(result.Title, result.Detail, OnColor);
            UpdateMultiplierChips(game);
            return;
        }

        int frames = multiplier switch { 2 => 1, 3 => 2, 4 => 3, 6 => 5, _ => 3 };

        await ExecuteAsync(() => _tensor.SetClassicMaxFrames(frames, game.Directory),
            $"ставлю {multiplier}X…");
    }

    private void EncoreMenu_Click(object sender, RoutedEventArgs e)
    {
        bool show = EncoreMenuToggle.IsChecked == true;
        _tensor.SetShowEncoreMenu(show, _selected?.Directory);

        StatusText.Text = show
            ? "Меню Encore будет появляться в игре (F8)."
            : "Внутриигровое меню Encore выключено.";
    }

    private void Eac_Click(object sender, RoutedEventArgs e)
    {
        bool allow = EacToggle.IsChecked == true;
        _tensor.SetIgnoreAntiCheatWarnings(allow);

        StatusText.Text = allow
            ? "Все моды разрешены в античит-играх — риск на вас."
            : "В античит-играх снова работает только LIVE FLOW.";

        UpdateSelectedPanel();
    }

    private void LiveFlowKey_Click(object sender, RoutedEventArgs e)
    {
        _capturingLiveFlowKey = true;
        LiveFlowKeyButton.Content = "PRESS NEW KEY // ESC CANCEL";
        LiveFlowKeyButton.Focus();
        Keyboard.Focus(LiveFlowKeyButton);
    }

    private void UpdateLiveFlowLabels()
    {
        LiveFlowModeSubText.Text = "SCREEN 2X // ONLINE";
        FooterHint.Text = $"NITRO [N] // 0.6S // TRAY // LF 2X // {_tensor.LiveFlowKeyName.ToUpperInvariant()}";
        LiveFlowKeyButton.Content = $"LIVE FLOW KEY // {_tensor.LiveFlowKeyName.ToUpperInvariant()}";
    }

    private void UpdateSourceText()
    {
        string classic = TensorAutoService.IsModSourceReady() ? "OK" : "NO";
        string encore = TensorAutoService.IsEncoreReady() ? "OK" : "NO";
        string smooth = TensorAutoService.IsSmoothMotionReady() ? "OK" : "NO";
        string live = LiveFlowBridge.IsInstalled ? "OK" : "NO";

        SourceText.Text = $"PKG // CLASSIC:{classic} // ENCORE:{encore} // SMOOTH:{smooth} // LIVE_FLOW:{live}";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadLibraryAsync();
    }

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Добавить игру — выберите рабочий .exe",
            Filter = "Исполняемые файлы (*.exe)|*.exe|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            GameEntry entry = _scanner.AddManualGame(dialog.FileName);

            GameEntry? existing = _games.FirstOrDefault(g =>
                string.Equals(g.ExecutablePath, entry.ExecutablePath, StringComparison.OrdinalIgnoreCase));

            if (existing is null) _games.Add(entry);
            else entry = existing;

            _games = _games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            SelectGame(entry);
            RenderCards();
            LibraryCountText.Text = $"{_games.Count} TARGETS // STEAM + EPIC + GOG + FOLDERS";

            ShowOsd("ИГРА ДОБАВЛЕНА", entry.Name, OnColor);
        }
        catch (Exception ex)
        {
            ShowOsd("НЕ УДАЛОСЬ ДОБАВИТЬ", ex.Message, ErrorColor);
        }
    }

    private async void RemoveMod_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _busy) return;

        GameEntry game = _selected;

        MessageBoxResult answer = MessageBox.Show(this,
            $"Удалить из папки игры ВСЕ файлы CHZM / RTX Encore / Smooth Motion?\n\n" +
            $"{game.Directory}\n\n" +
            "LIVE FLOW работает снаружи и файлы игры не трогает.",
            "CHZM Frame Unlock",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        if (_liveFlow.IsRunning &&
            string.Equals(_liveFlow.GameExecutable, game.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            StopLiveFlow();

        await ExecuteAsync(() => _tensor.RemoveEverythingFromDirectory(game.Directory),
            "чищу папку игры…");
    }

    // ===================== МЕНЮ ENCORE =====================

    private void MenuKey_Click(object sender, RoutedEventArgs e)
    {
        _capturingMenuKey = true;
        MenuKeyButton.Content = "PRESS NEW KEY // ESC CANCEL";
        MenuKeyButton.Focus();
        Keyboard.Focus(MenuKeyButton);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingMenuKey && !_capturingLiveFlowKey) return;

        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            _capturingMenuKey = false;
            _capturingLiveFlowKey = false;
            UpdateMenuKeyUi();
            UpdateLiveFlowLabels();
            StatusText.Text = "KEY BINDING // CANCELED";
            return;
        }

        if (IsModifierKey(key)) return;

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0) return;

        string displayName = KeyDisplayName(key);

        if (_capturingLiveFlowKey)
        {
            _tensor.SetLiveFlowKey(virtualKey, displayName);
            _liveFlowKey.SetKey(virtualKey);

            _capturingLiveFlowKey = false;
            UpdateLiveFlowLabels();
            StatusText.Text = $"LIVE FLOW KEY // {displayName.ToUpperInvariant()}";
            return;
        }

        _tensor.SetMenuKey(virtualKey, displayName);
        _menuHotkey.SetKey(virtualKey);

        _capturingMenuKey = false;
        UpdateMenuKeyUi();
        StatusText.Text = $"MENU KEY // {displayName.ToUpperInvariant()}";
    }

    private void UpdateMenuKeyUi()
    {
        if (!_capturingMenuKey)
            MenuKeyButton.Content = $"MENU KEY // {_tensor.MenuKeyName.ToUpperInvariant()}";
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private static string KeyDisplayName(Key key)
    {
        if (key is >= Key.F1 and <= Key.F24) return key.ToString();

        if (key is >= Key.D0 and <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return "Num" + (key - Key.NumPad0);

        return key switch
        {
            Key.OemTilde => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.Space => "Space",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PgUp",
            Key.PageDown => "PgDn",
            Key.Up => "↑",
            Key.Down => "↓",
            Key.Left => "←",
            Key.Right => "→",
            _ => key.ToString()
        };
    }

    // ===================== ОКНО / OSD =====================

    private void Refresh()
    {
        UpdateSourceText();
        UpdateSelectedPanel();
        UpdateLiveFlowLabels();
    }

    private void ShowOsd(string title, string body, Color? accent = null)
    {
        try { _osd.ShowMessage(StripEmoji(title), NormalizeOsd(StripEmoji(body)), accent); } catch { }
    }

    private static string StripEmoji(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        string cleaned = Regex.Replace(text,
            @"[\uD800-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u27BF\uFE0F\u200D]", string.Empty);

        return Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
    }

    private static string NormalizeOsd(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return body;

        string text = body.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 220 ? text : text[..220] + "…";
    }

    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        try { DragMove(); } catch { }
    }

    private void Minimize_Click(object sender, MouseButtonEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
    {
        try
        {
            MaximizeGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized
                ? "M 0,3 L 8,3 L 8,11 L 0,11 Z M 3,0 L 11,0 L 11,8 L 3,8 Z"
                : "M 1,1 L 9,1 L 9,9 L 1,9 Z");
            MaximizeGlyph.Stroke = new SolidColorBrush(WindowState == WindowState.Maximized
                ? Colors.White
                : Color.FromRgb(0x66, 0x66, 0x66));
        }
        catch
        {
            // окно ещё не создано
        }
    }

    private void Close_Click(object sender, MouseButtonEventArgs e)
    {
        ExitApplication();
    }
}
