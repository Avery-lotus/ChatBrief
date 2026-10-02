using WeChatSummary.Desktop.Models;

namespace WeChatSummary.Desktop.Services;

public interface IAiSummaryService
{
    Task<string> SummarizeAsync(
        IReadOnlyList<ChatMessage> messages,
        PromptTemplate template,
        AppConfig config,
        CancellationToken cancellationToken = default);

    Task<string> GenerateTemplateAsync(
        string category,
        string direction,
        AppConfig config,
        CancellationToken cancellationToken = default);

    Task<string> AnswerQuestionAsync(
        string question,
        NaturalInsightAnswer localAnswer,
        IReadOnlyList<ChatMessage> evidenceMessages,
        AppConfig config,
        CancellationToken cancellationToken = default);
}
