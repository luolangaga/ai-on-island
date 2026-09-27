using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;
using WinIsland.Core;

namespace AiOnIsland;

/// <summary>
/// 「超级展开」聚光卡：拖进来东西（或点岛体）后弹出的那张大卡片。
///
/// 它是**独立于岛视图的另一棵可视树**（每个窗口一棵树，不能复用岛视图那个 UIElement）。
/// 飞入飞回、遮罩、圆角、层级全由宿主负责，这里只管"长什么样"和"用户点了什么"。
/// 所有耗时操作都通过事件抛给插件去做，本类不碰网络。
///
/// 交互是**三步走**的，同一时刻只显示一步（不是把所有东西平铺在一屏）：
///   ① 选做法 Choose —— 只显示"来源一行 + AI 建议卡片 + 自己写需求入口"
///   ② 写需求 Compose —— 整屏只干一件事：写要求
///   ③ 看结果 Result  —— 只显示结果与操作
/// 步骤切换用逐帧赋值做过场（绝不用 Storyboard，见下方硬规矩）。
///
/// 【硬规矩，别再踩】插件程序集是动态加载的，不在宿主的 XamlTypeInfo 里：
///   · 不要在本程序集里注册 DependencyProperty（会让 XAML 属性系统解析 owner 类型时
///     原生 fail-fast：0xc000027b + Microsoft.UI.Xaml.dll + E_POINTER，宿主拦不住，整进程崩）；
///   · 要换行布局就用 ScrollViewer + 竖向 StackPanel（宿主 Settings/WrapPanel.cs 也只用 CLR 属性）；
///   · 形态动画逐帧直接赋值，不要 Storyboard。
/// </summary>
public sealed class AiIslandPanel : UserControl
{
    // ---- 对外事件（插件订阅，负责真正的干活）----
    public event Action<AiSuggestion>? SuggestionChosen;
    public event Action<string>? CustomRequestSubmitted;
    public event Action? ReloadSuggestionsRequested;
    public event Action? StopRequested;

    private enum Step { Choose, Compose, Result }

    private Step _step = Step.Choose;
    private bool _running;
    private bool _previewOpen;

    // ---- 三个步骤各自的根容器（同一时刻只有一个可见）----
    private Grid _chooseStep = null!;
    private Grid _composeStep = null!;
    private Grid _resultStep = null!;
    private readonly TranslateTransform _chooseSlide = new();
    private readonly TranslateTransform _composeSlide = new();
    private readonly TranslateTransform _resultSlide = new();

    // ---- ① 选做法 ----
    private TextBlock _modelText = null!;
    private ProgressRing _busy = null!;
    private Grid _chooseBody = null!;
    private StackPanel _setupPanel = null!;
    private SourceRow _chooseSource = null!;
    private TextBlock _suggestLabel = null!;
    private Button _reloadButton = null!;
    private StackPanel _suggestHost = null!;
    private ScrollViewer _suggestScroll = null!;

    // ---- ② 写需求 ----
    private SourceRow _composeSource = null!;
    private TextBox _input = null!;
    private Button _sendButton = null!;

    // ---- ③ 看结果 ----
    private TextBlock _runTitle = null!;
    private TextBlock _statusText = null!;
    private TextBlock _resultText = null!;
    private ScrollViewer _resultScroll = null!;
    private Button _resultBack = null!;
    private Button _stopButton = null!;

    // ---- 共享画刷：换主题只改 Color，所有引用处当场跟着变 ----
    private readonly IIslandTheme _theme;
    private readonly SolidColorBrush _textBrush = new();
    private readonly SolidColorBrush _mutedBrush = new();
    private readonly SolidColorBrush _faintBrush = new();
    private readonly SolidColorBrush _panelFillBrush = new();
    private readonly SolidColorBrush _panelStrokeBrush = new();
    private readonly SolidColorBrush _accentBrush = new();
    private readonly SolidColorBrush _onAccentBrush = new();

    // ---- 步骤过场：逐帧直接赋值 ----
    private const double TransitionMs = 180;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _transitionTimer;
    private FrameworkElement? _animHost;
    private TranslateTransform? _animSlide;
    private DateTimeOffset _animStart;
    private double _animFromY;

    public AiIslandPanel(PluginManifest manifest, IIslandTheme theme)
    {
        _theme = theme;
        ApplyThemeColors();
        _theme.Changed += ApplyThemeColors;

        _accentBrush.Color = Windows.UI.Color.FromArgb(255, 0x7C, 0x5C, 0xF5);
        _onAccentBrush.Color = Windows.UI.Color.FromArgb(255, 255, 255, 255);

        _chooseStep = BuildChooseStep(manifest);
        _composeStep = BuildComposeStep();
        _resultStep = BuildResultStep();

        var host = new Grid();
        host.Children.Add(_chooseStep);
        host.Children.Add(_composeStep);
        host.Children.Add(_resultStep);
        Content = host;

        _transitionTimer = DispatcherQueue?.CreateTimer();
        if (_transitionTimer is not null)
        {
            _transitionTimer.Interval = TimeSpan.FromMilliseconds(16);
            _transitionTimer.IsRepeating = true;
            _transitionTimer.Tick += (_, _) => OnTransitionTick();
        }

        Unloaded += (_, _) => _transitionTimer?.Stop();

        ShowStep(Step.Choose, forward: true, animate: false);
    }

    // ================= ① 选做法 =================

    private Grid BuildChooseStep(PluginManifest manifest)
    {
        var name = new TextBlock
        {
            Text = manifest.Name,
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _modelText = new TextBlock
        {
            FontSize = 12,
            Foreground = _faintBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        brand.Children.Add(name);
        brand.Children.Add(_modelText);

        _busy = new ProgressRing
        {
            IsActive = false,
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(brand, 0);
        Grid.SetColumn(_busy, 1);
        header.Children.Add(brand);
        header.Children.Add(_busy);

        _chooseSource = BuildSourceRow();

        // 建议标题 + 换一批
        _suggestLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = _faintBrush,
            Text = "挑一个做法",
            VerticalAlignment = VerticalAlignment.Center,
        };

        _reloadButton = MakeLinkButton("换一批");
        _reloadButton.Visibility = Visibility.Collapsed;
        _reloadButton.Click += (_, _) => ReloadSuggestionsRequested?.Invoke();

        var caption = new Grid();
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_suggestLabel, 0);
        Grid.SetColumn(_reloadButton, 1);
        caption.Children.Add(_suggestLabel);
        caption.Children.Add(_reloadButton);

        _suggestHost = new StackPanel { Spacing = 6 };

        _suggestScroll = new ScrollViewer
        {
            Content = _suggestHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 6, 0),
        };

        var composeEntry = BuildComposeEntry();

        _chooseBody = new Grid { RowSpacing = 12 };
        _chooseBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chooseBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chooseBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _chooseBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddRow(_chooseBody, 0, _chooseSource.Root);
        AddRow(_chooseBody, 1, caption);
        AddRow(_chooseBody, 2, _suggestScroll);
        AddRow(_chooseBody, 3, composeEntry);

        _setupPanel = BuildSetupPanel();

        var root = MakeStepGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        AddRow(root, 0, header);
        AddRow(root, 1, _chooseBody);
        AddRow(root, 1, _setupPanel);
        root.RenderTransform = _chooseSlide;

        return root;
    }

    /// <summary>未配置接口时，整一步退化成一段引导（不是盖在建议上的一层浮字）。</summary>
    private StackPanel BuildSetupPanel()
    {
        var title = new TextBlock
        {
            Text = "还没有连接 AI",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
        };

        var body = new TextBlock
        {
            Text = "打开 WinIsland 设置 → 插件 → 「岛上的 AI」，填好 API 地址、API Key 和模型名就能用了。\n\n" +
                   "支持任何 OpenAI 兼容接口：OpenAI、DeepSeek、本地 Ollama / LM Studio 都行。",
            FontSize = 13,
            Foreground = _mutedBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
        };

        var panel = new StackPanel
        {
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(title);
        panel.Children.Add(body);
        return panel;
    }

    // ================= ② 写需求 =================

    private Grid BuildComposeStep()
    {
        var title = new TextBlock
        {
            Text = "想让 AI 做什么？",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bar.Children.Add(MakeBackButton("返回", () => ShowStep(Step.Choose, forward: false)));
        bar.Children.Add(title);

        _composeSource = BuildSourceRow();

        _input = new TextBox
        {
            PlaceholderText = "例如：改写成 150 字以内的正式邮件 / 提取所有金额并求和 / 转成 JSON，字段 title、author、date…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 14,
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1),
            MinHeight = 120,
        };

        // Ctrl+Enter 发送（单按 Enter 就是换行）。万一这个 API 在动态程序集里不可用，
        // 也只是少个快捷键，绝不能因此让插件初始化失败。
        try
        {
            var accelerator = new KeyboardAccelerator
            {
                Key = VirtualKey.Enter,
                Modifiers = VirtualKeyModifiers.Control,
            };
            accelerator.Invoked += (_, e) =>
            {
                e.Handled = true;
                SubmitCustom();
            };
            _input.KeyboardAccelerators.Add(accelerator);
        }
        catch (Exception)
        {
            // 没有快捷键也能点按钮发送
        }

        var hint = new TextBlock
        {
            Text = "Ctrl + Enter 发送 · 内容已经带上，直接说你想怎么改",
            FontSize = 11.5,
            Foreground = _faintBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _sendButton = new Button
        {
            Content = "开始处理",
            MinWidth = 110,
            Height = 38,
            CornerRadius = new CornerRadius(8),
            Background = _accentBrush,
            Foreground = _onAccentBrush,
            BorderThickness = new Thickness(0),
        };
        _sendButton.Resources["ButtonBackgroundPointerOver"] = _accentBrush;
        _sendButton.Resources["ButtonBackgroundPressed"] = _accentBrush;
        _sendButton.Resources["ButtonForegroundPointerOver"] = _onAccentBrush;
        _sendButton.Resources["ButtonForegroundPressed"] = _onAccentBrush;
        _sendButton.Click += (_, _) => SubmitCustom();

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(hint, 0);
        Grid.SetColumn(_sendButton, 1);
        footer.Children.Add(hint);
        footer.Children.Add(_sendButton);

        var root = MakeStepGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddRow(root, 0, bar);
        AddRow(root, 1, _composeSource.Root);
        AddRow(root, 2, _input);
        AddRow(root, 3, footer);
        root.RenderTransform = _composeSlide;

        return root;
    }

    // ================= ③ 看结果 =================

    private Grid BuildResultStep()
    {
        _resultBack = MakeBackButton("换个做法", () => ShowStep(Step.Choose, forward: false));

        _runTitle = new TextBlock
        {
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bar.Children.Add(_resultBack);
        bar.Children.Add(_runTitle);

        _resultText = new TextBlock
        {
            FontSize = 14,
            Foreground = _textBrush,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            LineHeight = 22,
            Padding = new Thickness(0, 2, 0, 2),
        };

        _resultScroll = new ScrollViewer
        {
            Content = _resultText,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 6, 0),
        };

        var resultBox = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(10),
            Background = _panelFillBrush,
            BorderBrush = _panelStrokeBrush,
            BorderThickness = new Thickness(1),
            Child = _resultScroll,
        };

        _statusText = new TextBlock
        {
            FontSize = 12,
            Foreground = _faintBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _stopButton = MakeFlatButton("停止");
        _stopButton.Visibility = Visibility.Collapsed;
        _stopButton.Click += (_, _) => StopRequested?.Invoke();

        var copyButton = MakeFlatButton("复制结果");
        copyButton.Click += (_, _) => CopyResult();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(_stopButton);
        actions.Children.Add(copyButton);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_statusText, 0);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(_statusText);
        footer.Children.Add(actions);

        var root = MakeStepGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddRow(root, 0, bar);
        AddRow(root, 1, resultBox);
        AddRow(root, 2, footer);
        root.RenderTransform = _resultSlide;

        return root;
    }

    // ================= 公开接口 =================

    /// <summary>把当前模型名显示在标题旁（不显示 Key，避免泄露）。</summary>
    public void SetModel(string? model)
        => _modelText.Text = string.IsNullOrWhiteSpace(model) ? "" : "· " + model.Trim();

    /// <summary>接口是否配置完整；不完整时第①步整屏换成一段引导文字。</summary>
    public void SetConfigured(bool configured)
    {
        _chooseBody.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        _setupPanel.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;

        if (!configured && _step != Step.Choose) ShowStep(Step.Choose, forward: false, animate: false);
    }

    /// <summary>换一份内容：回到第①步，清空上一次的结果与建议，重建来源行。</summary>
    public void SetPayload(AiPayload? payload)
    {
        _resultText.Text = "";
        _runTitle.Text = "";
        _statusText.Text = "";
        SetRunning(false);
        ShowSuggestions([]);
        ApplySource(payload);
        SetPreviewOpen(false);
        ShowStep(Step.Choose, forward: false, animate: false);
    }

    /// <summary>显示"正在想建议"。</summary>
    public void ShowSuggestionsLoading()
    {
        _busy.IsActive = true;
        _suggestLabel.Text = "AI 正在读这份内容，想几条做法…";
        _reloadButton.Visibility = Visibility.Collapsed;
        _suggestScroll.Visibility = Visibility.Visible;

        _suggestHost.Children.Clear();
        _suggestHost.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
    }

    /// <summary>
    /// 填充建议行。这些建议是「谁给的」必须让用户一眼看清 ——
    /// 只有 caption 为空的默认路径才是真·模型生成；兜底/离线列表要显式写出来，
    /// 免得用户以为 AI 给的就这么敷衍（真踩过这个误解）。
    /// </summary>
    public void ShowSuggestions(IReadOnlyList<AiSuggestion> suggestions, string? caption = null)
    {
        _busy.IsActive = false;
        _suggestHost.Children.Clear();

        if (suggestions.Count == 0)
        {
            _suggestLabel.Text = caption ?? "挑一个做法";
            _reloadButton.Visibility = Visibility.Collapsed;
            _suggestScroll.Visibility = Visibility.Collapsed;
            return;
        }

        _suggestLabel.Text = caption ?? "挑一个做法 · 点一下直接执行";
        _reloadButton.Visibility = Visibility.Visible;
        _suggestScroll.Visibility = Visibility.Visible;

        foreach (var item in suggestions)
        {
            _suggestHost.Children.Add(MakeSuggestionRow(item));
        }
    }

    /// <summary>开始一次执行：切到第③步，把结果区清空并进入 busy 状态。</summary>
    public void BeginRun(string request)
    {
        _busy.IsActive = false;
        _runTitle.Text = request;
        ToolTipService.SetToolTip(_runTitle, request);

        _resultText.Text = "";
        _statusText.Text = "AI 正在写…";
        SetRunning(true);
        ShowStep(Step.Result, forward: true);
        _resultScroll.ChangeView(null, 0, null, true);
    }

    /// <summary>流式增量刷新（插件已编组回 UI 线程）。</summary>
    public void UpdateStream(string fullText)
    {
        _resultText.Text = fullText;
        _resultScroll.ChangeView(null, _resultScroll.ScrollableHeight, null, false);
    }

    /// <summary>一次执行结束（成功、失败或用户主动停止）。</summary>
    public void EndRun(bool ok, string? error, TimeSpan elapsed, bool stopped = false)
    {
        SetRunning(false);

        if (stopped)
        {
            _statusText.Text = $"已停止 · 用时 {elapsed.TotalSeconds:0.0}s";
            return;
        }

        if (ok)
        {
            _statusText.Text = $"完成 · 用时 {elapsed.TotalSeconds:0.0}s · 可选中复制";
            return;
        }

        _statusText.Text = "出错了";
        _resultText.Text = error ?? "未知错误";
    }

    /// <summary>宿主收起卡片时的收尾（取消在跑的请求由插件负责）。</summary>
    public void OnHostClosed()
    {
        if (_running) StopRequested?.Invoke();
    }

    // ================= 步骤切换 =================

    private void ShowStep(Step step, bool forward, bool animate = true)
    {
        _step = step;

        _chooseStep.Visibility = step == Step.Choose ? Visibility.Visible : Visibility.Collapsed;
        _composeStep.Visibility = step == Step.Compose ? Visibility.Visible : Visibility.Collapsed;
        _resultStep.Visibility = step == Step.Result ? Visibility.Visible : Visibility.Collapsed;

        var host = step switch
        {
            Step.Compose => (FrameworkElement)_composeStep,
            Step.Result => _resultStep,
            _ => _chooseStep,
        };

        var slide = step switch
        {
            Step.Compose => _composeSlide,
            Step.Result => _resultSlide,
            _ => _chooseSlide,
        };

        // 拿不到定时器就一帧切到终态，绝不留中间值
        if (!animate || _transitionTimer is null)
        {
            host.Opacity = 1;
            slide.Y = 0;
            FocusForStep(step);
            return;
        }

        _animHost = host;
        _animSlide = slide;
        _animFromY = forward ? 16 : -16;
        slide.Y = _animFromY;
        host.Opacity = 0;
        _animStart = DateTimeOffset.UtcNow;
        _transitionTimer.Start();

        FocusForStep(step);
    }

    private void FocusForStep(Step step)
    {
        if (step == Step.Compose)
        {
            _ = _input.Focus(FocusState.Programmatic);
        }
        else if (step == Step.Result)
        {
            // 结果区要能直接 Ctrl+A / 拖动选中，先把焦点从输入框挪开
            _ = _resultText.Focus(FocusState.Programmatic);
        }
    }

    private void OnTransitionTick()
    {
        var t = Math.Clamp(
            (DateTimeOffset.UtcNow - _animStart).TotalMilliseconds / TransitionMs, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);            // CubicEaseOut

        if (_animHost is not null) _animHost.Opacity = eased;
        if (_animSlide is not null) _animSlide.Y = _animFromY * (1 - eased);

        if (t < 1) return;

        _transitionTimer?.Stop();
        if (_animHost is not null) _animHost.Opacity = 1;
        if (_animSlide is not null) _animSlide.Y = 0;
        _animHost = null;
    }

    // ================= 内部 =================

    private void SubmitCustom()
    {
        var text = (_input.Text ?? "").Trim();
        if (text.Length == 0) return;

        _input.Text = "";
        CustomRequestSubmitted?.Invoke(text);
    }

    private void CopyResult()
    {
        var text = _resultText.Text ?? "";
        if (text.Length == 0) return;

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            _statusText.Text = "已复制到剪贴板";
        }
        catch (Exception)
        {
            _statusText.Text = "复制失败，可以手动选中文本";
        }
    }

    private void SetRunning(bool running)
    {
        _running = running;
        _stopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _resultBack.IsEnabled = !running;      // 跑着的时候只留「停止」这条路出去
        _sendButton.IsEnabled = !running;
    }

    /// <summary>把 payload 写进两个来源行（第①步 / 第②步各一份）。</summary>
    private void ApplySource(AiPayload? payload)
    {
        var hasPayload = payload is not null;

        string label;
        string meta;
        if (payload is null)
        {
            label = "还没有内容";
            meta = "把一段文字拖到岛上，或直接点「自己写需求」";
        }
        else
        {
            label = payload.Kind switch
            {
                AiPayloadKind.Image => "拖入的图片",
                AiPayloadKind.File => payload.Label,
                _ => "拖入的文字",
            };

            // Summary 里已经带了"文字 · N 字"这类前缀，这里只取后半段，避免和 label 重复
            meta = payload.Kind == AiPayloadKind.Text
                ? $"{payload.Text?.Length ?? 0} 字{(payload.Truncated ? "（已截断）" : "")}"
                : payload.Summary;

            if (payload.ExtraFileCount > 0) meta += $"（同批还有 {payload.ExtraFileCount} 个未处理）";
        }

        var canPreview = payload is not null &&
                         (payload.Text is { Length: > 0 } || payload.ImageBytes is { Length: > 0 });

        foreach (var row in new[] { _chooseSource, _composeSource })
        {
            row.Label.Text = label;
            row.Meta.Text = meta;
            row.Meta.Visibility = Visibility.Visible;
            row.Toggle.Visibility = canPreview ? Visibility.Visible : Visibility.Collapsed;

            if (payload?.ImageBytes is { Length: > 0 } bytes)
            {
                row.PreviewText.Visibility = Visibility.Collapsed;
                row.PreviewText.Text = "";
                row.PreviewImage.Visibility = Visibility.Visible;
                _ = LoadThumbnailAsync(row.PreviewImage, bytes);
            }
            else
            {
                row.PreviewImage.Visibility = Visibility.Collapsed;
                row.PreviewImage.Source = null;
                row.PreviewText.Visibility = Visibility.Visible;
                row.PreviewText.Text = payload?.Preview(400) ?? "";
            }
        }
    }

    private void SetPreviewOpen(bool open)
    {
        _previewOpen = open;

        foreach (var row in new[] { _chooseSource, _composeSource })
        {
            row.Preview.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            row.Toggle.Content = open ? "收起" : "看全文";
        }
    }

    private void TogglePreview() => SetPreviewOpen(!_previewOpen);

    /// <summary>
    /// 一行来源：左边一条强调色小竖条 + 名字 + 灰色副信息，右边一个"看全文"。
    /// 默认收起 —— 卡片一打开应该是"能做什么"，不是一整屏原文。
    /// </summary>
    private SourceRow BuildSourceRow()
    {
        var row = new SourceRow();

        var bar = new Border
        {
            Width = 3,
            Height = 14,
            CornerRadius = new CornerRadius(2),
            Background = _accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Label = new TextBlock
        {
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        row.Meta = new TextBlock
        {
            FontSize = 11.5,
            Foreground = _faintBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        left.Children.Add(bar);
        left.Children.Add(row.Label);
        left.Children.Add(row.Meta);

        row.Toggle = MakeLinkButton("看全文");
        row.Toggle.Click += (_, _) => TogglePreview();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(row.Toggle, 1);
        head.Children.Add(left);
        head.Children.Add(row.Toggle);

        row.PreviewText = new TextBlock
        {
            FontSize = 12,
            Foreground = _mutedBrush,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        row.PreviewImage = new Image
        {
            Stretch = Stretch.Uniform,
            MaxHeight = 150,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };

        row.Preview = new StackPanel
        {
            Spacing = 6,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(11, 8, 0, 0),
        };
        row.Preview.Children.Add(row.PreviewImage);
        row.Preview.Children.Add(row.PreviewText);

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(row.Preview);

        row.Root = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(10),
            Background = _panelFillBrush,
            BorderBrush = _panelStrokeBrush,
            BorderThickness = new Thickness(1),
            Child = body,
        };

        return row;
    }

    /// <summary>「自己写需求」入口：第①步底部的整行次要按钮，点了才进第②步。</summary>
    private Button BuildComposeEntry()
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new FontIcon { Glyph = "\uE70F", FontSize = 13, Foreground = _mutedBrush });
        content.Children.Add(new TextBlock
        {
            Text = "自己写需求",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = "比如「改成周报」「提取金额求和」",
            FontSize = 11.5,
            Foreground = _faintBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(10),
            Background = _panelFillBrush,
            BorderBrush = _panelStrokeBrush,
            BorderThickness = new Thickness(1),
        };
        NeutralizeHover(button);
        button.Click += (_, _) => ShowStep(Step.Compose, forward: true);
        return button;
    }

    /// <summary>
    /// 一条建议 = 一整行可点卡片：左边 emoji，中间标题（一行）+ 完整指令（灰字一行，鼠标悬停看全文），
    /// 右边一个 ›。只用 Button / Grid / TextBlock 这些框架类型，不引入任何自定义控件。
    /// </summary>
    private Button MakeSuggestionRow(AiSuggestion suggestion)
    {
        var icon = new TextBlock
        {
            Text = suggestion.Icon,
            FontSize = 15,
            Width = 26,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var title = new TextBlock
        {
            Text = suggestion.Title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = _textBrush,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var detail = new TextBlock
        {
            Text = suggestion.Prompt,
            FontSize = 11.5,
            Foreground = _faintBrush,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var textColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textColumn.Children.Add(title);
        textColumn.Children.Add(detail);

        var chevron = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 11,
            Foreground = _faintBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(textColumn, 1);
        Grid.SetColumn(chevron, 2);
        row.Children.Add(icon);
        row.Children.Add(textColumn);
        row.Children.Add(chevron);

        var card = new Button
        {
            Content = row,
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(10),
            Background = _panelFillBrush,
            BorderBrush = _panelStrokeBrush,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Tag = suggestion,
        };
        NeutralizeHover(card);

        card.Click += (_, _) => SuggestionChosen?.Invoke(suggestion);
        ToolTipService.SetToolTip(card, suggestion.Prompt);

        return card;
    }

    private Button MakeBackButton(string text, Action onClick)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new FontIcon { Glyph = "\uE72B", FontSize = 12, Foreground = _mutedBrush });
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            Foreground = _mutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var button = new Button
        {
            Content = content,
            Padding = new Thickness(9, 5, 12, 5),
            CornerRadius = new CornerRadius(8),
            Background = _panelFillBrush,
            BorderBrush = _panelStrokeBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        NeutralizeHover(button);
        button.Click += (_, _) => onClick();
        return button;
    }

    private Button MakeLinkButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Foreground = _mutedBrush,
            BorderThickness = new Thickness(0),
            FontSize = 12,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };

        button.Resources["ButtonBackgroundPointerOver"] = _panelStrokeBrush;
        button.Resources["ButtonBackgroundPressed"] = _panelStrokeBrush;
        button.Resources["ButtonForegroundPointerOver"] = _textBrush;
        button.Resources["ButtonForegroundPressed"] = _textBrush;

        return button;
    }

    private static Button MakeFlatButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(10, 4, 10, 4),
        CornerRadius = new CornerRadius(7),
        FontSize = 12,
        MinWidth = 0,
    };

    /// <summary>覆盖默认的悬停/按下底色，免得在浅色卡片上闪成系统蓝。</summary>
    private void NeutralizeHover(Button button)
    {
        button.Resources["ButtonBackgroundPointerOver"] = _panelStrokeBrush;
        button.Resources["ButtonBackgroundPressed"] = _panelStrokeBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = _faintBrush;
        button.Resources["ButtonBorderBrushPressed"] = _faintBrush;
    }

    private async Task LoadThumbnailAsync(Image target, byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            target.Source = bitmap;
        }
        catch (Exception)
        {
            // 缩略图只是好看用的：失败就退化成一行文字，绝不能影响功能
            target.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyThemeColors()
    {
        _textBrush.Color = Neutral(255);
        _mutedBrush.Color = Neutral(205);
        _faintBrush.Color = Neutral(145);
        _panelFillBrush.Color = Neutral(16);
        _panelStrokeBrush.Color = Neutral(34);
    }

    /// <summary>中性色：岛体深色时白色系，浅色时黑色系。</summary>
    private Windows.UI.Color Neutral(byte alpha) => _theme.IsLight
        ? Windows.UI.Color.FromArgb(alpha, 0, 0, 0)
        : Windows.UI.Color.FromArgb(alpha, 255, 255, 255);

    private static Grid MakeStepGrid() => new()
    {
        Padding = new Thickness(28, 22, 28, 20),
        RowSpacing = 12,
    };

    private static void AddRow(Grid grid, int row, FrameworkElement child)
    {
        Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    /// <summary>一行来源所需的几个元素（第①步与第②步各一份实例）。</summary>
    private sealed class SourceRow
    {
        public Border Root = null!;
        public TextBlock Label = null!;
        public TextBlock Meta = null!;
        public Button Toggle = null!;
        public StackPanel Preview = null!;
        public TextBlock PreviewText = null!;
        public Image PreviewImage = null!;
    }
}
