namespace WeChatSummary.Desktop.Models;

public sealed record AiBalance(bool IsAvailable, IReadOnlyList<AiBalanceInfo> Items);

public sealed record AiBalanceInfo(string Currency, decimal TotalBalance, decimal GrantedBalance, decimal ToppedUpBalance);
