using WeChatSummary.Desktop.Models;

namespace WeChatSummary.Desktop.Services;

public interface IWeChatDataService
{
    Task<IReadOnlyList<ChatRoom>> ListChatRoomsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FriendContact>> ListFriendsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMember>> ListMembersAsync(string chatRoomId, DateTime start, DateTime end, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, DateTime start, DateTime end, string? keyword = null, CancellationToken cancellationToken = default);
}
