namespace AiOnIsland;

/// <summary>拖进来 / 打开面板时携带的那一份内容。</summary>
public enum AiPayloadKind
{
    /// <summary>一段文字（浏览器或编辑器里选中的文字、链接）。</summary>
    Text,

    /// <summary>一张图片的原始字节（浏览器拖来的图、截图）。</summary>
    Image,

    /// <summary>磁盘上的一个文件（图片 → 按图片处理，文本类 → 读出文字）。</summary>
    File,
}

/// <summary>一次处理的输入内容。</summary>
public sealed class AiPayload
{
    public AiPayloadKind Kind { get; init; }

    /// <summary>界面上显示的来源名（文件名，或"选中的文字"/"拖入的图片"）。</summary>
    public string Label { get; init; } = "";

    /// <summary>文字内容（Kind == Text / 文本类文件时有值）。</summary>
    public string? Text { get; init; }

    /// <summary>图片字节（Kind == Image / 图片文件时有值），保留源格式。</summary>
    public byte[]? ImageBytes { get; init; }

    /// <summary>图片 MIME，例如 image/png。</summary>
    public string? ImageMime { get; init; }

    /// <summary>内容被截断过（超长文本只取了前面一段）。</summary>
    public bool Truncated { get; init; }

    /// <summary>同批还有几个文件没被处理（一次只处理第一个）。</summary>
    public int ExtraFileCount { get; init; }

    /// <summary>给设置页/岛体用的一句话摘要。</summary>
    public string Summary => Kind switch
    {
        AiPayloadKind.Image => $"图片 · {SizeText()}",
        AiPayloadKind.File when ImageBytes is not null => $"图片文件 · {SizeText()}",
        AiPayloadKind.File => $"文本文件 · {Text?.Length ?? 0} 字{(Truncated ? "（已截断）" : "")}",
        _ => $"文字 · {Text?.Length ?? 0} 字{(Truncated ? "（已截断）" : "")}",
    };

    /// <summary>预览用的一小段文字（图片返回空）。</summary>
    public string Preview(int max = 300)
    {
        if (ImageBytes is not null) return "";
        var t = Text ?? "";
        if (t.Length <= max) return t;
        return t[..max] + "…";
    }

    private string SizeText()
    {
        var bytes = ImageBytes?.Length ?? 0;
        return bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:0.0} MB"
            : $"{Math.Max(1, bytes / 1024)} KB";
    }
}

/// <summary>从设置里读出来的一份配置快照。</summary>
public sealed class AiOptions
{
    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";
    public string SystemPrompt { get; init; } = "";
    public double Temperature { get; init; } = 0.7;
    public int MaxTokens { get; init; }

    /// <summary>
    /// 思考模式开关。**默认关闭**。
    ///
    /// 一个开关管全部调用（生成建议 / 执行 / 测试连接），不再分场景。
    /// 注意「关闭」不等于"什么都不发"：有些服务商（实测 DeepSeek）**默认就开思考**，
    /// 不发字段根本关不掉 —— 所以关闭时会按 <see cref="ThinkingStyle"/> 主动发 disabled。
    /// </summary>
    public bool ThinkingOn { get; init; }

    /// <summary>
    /// 用哪种字段名控制思考。各家服务商没有统一标准，所以必须可选；关闭时同样按这个风格发 disabled。
    /// <c>none</c> = 完全不发这个字段 —— 不认识它的服务商（OpenAI 官方等）必须选这个，否则会被 400。
    /// </summary>
    public string ThinkingStyle { get; init; } = ThinkingNone;

    public const string ThinkingNone = "none";            // 不发任何字段
    public const string ThinkingDeepSeek = "deepseek";    // thinking: { type: "enabled" / "disabled" }
    public const string ThinkingReasoning = "reasoning";  // reasoning: { enabled: true / false }
    public const string ThinkingQwen = "qwen";            // enable_thinking: true / false
    public const string ThinkingOllama = "ollama";        // think: true / false

    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    public const string DefaultModel = "gpt-4o-mini";

    /// <summary>
    /// 按 baseUrl 猜一个默认风格（用户还没选过时用）。猜不出就 <c>none</c> —— 最安全，不会 400。
    /// </summary>
    public static string DetectThinkingStyle(string? baseUrl)
    {
        var u = (baseUrl ?? "").ToLowerInvariant();
        if (u.Length == 0) return ThinkingNone;
        if (u.Contains("deepseek") || u.Contains("bigmodel") || u.Contains("zhipu")) return ThinkingDeepSeek;
        if (u.Contains("openrouter")) return ThinkingReasoning;
        if (u.Contains("dashscope") || u.Contains("aliyun") || u.Contains("qwen")) return ThinkingQwen;
        if (u.Contains("ollama") || u.Contains("11434")) return ThinkingOllama;
        return ThinkingNone;
    }

    /// <summary>三项都填了才算配置完成，否则只能进设置页。</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(Model);
}
