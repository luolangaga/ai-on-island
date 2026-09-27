using Microsoft.UI.Xaml;
using WinIsland.Core;

namespace AiOnIsland;

/// <summary>
/// 「岛上的 AI」——把文字或文本类文件拖到灵动岛上，AI 立刻给出可点即用的处理建议，
/// 也能自己输入任意需求。
///
/// 用到三块增量 API，所以 plugin.json 的 min_host_version 是 2.3.0：
///   · 聚光卡 OpenSpotlight（2.1.0）—— 弹出处理面板
///   · 文件投放 AddDropTarget（2.2.0）—— 接收拖入的文字 / 文本文件
///   · 主题 Context.Theme（2.3.0）—— 浅色/深色适配
/// </summary>
public sealed class AiOnIslandPlugin : IslandPluginBase
{
    private static readonly string[] TextExtensions =
    [
        ".txt", ".md", ".markdown", ".json", ".jsonc", ".csv", ".tsv", ".log", ".xml",
        ".yaml", ".yml", ".ini", ".cfg", ".conf", ".toml", ".env", ".svg",
        ".cs", ".js", ".ts", ".jsx", ".tsx", ".py", ".java", ".kt", ".go", ".rs", ".rb",
        ".php", ".c", ".cpp", ".h", ".hpp", ".swift", ".lua", ".sql", ".sh", ".ps1", ".bat",
        ".html", ".htm", ".css", ".scss", ".vue", ".tex", ".rst", ".srt", ".vtt",
    ];

    private const int MaxTextChars = 120_000;

    private readonly SemaphoreSlim _gate = new(1, 1);      // 串行化所有 AI 请求：排队，绝不丢弃
    private readonly Lock _streamLock = new();
    private readonly System.Text.StringBuilder _streamBuffer = new();

    private AiOnIslandView? _view;
    private AiIslandPanel? _panel;
    private IslandLiveContent? _content;
    private AiPayload? _lastPayload;
    private CancellationTokenSource? _cts;
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;
    private bool _panelOpen;
    private volatile bool _stopped;

    protected override Task OnInitializeAsync()
    {
        _stopped = false;
        Log.Info($"启动：{Manifest.Id} {Manifest.Version}｜宿主 {Context.HostVersion}｜插件目录 {PluginDirectory}");

        // 初始化分四步走，每步都记一行：万一以后某一版崩了，日志能直接指出卡在哪一步，
        // 不用再靠猜。（历史上这里踩过一次大坑：在插件程序集里注册 DependencyProperty
        // 会让 XAML 属性系统解析不到 owner 类型，直接原生 fail-fast 崩掉整个宿主进程。）
        try
        {
            // ① 岛上的小视图 + 聚光卡面板（两张独立可视树）
            _view = new AiOnIslandView(Manifest, Theme);
            _panel = new AiIslandPanel(Manifest, Theme);
            Log.Info("① 界面已构建（岛体视图 + 聚光卡面板）");

            _panel.SuggestionChosen += suggestion => _ = RunAsync(_lastPayload, suggestion.Prompt);
            _panel.CustomRequestSubmitted += request => _ = RunAsync(_lastPayload, request);
            _panel.ReloadSuggestionsRequested += () =>
            {
                if (_lastPayload is not null) _ = LoadSuggestionsAsync(_lastPayload);
            };
            _panel.StopRequested += CancelCurrent;

            // ② 常驻内容
            _content = new IslandLiveContent
            {
                Priority = Settings.Get("priority", 40),
                OwnerLabel = Manifest.Name,
                OwnerGlyph = Manifest.IconGlyph,
                OwnerAccent = Windows.UI.Color.FromArgb(255, 0x7C, 0x5C, 0xF5),
                MorphView = _view,
                CompactSize = new Windows.Foundation.Size(250, 40),
                ExpandedSize = new Windows.Foundation.Size(430, 130),
                OnTap = () => OpenPanel(_lastPayload),      // 点岛体 = 打开处理面板
            };
            Log.Info("② 常驻内容已声明");

            // ③ 设置页 + 投放卡片
            Context.Island.AddSettingsPage(new SettingsPageDescriptor(
                Manifest.Id, Manifest.Name, Manifest.IconGlyph ?? "\uE713", () => AiSettingsPage.Build(this), order: 120));

            RegisterDropTargets();
            Log.Info("③ 设置页与投放卡片已注册");

            // ④ 设置变更即时生效
            Context.Register(Context.OnSettingsChanged("showOnIsland", ApplyShowOnIslandSetting));
            Context.Register(Context.OnSettingsChanged("model", OnModelChanged));

            ApplyShowOnIslandSetting();
            Log.Info("④ 初始化完成，已挂到岛上");
        }
        catch (Exception ex)
        {
            // 受管异常宿主会捕获并把插件标成「错误」，这里先留一份带完整堆栈的记录便于定位
            Log.Error("初始化失败", ex);
            throw;
        }

        return Task.CompletedTask;
    }

    protected override Task OnShutdownAsync()
    {
        _stopped = true;
        Log.Info("已停用");
        CancelCurrent();
        SetContent(null);
        return Task.CompletedTask;
    }

    // ---------------- 文件投放 ----------------

    /// <summary>
    /// 注册两张投放卡片，**只收文字和文本类文件**：
    ///   · 「问 AI」收从浏览器/编辑器里选中拖来的文字（网址链接也走这里）；
    ///   · 「AI 读文件」收资源管理器里拖来的文本类文件（靠扩展名白名单过滤）。
    /// 其它载荷（图片、二进制文件、文件夹）这两张卡片都不出现 —— 是宿主按 Kinds 与扩展名
    /// 自动过滤的，插件不需要也不该在回调里再判一次。
    ///
    /// 想恢复「拖入图片让 AI 看」的话，只要两处：
    ///   1) 把下面 ask-ai 的 <c>Kinds</c> 改成 <c>IslandDropKind.Text | IslandDropKind.Image</c>；
    ///   2) 往下面 ask-ai-file 的 <c>Extensions</c> 里追加
    ///      ".png" / ".jpg" / ".jpeg" / ".gif" / ".bmp" / ".webp"。
    /// 读取与请求侧（<c>ReadPayloadAsync</c> 的图片分支、vision 消息构造）都还在，不用改。
    /// </summary>
    private void RegisterDropTargets()
    {
        var accent = Windows.UI.Color.FromArgb(255, 0x7C, 0x5C, 0xF5);

        Context.Island.AddDropTarget(new IslandDropTarget
        {
            Id = "ask-ai",
            Title = "问 AI",
            Glyph = "\uE8BD",
            Hint = "让 AI 读这段文字并给出处理建议",
            AccentColor = accent,
            Order = 100,
            Kinds = IslandDropKind.Text,
            Handler = OnDropAsync,
        });

        Context.Island.AddDropTarget(new IslandDropTarget
        {
            Id = "ask-ai-file",
            Title = "AI 读文件",
            Glyph = "\uE8A5",
            Hint = "读取文本文件，让 AI 处理",
            AccentColor = accent,
            Order = 101,
            Kinds = IslandDropKind.Files,
            Extensions = TextExtensions,
            Handler = OnDropAsync,
        });
    }

    /// <summary>投放回调：在 UI 线程执行。读文件这种慢活丢给线程池，回来再碰界面。</summary>
    private async Task<string?> OnDropAsync(IslandDropContext context)
    {
        try
        {
            var payload = await ReadPayloadAsync(context);
            if (payload is null)
            {
                return "这份内容读不出来（可能是文件夹、空文件或体积过大）";
            }

            _lastPayload = payload;

            var options = ReadOptions();
            OpenPanel(payload, options);

            if (!options.IsConfigured)
            {
                return "已打开面板，先去设置里填好 API 地址 / Key / 模型名";
            }

            if (ReadAutoSuggest())
            {
                _ = LoadSuggestionsAsync(payload);
                return $"已载入「{payload.Label}」，AI 正在想怎么处理";
            }

            // 关掉了自动建议：给一组通用动作，并**明确标注这份不是 AI 给的**
            // （不标注的话，用户会以为模型就这水平 —— 这个误解真发生过）
            var manual = Prompts.FallbackSuggestions(payload);
            Push(() => _panel?.ShowSuggestions(manual, "通用动作（未调用 AI）· 点「换一批建议」让 AI 现场生成"));
            return $"已载入「{payload.Label}」，挑一条或自己写需求";
        }
        catch (Exception ex)
        {
            Log.Error("处理投放内容出错", ex);
            return "出错了，详情见插件日志";
        }
    }

    /// <summary>把三种载荷统一成一份 AiPayload。文件读取在线程池上做，避免卡住 UI。</summary>
    private async Task<AiPayload?> ReadPayloadAsync(IslandDropContext context)
    {
        switch (context.Kind)
        {
            case IslandDropKind.Text:
            {
                var text = context.Text ?? "";
                if (text.Trim().Length == 0) return null;
                return new AiPayload { Kind = AiPayloadKind.Text, Label = "拖入的文字", Text = text };
            }

            case IslandDropKind.Image:
            {
                var bytes = context.ImageBytes;
                if (bytes is null || bytes.Length == 0) return null;
                return new AiPayload
                {
                    Kind = AiPayloadKind.Image,
                    Label = "拖入的图片",
                    ImageBytes = bytes,
                    ImageMime = SniffImageMime(bytes),
                };
            }

            default:
            {
                var paths = context.Paths;
                if (paths is null || paths.Count == 0) return null;

                var path = paths[0];
                if (Directory.Exists(path)) return null;

                var label = Path.GetFileName(path);
                var extra = Math.Max(0, paths.Count - 1);

                if (OpenAiClient.IsImageFile(path))
                {
                    var bytes = await Task.Run(() => File.ReadAllBytes(path));
                    return new AiPayload
                    {
                        Kind = AiPayloadKind.File,
                        Label = label,
                        ImageBytes = bytes,
                        ImageMime = OpenAiClient.MimeFromExtension(path),
                        ExtraFileCount = extra,
                    };
                }

                var (text, truncated) = await Task.Run(() => ReadTextFile(path));
                if (text is null) return null;

                return new AiPayload
                {
                    Kind = AiPayloadKind.File,
                    Label = label,
                    Text = text,
                    Truncated = truncated,
                    ExtraFileCount = extra,
                };
            }
        }
    }

    private static (string? Text, bool Truncated) ReadTextFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 8 * 1024 * 1024) return (null, false);   // 超过 8MB 不读

            var text = File.ReadAllText(path);
            return text.Length > MaxTextChars ? (text[..MaxTextChars], true) : (text, false);
        }
        catch (Exception)
        {
            return (null, false);
        }
    }

    /// <summary>按文件头判断图片真实格式（浏览器拖来的图没有扩展名可用）。</summary>
    private static string SniffImageMime(byte[] b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "image/gif";
        if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "image/bmp";
        if (b.Length >= 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
        return "image/png";
    }

    // ---------------- 聚光卡面板 ----------------

    private void OpenPanel(AiPayload? payload, AiOptions? options = null)
    {
        var panel = _panel;
        if (panel is null) return;

        var opt = options ?? ReadOptions();
        panel.SetModel(opt.Model);
        panel.SetConfigured(opt.IsConfigured);
        panel.SetPayload(payload);

        if (_panelOpen) return;   // 已经开着：只更新内容，不再飞一次

        _panelOpen = true;
        try
        {
            Context.Island.OpenSpotlight(new IslandSpotlight
            {
                Content = panel,
                Size = new Windows.Foundation.Size(700, 520),
                OnClosed = () =>
                {
                    _panelOpen = false;
                    panel.OnHostClosed();
                },
            });
        }
        catch (Exception ex)
        {
            _panelOpen = false;
            Log.Error("打开聚光卡失败", ex);
        }
    }

    // ---------------- AI 调用 ----------------

    /// <summary>读一遍内容，让模型给出 4~6 条可点即用的建议。</summary>
    private async Task LoadSuggestionsAsync(AiPayload payload)
    {
        if (_stopped) return;

        var options = ReadOptions();
        if (!options.IsConfigured)
        {
            Push(() => _panel?.SetConfigured(false));
            return;
        }

        var ct = BeginOperation();
        Push(() => _panel?.ShowSuggestionsLoading());

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var messages = Prompts.BuildSuggestMessages(payload);
                Log.Info($"开始生成建议（{payload.Summary}）");

                var raw = await OpenAiClient.AskOnceAsync(options, messages, ct).ConfigureAwait(false);
                var suggestions = Prompts.ParseSuggestions(raw);

                string? caption = null;
                if (suggestions.Count == 0)
                {
                    Log.Warn($"建议解析失败，改用兜底建议。原始返回前 200 字：{Head(raw, 200)}");
                    suggestions = Prompts.FallbackSuggestions(payload);
                    caption = "AI 没按格式回，已换成通用动作 · 点「换一批建议」重试";
                }

                Push(() => _panel?.ShowSuggestions(suggestions, caption));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("生成建议已取消");
        }
        catch (Exception ex)
        {
            Log.Error("生成建议失败，改用兜底建议", ex);
            var fallback = Prompts.FallbackSuggestions(payload);
            Push(() => _panel?.ShowSuggestions(fallback, "AI 调用失败，已换成通用动作 · 点「换一批建议」重试"));
        }
    }

    /// <summary>按用户选的那条要求（或自定义输入）执行一次，结果流式回显。</summary>
    private async Task RunAsync(AiPayload? payload, string request)
    {
        if (_stopped || string.IsNullOrWhiteSpace(request)) return;

        var panel = _panel;
        var options = ReadOptions();

        if (!options.IsConfigured)
        {
            Push(() => panel?.SetConfigured(false));
            return;
        }

        var ct = BeginOperation();
        var started = DateTimeOffset.UtcNow;

        lock (_streamLock) _streamBuffer.Clear();
        _lastFlush = DateTimeOffset.MinValue;

        Push(() => panel?.BeginRun(request));
        Log.Info($"开始执行：{request}");

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var content = payload ?? new AiPayload { Kind = AiPayloadKind.Text, Label = "纯提问", Text = "" };
                var messages = Prompts.BuildExecuteMessages(options, content, request);

                var full = await OpenAiClient.ChatAsync(options, messages, OnDelta, ct).ConfigureAwait(false);
                FlushStream(full);

                var elapsed = DateTimeOffset.UtcNow - started;
                Push(() => panel?.EndRun(true, null, elapsed));
                Log.Info($"执行完成，用时 {elapsed.TotalSeconds:0.0}s，{full.Length} 字");
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            var elapsed = DateTimeOffset.UtcNow - started;
            Push(() => panel?.EndRun(false, null, elapsed, stopped: true));
            Log.Info("执行已取消");
        }
        catch (Exception ex)
        {
            var elapsed = DateTimeOffset.UtcNow - started;
            Log.Error("执行失败", ex);
            Push(() => panel?.EndRun(false, ex.Message, elapsed));
        }
    }

    /// <summary>流式增量：后台线程回调。攒够 ~60ms 才推一次 UI，避免刷爆调度队列。</summary>
    private void OnDelta(string delta)
    {
        lock (_streamLock) _streamBuffer.Append(delta);

        var now = DateTimeOffset.UtcNow;
        if ((now - _lastFlush).TotalMilliseconds < 60) return;
        _lastFlush = now;

        FlushStream(null);
    }

    /// <summary>把当前缓冲推给界面（finalText 非空时以它为准，避免最后一段丢失）。</summary>
    private void FlushStream(string? finalText)
    {
        string snapshot;
        lock (_streamLock)
        {
            snapshot = finalText ?? _streamBuffer.ToString();
            if (finalText is not null) _streamBuffer.Clear();
        }

        Push(() => _panel?.UpdateStream(snapshot));
    }

    /// <summary>取消当前请求：换一个 CancellationTokenSource，旧的自然失效。</summary>
    private CancellationToken BeginOperation()
    {
        CancelCurrent();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    private void CancelCurrent()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (Exception)
        {
            // 取消失败无所谓
        }
    }

    /// <summary>编组回 UI 线程；插件停用后直接丢弃，避免"已忽略调用"的噪音。</summary>
    private void Push(Action action)
    {
        if (_stopped) return;

        try
        {
            Context.RunOnUI(() =>
            {
                if (_stopped) return;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Warn($"界面更新失败（已忽略）：{ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"无法编组到 UI 线程（已忽略）：{ex.Message}");
        }
    }

    // ---------------- 设置 ----------------

    /// <summary>每次现读，不要缓存 —— 这样设置一变，下一次动作立刻用新值。</summary>
    internal AiOptions ReadOptions()
    {
        var baseUrl = Settings.Get("baseUrl", AiOptions.DefaultBaseUrl) ?? AiOptions.DefaultBaseUrl;

        // 用户还没选过字段名时按 baseUrl 猜一个 —— 这样 DeepSeek 用户开箱就能"真关掉"思考，
        // 而 OpenAI 官方用户会被猜成 none（不发字段），不会因为多了个陌生字段被 400。
        var style = Settings.Get("thinkingStyle", "") ?? "";
        if (style.Length == 0) style = AiOptions.DetectThinkingStyle(baseUrl);

        return new AiOptions
        {
            BaseUrl = baseUrl,
            ApiKey = Settings.Get("apiKey", "") ?? "",
            Model = Settings.Get("model", AiOptions.DefaultModel) ?? AiOptions.DefaultModel,
            SystemPrompt = Settings.Get("systemPrompt", "") ?? "",
            Temperature = Settings.Get("temperature", 0.7),
            MaxTokens = Settings.Get("maxTokens", 0),
            ThinkingOn = Settings.Get("thinking", false),
            ThinkingStyle = style,
        };
    }

    internal void ApplyOptions(AiOptions options)
    {
        if (!Same("baseUrl", options.BaseUrl)) Settings.Set("baseUrl", options.BaseUrl);
        if (!Same("apiKey", options.ApiKey)) Settings.Set("apiKey", options.ApiKey);
        if (!Same("model", options.Model)) Settings.Set("model", options.Model);
        if (!Same("systemPrompt", options.SystemPrompt)) Settings.Set("systemPrompt", options.SystemPrompt);
        if (!Same("thinkingStyle", options.ThinkingStyle)) Settings.Set("thinkingStyle", options.ThinkingStyle);

        if (Settings.Get("thinking", false) != options.ThinkingOn) Settings.Set("thinking", options.ThinkingOn);

        if (Math.Abs(Settings.Get("temperature", 0.7) - options.Temperature) > 0.001)
        {
            Settings.Set("temperature", options.Temperature);
        }

        if (Settings.Get("maxTokens", 0) != options.MaxTokens) Settings.Set("maxTokens", options.MaxTokens);

        bool Same(string key, string value) =>
            string.Equals(Settings.Get(key, "") ?? "", value, StringComparison.Ordinal);
    }

    internal bool ReadShowOnIsland() => Settings.Get("showOnIsland", true);

    internal bool ReadAutoSuggest() => Settings.Get("autoSuggest", true);

    internal void ApplyShowOnIsland(bool value)
    {
        if (ReadShowOnIsland() == value) return;
        Settings.Set("showOnIsland", value);
    }

    internal void ApplyAutoSuggest(bool value)
    {
        if (ReadAutoSuggest() == value) return;
        Settings.Set("autoSuggest", value);
    }

    /// <summary>设置页「测试连接」用：发一句最短的话，确认地址 / Key / 模型都对。</summary>
    internal Task<string> TestConnectionAsync()
    {
        var options = ReadOptions();
        if (!options.IsConfigured) throw new AiRequestException("API 地址、API Key、模型名三项都要填。");

        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Text = "只回复两个字：可用" },
        };

        return OpenAiClient.AskOnceAsync(options, messages, CancellationToken.None);
    }

    private void ApplyShowOnIslandSetting()
    {
        var visible = Settings.Get("showOnIsland", true);
        SetContent(visible ? _content : null);
    }

    private void OnModelChanged()
    {
        var model = ReadOptions().Model;
        Push(() => _panel?.SetModel(model));
    }

    private static string Head(string? text, int max)
    {
        var t = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return t.Length <= max ? t : t[..max];
    }
}
