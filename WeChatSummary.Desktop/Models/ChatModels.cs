namespace WeChatSummary.Desktop.Models;

public sealed record ChatRoom(string Id, string DisplayName, int MessageCount);

public sealed record FriendContact(string Id, string DisplayName, string? Remark, string? NickName, int MessageCount = 0);

public sealed record ChatMember(string Id, string DisplayName, int MessageCount);

public sealed record ChatMessage(DateTime Time, string SenderId, string SenderDisplayName, string Content, string MessageType = "text");

public sealed record InsightRankItem(string Name, int Count);

public sealed record InsightDailyCount(DateTime Day, int Count);

public sealed record InsightHourCount(int Hour, int Count);

public sealed record InsightOverviewStats(int MessageCount, int ObjectCount, string TopSpeaker, int TopSpeakerCount, int PeakHour, int PeakHourCount);

public sealed record NaturalInsightAnswer(
    string Question,
    string Answer,
    IReadOnlyList<InsightRankItem> Items,
    DateTime Start,
    DateTime End,
    string Keyword = "");

public sealed record PromptTemplate(string Name, string Category, string Content, bool BuiltIn);

public sealed record MemoEntry(
    string Id,
    string SourceType,
    string SourceId,
    string SourceName,
    string Content,
    DateTime CreatedAt,
    IReadOnlyList<int>? EvidenceNumbers = null,
    string EvidencePreview = "",
    string Category = "结论",
    string Status = "未归档");
