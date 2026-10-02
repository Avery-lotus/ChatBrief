using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WeChatSummary.Desktop.Models;

namespace WeChatSummary.Desktop.Services;

public sealed class DeepSeekSummaryService : IAiSummaryService
{
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("https://api.deepseek.com/")
    };

    public async Task<string> SummarizeAsync(
        IReadOnlyList<ChatMessage> messages,
        PromptTemplate template,
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
        {
            return "当前范围没有可总结的消息。";
        }

        var chatText = string.Join(Environment.NewLine, messages.Select((m, index) =>
            $"[#{index + 1:000}] [{m.Time:yyyy-MM-dd HH:mm}] {m.SenderDisplayName}: {m.Content}"));

        var prompt = template.Content
            .Replace("{count}", messages.Count.ToString())
            .Replace("{messages}", chatText)
            .Replace("{date}", DateTime.Now.ToString("yyyy年MM月dd日"))
            .Replace("{day_range}", string.Empty)
            .Replace("{member_notes}", "（正式版 C# 迁移中）");

        var selfAliases = ParseAliases(config.SelfAliases);
        var identityRule = selfAliases.Count == 0
            ? "消息发送者中的“我/本机”永远代表软件使用者本人，不要把它理解成对方。"
            : $"消息发送者中的“我/本机”永远代表软件使用者本人，不要把它理解成对方。聊天内容里如果有人称呼“{string.Join("、", selfAliases)}”，这些称呼也通常指软件使用者本人；做角色归因、负责人判断和好友分析时要优先按这个身份规则理解。";

        var citationRule =
            """
            输出要求补充：
            - 聊天记录每条都带有 [#001] 这样的消息编号。
            - 涉及具体事项、结论、风险、待办、时间、负责人、承诺或争议时，尽量在句末引用相关消息编号，例如：需要周五前确认方案。[#012][#018]
            - 如果一个判断没有明确消息依据，请写“待确认”，不要编造引用。
            - 引用编号必须来自聊天记录，不要生成不存在的编号。
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = "deepseek-chat",
            messages = new object[]
            {
                new { role = "system", content = $"你是一个擅长整理微信聊天记录的中文助手。只输出清晰 Markdown 正文。{identityRule}{Environment.NewLine}{citationRule}" },
                new { role = "user", content = prompt }
            },
            temperature = 0.3
        }), Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return $"AI 服务调用失败：{response.StatusCode}{Environment.NewLine}{json}";
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    public async Task<string> GenerateTemplateAsync(
        string category,
        string direction,
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(direction))
        {
            return "";
        }

        var productBrief =
            """
            ChatBrief 是一个微信聊天记录 AI 总结工具。用户会选择群聊或好友、日期时间范围、关键词和模板，然后软件把聊天记录替换进模板中的 {messages} 占位符，再交给 AI 生成分析结果。

            模板规则：
            1. 必须保留 {messages}，这是聊天记录占位符。
            2. 建议保留 {count}，表示消息数量。
            3. 可使用 {date} 表示生成日期。
            4. 可使用 {member_notes} 表示成员备注或身份补充。
            5. 模板应该要求输出 Markdown，便于软件渲染成文档视图、待办视图和思维导图。
            6. 模板要提醒 AI：只根据聊天记录总结，不确定就写“待确认”，不要编造。
            7. 模板要提醒 AI：“我/本机”以及用户在设置中填写的称呼代表软件使用者本人。
            """;

        var prompt =
            $$"""
            请为 ChatBrief 写一个「{{category}}」模板提示词。

            用户想要的总结方向：
            {{direction}}

            软件说明：
            {{productBrief}}

            输出要求：
            - 只输出模板正文，不要解释。
            - 模板正文必须包含 {messages}。
            - 模板正文要有明确标题、输出结构、规则和占位符。
            - 语言为简体中文，适合普通用户保存为模板。
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = "deepseek-chat",
            messages = new object[]
            {
                new { role = "system", content = "你是一个产品化 AI 提示词设计师，擅长为中文桌面软件生成稳定、清晰、可复用的模板提示词。" },
                new { role = "user", content = prompt }
            },
            temperature = 0.35
        }), Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return $"AI 模板生成失败：{response.StatusCode}{Environment.NewLine}{json}";
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()?
            .Trim() ?? string.Empty;
    }

    public async Task<string> AnswerQuestionAsync(
        string question,
        NaturalInsightAnswer localAnswer,
        IReadOnlyList<ChatMessage> evidenceMessages,
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return "AI 服务暂时不可用：请先在设置页填写 API Key。";
        }

        var ranked = localAnswer.Items.Count == 0
            ? "暂无统计排行。"
            : string.Join(Environment.NewLine, localAnswer.Items.Select((item, index) => $"{index + 1}. {item.Name}：{item.Count} 条"));
        var evidence = string.Join(Environment.NewLine, evidenceMessages.Take(80).Select((message, index) =>
            $"[#{index + 1:000}] [{message.Time:yyyy-MM-dd HH:mm}] {message.SenderDisplayName}: {message.Content}"));
        if (string.IsNullOrWhiteSpace(evidence))
        {
            evidence = "无可用原文证据。";
        }

        var prompt =
            $$"""
            用户问题：{{question}}

            本地 SQL 统计结论：
            {{localAnswer.Answer}}

            统计排行：
            {{ranked}}

            时间范围：{{localAnswer.Start:yyyy-MM-dd HH:mm}} 至 {{localAnswer.End:yyyy-MM-dd HH:mm}}
            关键词：{{(string.IsNullOrWhiteSpace(localAnswer.Keyword) ? "无" : localAnswer.Keyword)}}

            相关原文（最多 80 条）：
            {{evidence}}

            请基于“本地 SQL 统计结论 + 相关原文”回答用户问题。
            要求：
            - 用简体中文。
            - 先给直接答案，再补充 2-4 条依据。
            - 不要编造没有证据的事实；不确定就写“待确认”。
            - 如果统计和原文不足以回答，明确说明缺口。
            - 不要输出长篇报告。
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = "deepseek-chat",
            messages = new object[]
            {
                new { role = "system", content = "你是 ChatBrief 的本地聊天洞察助手。你只能根据提供的本地统计和证据消息回答，不要编造。" },
                new { role = "user", content = prompt }
            },
            temperature = 0.2
        }), Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return $"AI 提问失败：{response.StatusCode}{Environment.NewLine}{json}";
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()?
            .Trim() ?? "";
    }

    public async Task<AiBalance?> GetBalanceAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "user/balance");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        using var response = await Client.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var infos = new List<AiBalanceInfo>();
        if (root.TryGetProperty("balance_infos", out var balanceInfos) && balanceInfos.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in balanceInfos.EnumerateArray())
            {
                infos.Add(new AiBalanceInfo(
                    item.GetProperty("currency").GetString() ?? "",
                    ParseMoney(item.GetProperty("total_balance").GetString()),
                    ParseMoney(item.GetProperty("granted_balance").GetString()),
                    ParseMoney(item.GetProperty("topped_up_balance").GetString())));
            }
        }

        return new AiBalance(root.GetProperty("is_available").GetBoolean(), infos);
    }

    private static decimal ParseMoney(string? value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount)
            ? amount
            : 0m;

    private static IReadOnlyList<string> ParseAliases(string? aliases)
    {
        return (aliases ?? string.Empty)
            .Split(['、', ',', '，', ';', '；', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(alias => alias.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }
}
