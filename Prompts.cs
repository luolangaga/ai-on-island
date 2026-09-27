using System.Text;
using System.Text.Json;

namespace AiOnIsland;

/// <summary>一条可点击的建议：界面上显示 Title，点下去实际发给模型的是 Prompt。</summary>
public sealed record AiSuggestion(string Icon, string Title, string Prompt);

/// <summary>所有提示词都放这里，方便一处调整。</summary>
public static class Prompts
{
    /// <summary>执行阶段默认的系统提示词（设置页里可以追加自定义内容）。</summary>
    public const string DefaultSystem =
        "你是一个随叫随到的中文助手，工作在 Windows 灵动岛上。回答要直接、可用、不说废话；" +
        "保持 Markdown 简洁（可以用短标题、短列表、代码块），不要输出客套话。";

    /// <summary>
    /// 生成建议时用的输入上限。**故意压得比较小**：模型只要看懂"这是什么内容"就够设计动作了，
    /// 塞整篇进去纯属白等 token。截断后末尾会注明，模型自己知道不完整。
    /// </summary>
    private const int SuggestInputLimit = 1800;

    /// <summary>执行时随请求附带的输入上限，防止一次塞爆上下文。</summary>
    private const int ExecuteInputLimit = 100_000;

    /// <summary>
    /// 让模型为这份内容设计 3~4 个可执行动作。
    /// 这段提示词要**尽量短**：它在生成建议这条链路上是纯开销 —— 每个字都要等服务端吐完才轮到 JSON。
    /// 所以规则压到 4 条、只给一个示例、条数降到 3~4（原来 4~6 条，输出量差近一倍）。
    /// </summary>
    public static string BuildSuggestInstruction(AiPayload payload)
    {
        var kind = payload.ImageBytes is not null
            ? "一张图片"
            : payload.Kind == AiPayloadKind.Text
                ? "一段文字"
                : "一个文本文件";

        return
            $"我刚把{kind}拖到了岛上（内容见下）。给出 3~4 条**具体、可直接执行**的处理建议。\n" +
            "规则：\n" +
            "1. prompt 是一句自包含的祈使句，点一下会原样发给我执行，所以必须写清目标格式或风格（要带参数），" +
            "别用「翻译」「总结」这种没头没尾的词。\n" +
            "2. 至少一条是「转换 / 改写」类，明确写出目标格式或风格。\n" +
            "3. 要贴合这份内容的实际主题，不要套用放之四海皆准的模板。\n" +
            "4. title ≤ 6 字，prompt ≤ 40 字。\n" +
            "直接输出 JSON，不要解释、不要代码块：\n" +
            "{\"suggestions\":[{\"icon\":\"🔄\",\"title\":\"转成 JSON\",\"prompt\":\"转成 JSON，字段：title、summary、keywords\"}]}";
    }

    /// <summary>执行动作时构造的消息列表。</summary>
    public static List<ChatMessage> BuildExecuteMessages(AiOptions options, AiPayload payload, string request)
    {
        var messages = new List<ChatMessage>();

        var system = DefaultSystem;
        if (!string.IsNullOrWhiteSpace(options.SystemPrompt))
        {
            system += "\n\n用户给这个插件额外设定的要求：\n" + options.SystemPrompt.Trim();
        }
        messages.Add(new ChatMessage { Role = "system", Text = system });

        var text = new StringBuilder();
        text.Append("【用户的要求】\n").Append(request.Trim()).Append("\n\n");

        if (payload.ImageBytes is null)
        {
            var body = payload.Text ?? "";
            if (body.Length > ExecuteInputLimit) body = body[..ExecuteInputLimit] + "\n…（内容过长，已截断）";

            text.Append(payload.Kind == AiPayloadKind.Text ? "【拖入的文字】\n" : $"【拖入的文件：{payload.Label}】\n");
            text.Append(body);
        }
        else
        {
            text.Append("【拖入的图片】已在下方，请直接看图。");
        }

        messages.Add(new ChatMessage
        {
            Role = "user",
            Text = text.ToString(),
            ImageBytes = payload.ImageBytes,
            ImageMime = payload.ImageMime,
        });

        return messages;
    }

    /// <summary>把建议请求拼成消息：图片走 vision，文字直接贴内容。</summary>
    public static List<ChatMessage> BuildSuggestMessages(AiPayload payload)
    {
        var instruction = BuildSuggestInstruction(payload);

        if (payload.ImageBytes is not null)
        {
            return
            [
                new ChatMessage
                {
                    Role = "user",
                    Text = instruction,
                    ImageBytes = payload.ImageBytes,
                    ImageMime = payload.ImageMime,
                },
            ];
        }

        var body = payload.Text ?? "";
        if (body.Length > SuggestInputLimit) body = body[..SuggestInputLimit] + "\n…（后面还有很多，已省略）";

        return
        [
            new ChatMessage
            {
                Role = "user",
                Text = instruction + "\n\n内容如下：\n---\n" + body + "\n---",
            },
        ];
    }

    /// <summary>
    /// 解析建议 JSON。模型偶尔会加代码块、加解释文字，所以这里做宽松提取：
    /// 去掉 ``` 围栏 → 取第一个 { 到最后一个 } → 解析并做字段兜底。
    /// </summary>
    public static List<AiSuggestion> ParseSuggestions(string raw)
    {
        var result = new List<AiSuggestion>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            json = json.Trim('`');
            var firstLineEnd = json.IndexOf('\n');
            if (firstLineEnd > 0 && json[..firstLineEnd].Trim() is "json" or "JSON")
            {
                json = json[(firstLineEnd + 1)..];
            }
        }

        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start) return result;
        json = json[start..(end + 1)];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("suggestions", out var arr)) return result;
            if (arr.ValueKind != JsonValueKind.Array) return result;

            foreach (var item in arr.EnumerateArray())
            {
                var title = ReadString(item, "title");
                var prompt = ReadString(item, "prompt");
                var icon = ReadString(item, "icon");

                if (string.IsNullOrWhiteSpace(prompt)) continue;
                if (string.IsNullOrWhiteSpace(title)) title = Shorten(prompt, 8);
                if (string.IsNullOrWhiteSpace(prompt)) continue;

                result.Add(new AiSuggestion(
                    string.IsNullOrWhiteSpace(icon) ? "✨" : icon,
                    Shorten(title, 8),
                    prompt.Trim()));

                if (result.Count >= 8) break;
            }
        }
        catch (JsonException)
        {
            return result;   // 让调用方走兜底建议
        }

        return result;
    }

    /// <summary>接口没配好或生成失败时的兜底建议，保证面板永远有东西可点。</summary>
    public static List<AiSuggestion> FallbackSuggestions(AiPayload payload)
    {
        if (payload.ImageBytes is not null)
        {
            return
            [
                new("📖", "解释图片", "详细解释这张图片的内容：画面里有什么、在发生什么、可能是什么用途。"),
                new("🔤", "提取文字", "把图片里出现的所有文字原样提取出来，保持原有排版顺序。"),
                new("🎨", "画面分析", "分析这张图片的构图、配色和光线，说说它传达了什么情绪。"),
                new("✍️", "写条配文", "为这张图片写一条 50 字以内的社交媒体配文，语气轻松活泼。"),
                new("🔍", "找细节", "列出这张图片里容易被忽略的细节，逐条说明。"),
            ];
        }

        return
        [
            new("📝", "总结要点", "把这段内容总结成 3 条要点，每条不超过 20 个字。"),
            new("🔄", "转成 JSON", "把这段内容转换成 JSON，字段为 title、summary、keywords、action_items。"),
            new("🌐", "翻译成英文", "把这段内容翻译成地道的英文，保持原有的语气和结构。"),
            new("✨", "润色改写", "把这段内容润色一遍：语句通顺、去冗余，保持原意不变，直接给出润色后的完整文本。"),
            new("📋", "提取待办", "从这段内容里提取所有待办事项，按优先级排好，输出成清单。"),
            new("💬", "拟一封回复", "根据这段内容替我拟一封得体的回复，语气专业友好，150 字以内。"),
        ];
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private static string Shorten(string text, int max)
    {
        var t = text.Trim();
        return t.Length <= max ? t : t[..max];
    }
}
