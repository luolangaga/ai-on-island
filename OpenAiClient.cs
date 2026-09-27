using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiOnIsland;

/// <summary>一条发给模型的消息。</summary>
public sealed class ChatMessage
{
    public string Role { get; init; } = "user";
    public string Text { get; init; } = "";

    /// <summary>要一起发给模型的图片（vision 模型用），保留源格式字节。</summary>
    public byte[]? ImageBytes { get; init; }
    public string? ImageMime { get; init; }
}

/// <summary>调用失败时抛这个，消息已经是可以直接显示给用户的中文。</summary>
public sealed class AiRequestException : Exception
{
    public AiRequestException(string message) : base(message) { }
}

/// <summary>
/// OpenAI 兼容接口的极简客户端：POST {base}/chat/completions，stream=true，逐段回调增量文本。
/// 只用 System.Text.Json + HttpClient，不引第三方依赖。
/// </summary>
public static class OpenAiClient
{
    /// <summary>插件里所有请求共用一支 HttpClient（每次 new 会耗尽端口）。</summary>
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// 把用户填的 base 地址拼成 chat/completions 端点。
    /// 接受 <c>https://api.openai.com</c>、<c>https://api.openai.com/v1</c>、<c>…/v1/chat/completions</c> 这几种写法。
    /// </summary>
    public static string ResolveEndpoint(string? baseUrl)
    {
        var raw = (baseUrl ?? "").Trim().TrimEnd('/');
        if (raw.Length == 0) throw new AiRequestException("还没填 API 地址。");

        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "https://" + raw;
        }

        if (raw.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            throw new AiRequestException($"API 地址看起来不对：{baseUrl}");
        }

        // 只给了域名（没有路径）时补上 /v1；已经有路径（…/v1、…/api/v3）就只补 chat/completions
        var hasPath = uri.AbsolutePath.Trim('/').Length > 0;
        return hasPath ? raw + "/chat/completions" : raw + "/v1/chat/completions";
    }

    /// <summary>
    /// 发一次请求。<paramref name="onDelta"/> 在**后台线程**被调用（增量文本），
    /// 调用方自己负责编组回 UI 线程。返回完整的最终文本。
    /// </summary>
    public static async Task<string> ChatAsync(
        AiOptions options,
        IReadOnlyList<ChatMessage> messages,
        Action<string>? onDelta,
        CancellationToken ct)
    {
        var endpoint = ResolveEndpoint(options.BaseUrl);

        var body = BuildRequestBody(options, messages, stream: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new AiRequestException($"连不上 {endpoint}：{ex.Message}");
        }
        catch (Exception ex)
        {
            throw new AiRequestException($"请求发送失败：{ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await SafeReadAsync(response, ct).ConfigureAwait(false);
                throw new AiRequestException(DescribeHttpError((int)response.StatusCode, error));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var full = new StringBuilder();
            await foreach (var chunk in ReadDeltasAsync(reader, ct).ConfigureAwait(false))
            {
                if (chunk.Length == 0) continue;
                full.Append(chunk);
                onDelta?.Invoke(chunk);
            }

            if (full.Length == 0)
            {
                throw new AiRequestException("模型返回了空内容。换个模型名或检查一下服务端日志。");
            }

            return full.ToString();
        }
    }

    /// <summary>
    /// 不带 stream 的简易调用：用于「测试连接」和「生成建议」这种短请求，
    /// 免得为了一句话也要走一遍 SSE 解析。
    /// 思考模式的设置**对这里同样生效**（一个开关管全部调用）。
    /// </summary>
    public static async Task<string> AskOnceAsync(
        AiOptions options, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var endpoint = ResolveEndpoint(options.BaseUrl);
        var body = BuildRequestBody(options, messages, stream: false);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey.Trim());

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new AiRequestException($"连不上 {endpoint}：{ex.Message}");
        }

        using (response)
        {
            var text = await SafeReadAsync(response, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new AiRequestException(DescribeHttpError((int)response.StatusCode, text));
            }

            return ExtractContent(text) ?? throw new AiRequestException("模型返回里没有找到正文。");
        }
    }

    // ---------------- 内部 ----------------

    private static string BuildRequestBody(
        AiOptions options, IReadOnlyList<ChatMessage> messages, bool stream)
    {
        var array = new JsonArray();
        foreach (var m in messages)
        {
            var node = new JsonObject { ["role"] = m.Role };

            if (m.ImageBytes is { Length: > 0 })
            {
                var parts = new JsonArray();
                if (!string.IsNullOrWhiteSpace(m.Text))
                {
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
                }

                var mime = string.IsNullOrWhiteSpace(m.ImageMime) ? "image/png" : m.ImageMime;
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:{mime};base64,{Convert.ToBase64String(m.ImageBytes)}",
                    },
                });

                node["content"] = parts;
            }
            else
            {
                node["content"] = m.Text;
            }

            array.Add(node);
        }

        var root = new JsonObject
        {
            ["model"] = options.Model.Trim(),
            ["messages"] = array,
            ["stream"] = stream,
            ["temperature"] = Math.Clamp(options.Temperature, 0, 2),
        };

        if (options.MaxTokens > 0) root["max_tokens"] = options.MaxTokens;

        ApplyThinking(root, options.ThinkingOn, options.ThinkingStyle);

        return root.ToJsonString();
    }

    /// <summary>
    /// 按用户选的"服务商风格"把思考模式的开关塞进请求体。
    /// 这块没有统一标准，各家字段名和取值形状都不一样，所以只能分风格处理；
    /// 选错风格多半会被服务端直接 400（消息里会写是哪个字段不认识），改成「关闭」即可。
    /// 另外**开和关都要写**：实测 DeepSeek 默认就开思考（不发字段时返回里带 reasoning_content），
    /// 所以"关闭"不能靠"不发字段"实现，必须显式发 disabled；选 none 风格才是什么都不发。
    /// 思考过程（DeepSeek 的 reasoning_content 等）这里**故意不读** —— 我们要的只是最终答案。
    /// </summary>
    private static void ApplyThinking(JsonObject root, bool on, string style)
    {
        switch ((style ?? "").Trim().ToLowerInvariant())
        {
            case AiOptions.ThinkingDeepSeek:
                root["thinking"] = new JsonObject { ["type"] = on ? "enabled" : "disabled" };
                break;

            case AiOptions.ThinkingReasoning:
                root["reasoning"] = new JsonObject { ["enabled"] = on };
                break;

            case AiOptions.ThinkingQwen:
                root["enable_thinking"] = on;
                break;

            case AiOptions.ThinkingOllama:
                root["think"] = on;
                break;

            default:
                // none / 认不出来的值：什么都不发，避免被不认识该字段的服务端 400
                break;
        }
    }

    /// <summary>逐行读 SSE，产出每个 delta 的文本。</summary>
    private static async IAsyncEnumerable<string> ReadDeltasAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;                       // 流结束

            if (line.Length == 0) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;   // 注释/心跳行

            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") break;

            var delta = ExtractDelta(payload);
            if (delta is not null) yield return delta;
        }
    }

    /// <summary>从一条 SSE 的 data 里取 choices[0].delta.content。</summary>
    private static string? ExtractDelta(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)) return null;
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;

            var first = choices[0];
            if (!first.TryGetProperty("delta", out var delta)) return null;
            if (!delta.TryGetProperty("content", out var content)) return null;
            if (content.ValueKind != JsonValueKind.String) return null;

            return content.GetString();
        }
        catch (JsonException)
        {
            return null;   // 个别服务端会插非 JSON 的心跳行，忽略即可
        }
    }

    /// <summary>非流式响应里取 choices[0].message.content。</summary>
    private static string? ExtractContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)) return null;
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;

            if (!choices[0].TryGetProperty("message", out var message)) return null;
            if (!message.TryGetProperty("content", out var content)) return null;
            if (content.ValueKind != JsonValueKind.String) return null;

            return content.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>把 HTTP 错误翻译成用户看得懂的一句话（尽量带上服务端原文）。</summary>
    private static string DescribeHttpError(int status, string body)
    {
        var detail = ExtractErrorMessage(body);
        if (detail.Length > 400) detail = detail[..400] + "…";

        var hint = status switch
        {
            401 or 403 => "API Key 不对或没有权限",
            404 => "地址或模型名不对（404）",
            429 => "请求太频繁或额度用完了（429）",
            >= 500 => "服务端出错了",
            _ => "请求被拒绝",
        };

        return detail.Length > 0
            ? $"{hint}（HTTP {status}）：{detail}"
            : $"{hint}（HTTP {status}）";
    }

    /// <summary>从错误响应里挖出人类可读的 message 字段。</summary>
    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? "";
                if (error.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                {
                    return msg.GetString() ?? "";
                }
            }

            if (root.TryGetProperty("message", out var direct) && direct.ValueKind == JsonValueKind.String)
            {
                return direct.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // 不是 JSON：多半是网关的 HTML 错误页，直接截原文
        }

        return body.Trim();
    }

    /// <summary>按扩展名猜图片 MIME（拿不到就回退 image/png）。</summary>
    public static string MimeFromExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "image/png",
        };
    }

    /// <summary>这些扩展名的文件按"图片"处理。</summary>
    public static bool IsImageFile(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg";
}
