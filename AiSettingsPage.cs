using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace AiOnIsland;

/// <summary>
/// 设置页：API 地址 / API Key / 模型名 + 可选参数。
///
/// 两个容易踩的坑都在这里处理掉了（见 sdk-api.md §15）：
///   · 输入框的值要在**动作发生前**提交 —— 按钮点击早于 LostFocus，只挂 LostFocus 会拿旧值去干活；
///   · 值没变就别写设置，否则每次失焦都白触发一次刷新。
/// </summary>
public static class AiSettingsPage
{
    /// <summary>每次进设置页都会调一次，必须返回全新实例。</summary>
    public static UIElement Build(AiOnIslandPlugin plugin)
    {
        var current = plugin.ReadOptions();
        var loading = true;   // 初次填充期间不要回写设置

        var panel = new StackPanel { Spacing = 12, MaxWidth = 640 };

        panel.Children.Add(new TextBlock
        {
            Text = "岛上的 AI",
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });

        panel.Children.Add(new TextBlock
        {
            Text = "任何 OpenAI 兼容接口都行：官方 OpenAI、DeepSeek、Kimi、通义、Ollama、LiteLLM 网关…" +
                   "地址填到 /v1 就行（例如 https://api.openai.com/v1）。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontSize = 12,
        });

        // ---------- 三个必填项 ----------
        var baseUrlBox = new TextBox
        {
            Header = "API 地址（Base URL）",
            Text = current.BaseUrl,
            PlaceholderText = AiOptions.DefaultBaseUrl,
        };

        var apiKeyBox = new PasswordBox
        {
            Header = "API Key",
            Password = current.ApiKey,
            PlaceholderText = "sk-...",
            PasswordRevealMode = PasswordRevealMode.Peek,
        };

        var modelBox = new TextBox
        {
            Header = "模型名",
            Text = current.Model,
            PlaceholderText = AiOptions.DefaultModel,
        };

        var credentials = new StackPanel { Spacing = 10, Children = { baseUrlBox, apiKeyBox, modelBox } };

        var testButton = new Button { Content = "测试连接" };
        var saveButton = new Button { Content = "保存" };
        var testResult = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        };

        var credentialFooter = new StackPanel { Spacing = 8 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        buttons.Children.Add(saveButton);
        buttons.Children.Add(testButton);
        credentialFooter.Children.Add(buttons);
        credentialFooter.Children.Add(testResult);
        credentials.Children.Add(credentialFooter);

        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = credentials,
        });

        // ---------- 生成参数 ----------
        var temperature = new NumberBox
        {
            Header = "随机性 temperature（0 = 稳定，1 = 发散）",
            Value = current.Temperature,
            Minimum = 0,
            Maximum = 2,
            SmallChange = 0.1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };

        var maxTokens = new NumberBox
        {
            Header = "单次最多输出 token（0 = 不限制）",
            Value = current.MaxTokens,
            Minimum = 0,
            Maximum = 128000,
            SmallChange = 256,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };

        var systemPrompt = new TextBox
        {
            Header = "追加的风格要求（可选）",
            PlaceholderText = "例如：回答一律用中文，专业但口语化；代码块要带语言标记。",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
            Text = current.SystemPrompt,
        };

        // ---------- 思考模式（默认关，用户自己选） ----------
        var thinkingOn = new ToggleSwitch
        {
            Header = "思考模式",
            OnContent = "开启（更慢、更强）",
            OffContent = "关闭（默认，最快）",
            IsOn = current.ThinkingOn,
        };

        var styles = new[]
        {
            (AiOptions.ThinkingNone, "不发送这个字段（OpenAI 官方 / 不确定就选它）"),
            (AiOptions.ThinkingDeepSeek, "DeepSeek / 智谱（thinking）"),
            (AiOptions.ThinkingReasoning, "OpenRouter / o 系列（reasoning）"),
            (AiOptions.ThinkingQwen, "通义千问（enable_thinking）"),
            (AiOptions.ThinkingOllama, "Ollama（think）"),
        };

        // 这个下拉**一直可见**：关闭思考时也要靠它决定发哪个 disabled 字段（见下面那段说明）。
        var thinkingStyle = new ComboBox
        {
            Header = "字段名（各家服务商不一样，填错会被拒）",
            MinWidth = 340,
        };
        foreach (var (_, label) in styles) thinkingStyle.Items.Add(label);

        var styleIndex = Array.FindIndex(styles, s => s.Item1 == current.ThinkingStyle);
        thinkingStyle.SelectedIndex = styleIndex < 0 ? 0 : styleIndex;

        var thinkingHint = new TextBlock
        {
            Text = "「开启」= 模型先推理一遍再回答，更准但明显更慢；「关闭」= 不让它推理。\n" +
                   "为什么关闭也要选字段名：有些服务商（实测 DeepSeek）**默认就开思考**，" +
                   "不主动发关闭指令根本关不掉 —— 所以关的时候也会按这里选的字段发一次 disabled。\n" +
                   "只有选「不发送这个字段」，才是什么都不加（给不认识这个字段的服务商用）。\n" +
                   "它同时作用于「生成建议」和「执行」。选错字段名时服务端一般返回 400，换个选项即可。",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        };

        var thinkingBlock = new StackPanel
        {
            Spacing = 8,
            Children = { thinkingOn, thinkingStyle, thinkingHint },
        };

        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = new StackPanel { Spacing = 10, Children = { temperature, maxTokens, systemPrompt, thinkingBlock } },
        });

        // ---------- 行为开关 ----------
        var showOnIsland = new ToggleSwitch
        {
            Header = "在灵动岛上显示",
            OnContent = "显示",
            OffContent = "隐藏",
            IsOn = plugin.ReadShowOnIsland(),
        };

        var autoSuggest = new ToggleSwitch
        {
            Header = "拖入内容后自动让 AI 想建议",
            OnContent = "自动",
            OffContent = "手动",
            IsOn = plugin.ReadAutoSuggest(),
        };

        panel.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SettingsCardStyle"],
            Child = new StackPanel { Spacing = 6, Children = { showOnIsland, autoSuggest } },
        });

        // ---------- 提交逻辑 ----------

        void CommitAll()
        {
            if (loading) return;   // 初次填充阶段不回写

            plugin.ApplyOptions(new AiOptions
            {
                BaseUrl = (baseUrlBox.Text ?? "").Trim(),
                ApiKey = (apiKeyBox.Password ?? "").Trim(),
                Model = (modelBox.Text ?? "").Trim(),
                SystemPrompt = systemPrompt.Text ?? "",
                Temperature = double.IsNaN(temperature.Value) ? 0.7 : temperature.Value,
                MaxTokens = double.IsNaN(maxTokens.Value) ? 0 : (int)maxTokens.Value,
                ThinkingOn = thinkingOn.IsOn,
                ThinkingStyle = thinkingStyle.SelectedIndex >= 0 && thinkingStyle.SelectedIndex < styles.Length
                    ? styles[thinkingStyle.SelectedIndex].Item1
                    : AiOptions.ThinkingNone,
            });
        }

        void WireCommit(Control input)
        {
            input.LostFocus += (_, _) => CommitAll();

            if (input is TextBox tb)
            {
                tb.KeyDown += (_, e) =>
                {
                    if (e.Key == VirtualKey.Enter && !tb.AcceptsReturn) CommitAll();
                };
            }
            else if (input is PasswordBox pb)
            {
                pb.KeyDown += (_, e) =>
                {
                    if (e.Key == VirtualKey.Enter) CommitAll();
                };
            }
        }

        WireCommit(baseUrlBox);
        WireCommit(apiKeyBox);
        WireCommit(modelBox);
        WireCommit(systemPrompt);

        temperature.ValueChanged += (_, _) => CommitAll();
        maxTokens.ValueChanged += (_, _) => CommitAll();
        systemPrompt.LostFocus += (_, _) => CommitAll();

        saveButton.Click += (_, _) =>
        {
            CommitAll();   // ← 先提交再"保存"，否则用的是旧值
            testResult.Text = "已保存。";
        };

        testButton.Click += async (_, _) =>
        {
            CommitAll();   // ← 同上：按钮点击早于 LostFocus

            testButton.IsEnabled = false;
            testResult.Text = "正在连接…";

            try
            {
                var reply = await plugin.TestConnectionAsync();
                testResult.Text = "✅ 连接成功，模型回了：" + Shorten(reply, 60);
            }
            catch (Exception ex)
            {
                testResult.Text = "❌ " + ex.Message;
            }
            finally
            {
                testButton.IsEnabled = true;
            }
        };

        showOnIsland.Toggled += (_, _) =>
        {
            if (!loading) plugin.ApplyShowOnIsland(showOnIsland.IsOn);
        };

        autoSuggest.Toggled += (_, _) =>
        {
            if (!loading) plugin.ApplyAutoSuggest(autoSuggest.IsOn);
        };

        thinkingOn.Toggled += (_, _) =>
        {
            if (!loading) CommitAll();
        };

        thinkingStyle.SelectionChanged += (_, _) =>
        {
            if (!loading) CommitAll();
        };

        loading = false;   // 初次填充结束，之后才允许回写
        return panel;
    }

    private static string Shorten(string text, int max)
    {
        var flat = text.Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
