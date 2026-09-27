using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinIsland.Core;

namespace AiOnIsland;

/// <summary>
/// 岛上的视图：同一棵可视树同时承载「小岛（紧凑态）」和「大岛（展开态）」。
///
/// 严格遵守三条硬规矩：
///   1. 所有元素一开始就常驻可视树，隐藏用 Height = 0 + Opacity = 0（不用 Visibility.Collapsed）；
///   2. 形态动画**逐帧直接赋值**，绝不用 Storyboard（动态加载的程序集里属性路径动画会在 tick 上抛
///      COMException 0x800F1001，导致"大岛变小之后卡死"）；
///   3. 配色跟着岛体主题走，写死白字在浅色主题下就是白字压白底。
/// </summary>
public sealed class AiOnIslandView : UserControl, IMorphView
{
    private const double CompactIconSize = 16;
    private const double ExpandedIconSize = 22;
    private const double CompactTitleSize = 13;
    private const double ExpandedTitleSize = 15;
    private const double ExpandedDetailHeight = 44;
    private const double BackAmplitude = 0.45;      // 与宿主内置视图同一条 BackEase 曲线

    private readonly IIslandTheme _theme;
    private readonly FontIcon _icon;
    private readonly TextBlock _title;
    private readonly TextBlock _status;
    private readonly TextBlock _hint;
    private readonly StackPanel _detail;

    /// <summary>中性色一律用共享画刷：换主题时只改 Color，所有引用处当场跟着变。</summary>
    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();

    private readonly DispatcherQueueTimer? _morphTimer;
    private DateTimeOffset _morphStart;
    private TimeSpan _morphDuration = TimeSpan.FromMilliseconds(333);
    private double _morphFrom;
    private double _morphTarget;

    /// <summary>当前形态进度：0 = 紧凑，1 = 展开。反转动画时从这里接着走。</summary>
    private double _progress;

    public AiOnIslandView(PluginManifest manifest, IIslandTheme theme)
    {
        _theme = theme;
        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        _icon = new FontIcon
        {
            Glyph = manifest.IconGlyph ?? "\uE99A",
            FontSize = CompactIconSize,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x7C, 0x5C, 0xF5)),
        };

        _title = new TextBlock
        {
            Text = manifest.Name,
            FontSize = CompactTitleSize,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = _textBrush,
        };

        _status = new TextBlock
        {
            Text = "把文字拖到岛上，或拖一个文本文件",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _mutedBrush,
        };

        _hint = new TextBlock
        {
            Text = "点一下岛体打开面板，也可以自己写需求",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _faintBrush,
        };

        _detail = new StackPanel { Height = 0, Opacity = 0, Spacing = 3 };
        _detail.Children.Add(_status);
        _detail.Children.Add(_hint);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(_icon);
        header.Children.Add(_title);

        var root = new StackPanel
        {
            Spacing = 6,
            Padding = new Thickness(18, 10, 18, 10),
            VerticalAlignment = VerticalAlignment.Center,
        };
        root.Children.Add(header);
        root.Children.Add(_detail);

        Content = root;   // 根元素保持透明：岛体材质由宿主绘制

        _morphTimer = DispatcherQueue?.CreateTimer();
        if (_morphTimer is not null)
        {
            _morphTimer.Interval = TimeSpan.FromMilliseconds(16);
            _morphTimer.IsRepeating = true;
            _morphTimer.Tick += (_, _) => OnMorphTick();
        }

        Unloaded += (_, _) => _morphTimer?.Stop();
    }

    public UIElement View => this;

    /// <summary>更新展开态里的状态行（插件在 UI 线程调用）。</summary>
    public void SetStatus(string text) => _status.Text = text;

    public void AnimateToExpanded(TimeSpan duration) => StartMorph(expanded: true, duration);

    public void AnimateToCompact(TimeSpan duration) => StartMorph(expanded: false, duration);

    private void ApplyThemeColors()
    {
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(200);
        _faintBrush.Color = Neutral(150);
    }

    /// <summary>中性色：岛体深色时白色系，浅色时黑色系（Apple 风格恒为深色）。</summary>
    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    private void StartMorph(bool expanded, TimeSpan duration)
    {
        _morphFrom = _progress;
        _morphTarget = expanded ? 1d : 0d;
        _morphDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _morphStart = DateTimeOffset.UtcNow;

        if (_morphTimer is null)
        {
            ApplyMorph(_morphTarget);   // 拿不到定时器就一帧切到终态，绝不留中间值
            return;
        }

        _morphTimer.Start();
    }

    private void OnMorphTick()
    {
        var elapsed = (DateTimeOffset.UtcNow - _morphStart).TotalMilliseconds;
        var t = Math.Clamp(elapsed / Math.Max(1, _morphDuration.TotalMilliseconds), 0, 1);

        ApplyMorph(_morphFrom + (_morphTarget - _morphFrom) * BackEaseOut(t));

        if (t >= 1)
        {
            _morphTimer?.Stop();
            ApplyMorph(_morphTarget);   // 收尾锁定终态
        }
    }

    /// <summary>BackEase(EaseOut, A) = 1 + (A+1)(t-1)³ + A(t-1)²，与宿主曲线等价。</summary>
    private static double BackEaseOut(double t)
    {
        var d = t - 1;
        return 1 + (BackAmplitude + 1) * d * d * d + BackAmplitude * d * d;
    }

    private void ApplyMorph(double progress)
    {
        _progress = progress;

        _detail.Height = Math.Max(0, ExpandedDetailHeight * progress);   // Height 不能为负
        _detail.Opacity = Math.Clamp(progress, 0, 1);

        _icon.FontSize = CompactIconSize + (ExpandedIconSize - CompactIconSize) * progress;
        _title.FontSize = CompactTitleSize + (ExpandedTitleSize - CompactTitleSize) * progress;
    }
}
