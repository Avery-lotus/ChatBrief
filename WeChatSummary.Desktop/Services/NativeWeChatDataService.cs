using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WeChatSummary.Desktop.Models;

namespace WeChatSummary.Desktop.Services;

public sealed class NativeWeChatDataService : IWeChatDataService, IDisposable
{
    private const int PageSize = 4096;
    private const int SaltSize = 16;
    private const int ReserveSize = 80;
    private const int KeySize = 32;
    private const uint MemCommit = 0x1000;
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private static readonly HashSet<uint> ReadableProtects = [0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80];

    private readonly object _initLock = new();
    private string? _decryptedMessageDbPath;
    private SqliteConnection? _messageConnection;
    private string? _wechatUserDir;
    private string? _selfWechatId;
    private string? _preferredWechatRoot;
    private readonly LocalOcrService _ocrService = new();
    private List<ImageFileCandidate>? _imageFileIndex;
    private readonly Dictionary<string, string> _chatroomDisplayNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _contactDisplayNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _chatroomMemberDisplayNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ContactInfo> _contacts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownChatroomMemberIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _oneToOneConversationIds = new(StringComparer.OrdinalIgnoreCase);

    public string? CurrentWechatUserDir => _wechatUserDir;
    public IReadOnlyCollection<string> CurrentSelfDisplayNames
    {
        get
        {
            var names = new List<string>();
            if (!string.IsNullOrWhiteSpace(_selfWechatId))
            {
                names.Add(_selfWechatId);
                if (_contactDisplayNames.TryGetValue(_selfWechatId, out var contactName) && !string.IsNullOrWhiteSpace(contactName))
                {
                    names.Add(contactName);
                }
                if (_chatroomMemberDisplayNames.TryGetValue(_selfWechatId, out var memberName) && !string.IsNullOrWhiteSpace(memberName))
                {
                    names.Add(memberName);
                }
            }

            return names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public Task<IReadOnlyList<ChatRoom>> ListChatRoomsAsync(CancellationToken cancellationToken = default)
    {
        EnsureMessageConnection();
        using var cmd = _messageConnection!.CreateCommand();
        cmd.CommandText = "SELECT user_name FROM Name2Id WHERE user_name LIKE '%@chatroom'";
        using var reader = cmd.ExecuteReader();
        var rooms = new List<ChatRoom>();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            rooms.Add(new ChatRoom(id, ResolveDisplayName(id), CountRows(GetConversationTable(id))));
        }
        return Task.FromResult<IReadOnlyList<ChatRoom>>(rooms.OrderByDescending(r => r.MessageCount).ToList());
    }

    public Task<IReadOnlyList<FriendContact>> ListFriendsAsync(CancellationToken cancellationToken = default)
    {
        EnsureMessageConnection();
        var contacts = new Dictionary<string, FriendContact>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _contacts.Values.Where(item => IsRealFriendId(item.Id) && !string.IsNullOrWhiteSpace(item.DisplayName)))
        {
            var table = GetConversationTable(item.Id);
            var count = TableExists(table) ? CountRows(table) : 0;
            contacts[item.Id] = new FriendContact(item.Id, item.DisplayName, item.Remark, item.NickName, count);
        }

        foreach (var id in _oneToOneConversationIds)
        {
            var table = GetConversationTable(id);
            var count = TableExists(table) ? CountRows(table) : 0;
            if (count <= 0)
            {
                continue;
            }

            if (contacts.TryGetValue(id, out var existing))
            {
                contacts[id] = existing with { MessageCount = count };
                continue;
            }

            var displayName = ResolveDisplayName(id);
            contacts[id] = new FriendContact(id, displayName, null, null, count);
        }

        var results = contacts.Values
            .OrderByDescending(c => c.MessageCount)
            .ThenBy(c => c.DisplayName)
            .ToList();
        return Task.FromResult<IReadOnlyList<FriendContact>>(results);
    }

    public Task<IReadOnlyList<ChatMember>> ListMembersAsync(string chatRoomId, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        EnsureMessageConnection();
        var table = GetConversationTable(NormalizeChatroomId(chatRoomId));
        if (!TableExists(table))
        {
            return Task.FromResult<IReadOnlyList<ChatMember>>([]);
        }

        var counts = new Dictionary<string, int>();
        foreach (var row in QueryMessageRows(table, start, end, null, chatRoomId))
        {
            counts[row.Sender] = counts.GetValueOrDefault(row.Sender) + 1;
        }

        return Task.FromResult<IReadOnlyList<ChatMember>>(counts
            .OrderByDescending(x => x.Value)
            .Select(x => new ChatMember(x.Key, ResolveDisplayName(x.Key), x.Value))
            .ToList());
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, DateTime start, DateTime end, string? keyword = null, CancellationToken cancellationToken = default)
    {
        EnsureMessageConnection();
        var table = GetConversationTable(conversationId);
        if (!TableExists(table))
        {
            return [];
        }

        var messages = new List<ChatMessage>();
        foreach (var row in QueryMessageRows(table, start, end, null, conversationId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = await EnrichMessageBodyAsync(row, cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            var displayName = ResolveDisplayName(row.Sender);
            if (!string.IsNullOrWhiteSpace(keyword) &&
                !body.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                !displayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            messages.Add(new ChatMessage(row.Time, row.Sender, displayName, body, row.MessageType));
        }
        return messages;
    }

    public void Dispose()
    {
        ResetConnection();
    }

    public void SetPreferredWechatRoot(string? root)
    {
        var normalized = string.IsNullOrWhiteSpace(root) ? null : root.Trim();
        if (string.Equals(_preferredWechatRoot, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_initLock)
        {
            _preferredWechatRoot = normalized;
            ResetConnection();
        }
    }

    public static string? ResolveWechatUserDir(string? selectedRoot)
    {
        if (string.IsNullOrWhiteSpace(selectedRoot) || !Directory.Exists(selectedRoot))
        {
            return null;
        }

        var root = selectedRoot.Trim();
        if (File.Exists(Path.Combine(root, "db_storage", "message", "message_0.db")))
        {
            return root;
        }

        return FindUserDirsUnderRoot(root)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "db_storage", "message", "message_0.db")))
            .FirstOrDefault();
    }

    public static string? AutoDetectWechatUserDir()
    {
        return FindWeChatUserDir();
    }

    private void ResetConnection()
    {
        _messageConnection?.Dispose();
        _messageConnection = null;
        _wechatUserDir = null;
        _selfWechatId = null;
        _imageFileIndex = null;
        _chatroomDisplayNames.Clear();
        _contactDisplayNames.Clear();
        _chatroomMemberDisplayNames.Clear();
        _contacts.Clear();
        _knownChatroomMemberIds.Clear();
        _oneToOneConversationIds.Clear();
        if (_decryptedMessageDbPath is not null && File.Exists(_decryptedMessageDbPath))
        {
            try { File.Delete(_decryptedMessageDbPath); } catch { }
        }
        _decryptedMessageDbPath = null;
    }

    private void EnsureMessageConnection()
    {
        if (_messageConnection is not null) return;
        lock (_initLock)
        {
            if (_messageConnection is not null) return;
            var userDir = ResolveWechatUserDir(_preferredWechatRoot) ??
                          FindWeChatUserDir() ??
                          throw new InvalidOperationException("未找到微信数据目录。请确认微信 Windows 4.1.9.57 已登录；如果你改过微信文件保存位置，请先选择微信数据目录。");
            _wechatUserDir = userDir;
            _selfWechatId = new DirectoryInfo(userDir).Name;
            var dbStorage = Path.Combine(userDir, "db_storage");
            var messageDb = Path.Combine(dbStorage, "message", "message_0.db");
            if (!File.Exists(messageDb))
            {
                throw new FileNotFoundException("未找到 message_0.db。", messageDb);
            }

            var keyMap = ExtractKeysFromMemory(dbStorage);
            var salt = Convert.ToHexString(ReadShared(messageDb, SaltSize)).ToLowerInvariant();
            if (!keyMap.TryGetValue(salt, out var keyHex))
            {
                throw new InvalidOperationException("未能提取消息数据库密钥。请保持微信已登录并重新初始化。");
            }

            _decryptedMessageDbPath = DecryptDatabase(messageDb, keyHex);
            _messageConnection = new SqliteConnection($"Data Source={_decryptedMessageDbPath};Mode=ReadOnly");
            _messageConnection.Open();
            LoadOneToOneConversationIds();
            LoadKnownChatroomMemberIds();
            LoadDisplayNamesFromDecryptedDatabases(dbStorage, keyMap);
        }
    }

    private void LoadOneToOneConversationIds()
    {
        _oneToOneConversationIds.Clear();
        using var cmd = _messageConnection!.CreateCommand();
        cmd.CommandText = "SELECT user_name FROM Name2Id WHERE user_name NOT LIKE '%@chatroom'";
        using var reader = cmd.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        foreach (var id in ids)
        {
            var table = GetConversationTable(id);
            if (IsRealFriendId(id) && TableExists(table) && CountRows(table) > 0)
            {
                _oneToOneConversationIds.Add(id);
            }
        }
    }

    private void LoadKnownChatroomMemberIds()
    {
        _knownChatroomMemberIds.Clear();
        using var cmd = _messageConnection!.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'Msg_%'";
        using var tableReader = cmd.ExecuteReader();
        var tables = new List<string>();
        while (tableReader.Read())
        {
            tables.Add(tableReader.GetString(0));
        }

        var idNameMap = LoadIdNameMap();
        foreach (var table in tables)
        {
            var conversationId = idNameMap.Values.FirstOrDefault(id => GetConversationTable(id) == table);
            if (conversationId is null || !conversationId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var rowCmd = _messageConnection.CreateCommand();
            rowCmd.CommandText = $"SELECT DISTINCT real_sender_id FROM \"{table}\" WHERE real_sender_id IS NOT NULL";
            using var reader = rowCmd.ExecuteReader();
            while (reader.Read())
            {
                var resolved = ResolveRealSender(reader.GetValue(0)?.ToString(), idNameMap);
                if (IsRealFriendId(resolved))
                {
                    _knownChatroomMemberIds.Add(resolved);
                }
            }
        }
    }

    private void LoadDisplayNamesFromDecryptedDatabases(string dbStorage, Dictionary<string, string> keyMap)
    {
        var contactDb = Path.Combine(dbStorage, "contact", "contact.db");
        if (!File.Exists(contactDb) || !TryResolveDatabaseKey(contactDb, keyMap, out var keyHex))
        {
            return;
        }

        string? decrypted = null;
        try
        {
            decrypted = DecryptDatabase(contactDb, keyHex);
            using var conn = new SqliteConnection($"Data Source={decrypted};Mode=ReadOnly");
            conn.Open();
            ScanContactTable(conn);
        }
        catch
        {
            // Keep raw IDs if the contact database schema is unavailable.
        }
        finally
        {
            if (decrypted is not null)
            {
                try { File.Delete(decrypted); } catch { }
            }
        }
    }

    private static bool TryResolveDatabaseKey(string dbPath, Dictionary<string, string> keyMap, out string keyHex)
    {
        keyHex = "";
        var salt = Convert.ToHexString(ReadShared(dbPath, SaltSize)).ToLowerInvariant();
        return keyMap.TryGetValue(salt, out keyHex!);
    }

    private void ScanContactTable(SqliteConnection conn)
    {
        var table = ListTables(conn).FirstOrDefault(t => t.Equals("Contact", StringComparison.OrdinalIgnoreCase));
        if (table is null)
        {
            return;
        }

        var columns = ListColumns(conn, table);
        var idColumn = PickColumn(columns, "user_name", "username", "usrname", "user_id", "id");
        if (idColumn is null)
        {
            return;
        }

        var remarkColumn = PickColumn(columns, "remark", "conremark", "con_remark", "remark_name", "remarkname", "remarkName", "displayRemark", "labelName");
        var nickColumn = PickColumn(columns, "nick_name", "nickname", "nickName", "name", "display_name", "displayName");
        var aliasColumn = PickColumn(columns, "alias", "pyInitial", "quanPin");
        var contactTypeColumn = PickExactColumn(columns, "flag", "type", "contact_type", "contactType", "contact_flag", "contactFlag", "friend_flag", "friendFlag");
        var verifyFlagColumn = PickExactColumn(columns, "verifyFlag", "verify_flag", "verifyFlagEx");
        var isContactColumn = PickExactColumn(columns, "is_contact", "isContact", "is_friend", "isFriend", "friend", "isContactFriend");
        var deleteFlagColumn = PickExactColumn(columns, "delete_flag", "deleteFlag", "deleted", "is_deleted", "isDeleted");
        var inChatRoomColumn = PickExactColumn(columns, "is_in_chat_room", "isInChatRoom");
        var selectColumns = new[] { idColumn, remarkColumn, nickColumn, aliasColumn, contactTypeColumn, verifyFlagColumn, isContactColumn, deleteFlagColumn, inChatRoomColumn }
            .OfType<string>()
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", selectColumns.Select(Quote))} FROM \"{table}\"";
        using var reader = cmd.ExecuteReader();
        var diagnosticLines = new List<string>
        {
            $"Contact columns: {string.Join(", ", columns)}",
            $"Picked: id={idColumn}; remark={remarkColumn}; nick={nickColumn}; alias={aliasColumn}; contactType={contactTypeColumn}; verifyFlag={verifyFlagColumn}; isContact={isContactColumn}; deleteFlag={deleteFlagColumn}; inChatRoom={inChatRoomColumn}"
        };
        var totalRows = 0;
        var acceptedRows = 0;
        var rejectedGroupCache = 0;
        var rejectedNotFriend = 0;
        var acceptedNoRemarkWithOneToOne = 0;
        while (reader.Read())
        {
            totalRows++;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values[selectColumns[i]] = CleanContactValue(DecodeDbValue(reader.GetValue(i)));
            }

            var id = values.GetValueOrDefault(idColumn!, "");
            var remark = remarkColumn is null ? "" : values.GetValueOrDefault(remarkColumn, "");
            var nick = nickColumn is null ? "" : values.GetValueOrDefault(nickColumn, "");
            var alias = aliasColumn is null ? "" : values.GetValueOrDefault(aliasColumn, "");
            var contactType = contactTypeColumn is null ? null : values.GetValueOrDefault(contactTypeColumn, "");
            var verifyFlag = verifyFlagColumn is null ? null : values.GetValueOrDefault(verifyFlagColumn, "");
            var isContact = isContactColumn is null ? null : values.GetValueOrDefault(isContactColumn, "");
            var deleteFlag = deleteFlagColumn is null ? null : values.GetValueOrDefault(deleteFlagColumn, "");
            var inChatRoom = inChatRoomColumn is null ? null : values.GetValueOrDefault(inChatRoomColumn, "");
            AddChatroomMemberDisplayName(id, FirstUseful(remark, nick, alias));
            var result = AddContactInfo(id, remark, nick, alias, contactType, verifyFlag, isContact, deleteFlag, inChatRoom);
            if (result == ContactAcceptResult.Accepted)
            {
                acceptedRows++;
                if (string.IsNullOrWhiteSpace(remark) && _oneToOneConversationIds.Contains(id))
                {
                    acceptedNoRemarkWithOneToOne++;
                }
            }
            else if (result == ContactAcceptResult.GroupCache)
            {
                rejectedGroupCache++;
            }
            else
            {
                rejectedNotFriend++;
            }
        }
        diagnosticLines.Add($"Rows: total={totalRows}; accepted={acceptedRows}; rejectedGroupCache={rejectedGroupCache}; rejectedOther={rejectedNotFriend}; oneToOneNoRemarkAccepted={acceptedNoRemarkWithOneToOne}; oneToOneConversationIds={_oneToOneConversationIds.Count}; knownChatroomMembers={_knownChatroomMemberIds.Count}");
        try
        {
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "chatbrief-contact-diagnostics.txt"), diagnosticLines);
        }
        catch
        {
        }
    }

    private static string Quote(string column) => $"\"{column}\"";

    private static List<string> ListTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = cmd.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }

    private static List<string> ListColumns(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = cmd.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static string? PickColumn(IEnumerable<string> columns, params string[] candidates)
    {
        var list = columns.ToList();
        foreach (var candidate in candidates)
        {
            var exact = list.FirstOrDefault(c => c.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        foreach (var candidate in candidates)
        {
            var fuzzy = list.FirstOrDefault(c => c.Contains(candidate, StringComparison.OrdinalIgnoreCase));
            if (fuzzy is not null) return fuzzy;
        }
        return null;
    }

    private static string? PickExactColumn(IEnumerable<string> columns, params string[] candidates)
    {
        var list = columns.ToList();
        foreach (var candidate in candidates)
        {
            var exact = list.FirstOrDefault(c => c.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        return null;
    }

    private static bool IsUsefulDisplayName(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return false;
        if (id == name) return false;
        if (name.All(char.IsDigit)) return false;
        if (name.StartsWith("wxid_", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private void AddDisplayName(string id, string name)
    {
        if (!IsUsefulDisplayName(id, name))
        {
            return;
        }

        if (id.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase))
        {
            _chatroomDisplayNames[id] = name;
            return;
        }

        if (IsRealFriendId(id))
        {
            _contactDisplayNames[id] = name;
        }
    }

    private void AddChatroomMemberDisplayName(string id, string name)
    {
        if (!IsRealFriendId(id) || !IsUsefulDisplayName(id, name))
        {
            return;
        }

        _chatroomMemberDisplayNames[id] = name;
    }

    private ContactAcceptResult AddContactInfo(string id, string remark, string nickName, string alias, string? contactType = null, string? verifyFlag = null, string? isContact = null, string? deleteFlag = null, string? inChatRoom = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return ContactAcceptResult.NotFriend;
        }

        if (id.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase))
        {
            var chatroomName = FirstUseful(remark, alias, nickName);
            if (!string.IsNullOrWhiteSpace(chatroomName))
            {
                _chatroomDisplayNames[id] = chatroomName;
            }
            return ContactAcceptResult.NotFriend;
        }

        if (!IsRealFriendId(id) || !LooksLikeAddressBookFriend(contactType, verifyFlag, isContact, deleteFlag, inChatRoom, remark, _oneToOneConversationIds.Contains(id)))
        {
            return ContactAcceptResult.NotFriend;
        }

        if (_knownChatroomMemberIds.Contains(id) && string.IsNullOrWhiteSpace(remark) && !_oneToOneConversationIds.Contains(id))
        {
            return ContactAcceptResult.GroupCache;
        }

        var display = !string.IsNullOrWhiteSpace(remark)
            ? remark
            : !string.IsNullOrWhiteSpace(nickName)
              ? nickName
                : "";
        if (string.IsNullOrWhiteSpace(display))
        {
            return ContactAcceptResult.NotFriend;
        }

        _contacts[id] = new ContactInfo(id, display, remark, nickName, alias);
        _contactDisplayNames[id] = display;
        return ContactAcceptResult.Accepted;
    }

    private static bool LooksLikeAddressBookFriend(string? contactType, string? verifyFlag, string? isContact, string? deleteFlag, string? inChatRoom, string remark, bool hasOneToOneConversation)
    {
        if (!string.IsNullOrWhiteSpace(deleteFlag) && SafeReadInt64(deleteFlag) != 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(remark))
        {
            return true;
        }

        if (hasOneToOneConversation)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(verifyFlag) && SafeReadInt64(verifyFlag) != 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(isContact))
        {
            return SafeReadInt64(isContact) != 0;
        }

        if (!string.IsNullOrWhiteSpace(contactType))
        {
            return (SafeReadInt64(contactType) & 1) == 1;
        }

        return true;
    }

    private static string FirstUseful(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("wxid_", StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    private static bool IsRealFriendId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase)) return false;
        if (id.Contains('@')) return false;
        if (id.All(char.IsDigit)) return false;
        if (id.StartsWith("gh_", StringComparison.OrdinalIgnoreCase)) return false;
        if (id.Equals("weixin", StringComparison.OrdinalIgnoreCase)) return false;
        if (id.Equals("filehelper", StringComparison.OrdinalIgnoreCase)) return false;
        if (id.Equals("floatbottle", StringComparison.OrdinalIgnoreCase)) return false;
        if (id.StartsWith("medianote", StringComparison.OrdinalIgnoreCase)) return false;
        return id.StartsWith("wxid_", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("wx_", StringComparison.OrdinalIgnoreCase)
            || id.Length > 2;
    }

    private static string? FindWeChatUserDir()
    {
        var roots = new List<string>();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            roots.AddRange(FindLikelyRootsUnder(Path.Combine(profile, "Documents"), maxDepth: 3));
            roots.AddRange(FindLikelyRootsUnder(Path.Combine(profile, "Desktop"), maxDepth: 2));
            roots.AddRange(FindLikelyRootsUnder(Path.Combine(profile, "Downloads"), maxDepth: 2));
            roots.AddRange(FindLikelyRootsUnder(Path.Combine(profile, "OneDrive"), maxDepth: 4));
            roots.AddRange(FindLikelyRootsUnder(Path.Combine(profile, "OneDrive", "Documents"), maxDepth: 3));
            roots.AddRange(FindLikelyRootsUnder(Path.Combine(profile, "AppData", "Roaming", "Tencent"), maxDepth: 4));
            roots.AddRange(FindLikelyRootsUnder(Path.Combine(profile, "AppData", "Local", "Tencent"), maxDepth: 4));
            roots.Add(Path.Combine(profile, "Documents", "xwechat_files"));
            roots.Add(Path.Combine(profile, "Documents", "WeChat Files"));
            roots.Add(Path.Combine(profile, "Documents", "weixinchat", "xwechat_files"));
            roots.Add(Path.Combine(profile, "Documents", "weixinchat", "WeChat Files"));
            roots.Add(Path.Combine(profile, "xwechat_files"));
            roots.Add(Path.Combine(profile, "AppData", "Roaming", "Tencent", "xwechat"));
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var root = drive.RootDirectory.FullName;
            roots.AddRange(FindLikelyRootsUnder(root, maxDepth: 2));
            roots.Add(Path.Combine(root, "xwechat_files"));
            roots.Add(Path.Combine(root, "WeChat Files"));
            roots.Add(Path.Combine(root, "微信文件"));
            roots.Add(Path.Combine(root, "weixinchat", "xwechat_files"));
            roots.Add(Path.Combine(root, "weixinchat", "WeChat Files"));
            var users = Path.Combine(root, "Users");
            if (!Directory.Exists(users)) continue;
            foreach (var user in Directory.EnumerateDirectories(users))
            {
                roots.AddRange(FindLikelyRootsUnder(Path.Combine(user, "Documents"), maxDepth: 3));
                roots.Add(Path.Combine(user, "Documents", "xwechat_files"));
                roots.Add(Path.Combine(user, "Documents", "WeChat Files"));
                roots.Add(Path.Combine(user, "Documents", "weixinchat", "xwechat_files"));
                roots.Add(Path.Combine(user, "Documents", "weixinchat", "WeChat Files"));
                roots.Add(Path.Combine(user, "xwechat_files"));
                roots.Add(Path.Combine(user, "AppData", "Roaming", "Tencent", "xwechat"));
            }
        }

        return roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .SelectMany(FindUserDirsUnderRoot)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "db_storage", "message", "message_0.db")))
            .FirstOrDefault();
    }

    private static IEnumerable<string> FindLikelyRootsUnder(string root, int maxDepth)
    {
        if (!Directory.Exists(root) || maxDepth < 0)
        {
            yield break;
        }

        var name = Path.GetFileName(root);
        if (name.Equals("xwechat_files", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("WeChat Files", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("微信文件", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(Path.Combine(root, "db_storage", "message", "message_0.db")))
        {
            yield return root;
        }

        if (maxDepth == 0)
        {
            yield break;
        }

        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(root); }
        catch { yield break; }

        foreach (var child in children)
        {
            foreach (var found in FindLikelyRootsUnder(child, maxDepth - 1))
            {
                yield return found;
            }
        }
    }

    private static IEnumerable<string> FindUserDirsUnderRoot(string root)
    {
        if (File.Exists(Path.Combine(root, "db_storage", "message", "message_0.db")))
        {
            yield return root;
        }

        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(root); }
        catch { yield break; }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || name.Equals("all_users", StringComparison.OrdinalIgnoreCase) || name.Equals("Backup", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (File.Exists(Path.Combine(child, "db_storage", "message", "message_0.db")))
            {
                yield return child;
            }
        }
    }

    private static Dictionary<string, string> ExtractKeysFromMemory(string dbStorageDir)
    {
        var dbFiles = Directory.EnumerateFiles(dbStorageDir, "*.db", SearchOption.AllDirectories)
            .Where(p => new FileInfo(p).Length >= PageSize)
            .Select(p => new DbFile(p, ReadPage(p)))
            .Where(f => !f.Page.Take(16).SequenceEqual(SqliteHeader))
            .ToList();
        var remaining = dbFiles.Select(f => f.SaltHex).ToHashSet();
        var keyMap = new Dictionary<string, string>();

        foreach (var process in Process.GetProcessesByName("Weixin").OrderByDescending(SafeWorkingSet))
        {
            if (remaining.Count == 0) break;
            var handle = OpenProcess(0x0010 | 0x0400, false, process.Id);
            if (handle == IntPtr.Zero) continue;
            try
            {
                foreach (var region in EnumerateReadableRegions(handle))
                {
                    var buffer = new byte[region.Size];
                    if (!ReadProcessMemory(handle, region.BaseAddress, buffer, buffer.Length, out var read) || read.ToInt64() <= 0)
                    {
                        continue;
                    }
                    var text = Encoding.ASCII.GetString(buffer, 0, (int)read);
                    foreach (Match match in Regex.Matches(text, "x'([0-9a-fA-F]{64,192})'"))
                    {
                        var hex = match.Groups[1].Value;
                        var keyHex = hex[..64].ToLowerInvariant();
                        var saltHex = hex.Length == 96 ? hex[64..].ToLowerInvariant() : null;
                        var key = Convert.FromHexString(keyHex);
                        foreach (var db in dbFiles)
                        {
                            if (!remaining.Contains(db.SaltHex)) continue;
                            if (saltHex is not null && saltHex != db.SaltHex) continue;
                            if (!VerifyKey(key, db.Page)) continue;
                            keyMap[db.SaltHex] = keyHex;
                            remaining.Remove(db.SaltHex);
                        }
                    }
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        return keyMap;
    }

    private static byte[] ReadPage(string path)
    {
        using var fs = OpenSharedRead(path);
        var page = new byte[PageSize];
        _ = fs.Read(page, 0, page.Length);
        return page;
    }

    private static bool VerifyKey(byte[] key, byte[] page)
    {
        var salt = page[..SaltSize];
        var macSalt = salt.Select(b => (byte)(b ^ 0x3A)).ToArray();
        var macKey = Rfc2898DeriveBytes.Pbkdf2(key, macSalt, 2, HashAlgorithmName.SHA512, KeySize);
        var hmacData = page[SaltSize..(PageSize - ReserveSize + 16)];
        var stored = page[(PageSize - 64)..PageSize];
        using var hmac = new HMACSHA512(macKey);
        hmac.TransformBlock(hmacData, 0, hmacData.Length, null, 0);
        var pageNo = BitConverter.GetBytes(1);
        hmac.TransformFinalBlock(pageNo, 0, pageNo.Length);
        return CryptographicOperations.FixedTimeEquals(hmac.Hash!, stored);
    }

    private static string DecryptDatabase(string dbPath, string keyHex)
    {
        var raw = ReadAllShared(dbPath);
        var tmp = Path.Combine(Path.GetTempPath(), $"wechat-summary-{Guid.NewGuid():N}.db");
        if (raw.Take(16).SequenceEqual(SqliteHeader))
        {
            File.WriteAllBytes(tmp, raw);
            return tmp;
        }

        var key = Convert.FromHexString(keyHex);
        using var output = File.Create(tmp);
        for (var offset = 0; offset + PageSize <= raw.Length; offset += PageSize)
        {
            var pageNo = offset / PageSize + 1;
            var page = raw[offset..(offset + PageSize)];
            var iv = page[(PageSize - ReserveSize)..(PageSize - ReserveSize + 16)];
            var encrypted = pageNo == 1 ? page[SaltSize..(PageSize - ReserveSize)] : page[..(PageSize - ReserveSize)];
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            aes.IV = iv;
            var decrypted = aes.CreateDecryptor().TransformFinalBlock(encrypted, 0, encrypted.Length);
            var outPage = new byte[PageSize];
            if (pageNo == 1)
            {
                Buffer.BlockCopy(SqliteHeader, 0, outPage, 0, SqliteHeader.Length);
                Buffer.BlockCopy(decrypted, 0, outPage, SqliteHeader.Length, Math.Min(decrypted.Length, PageSize - ReserveSize - SaltSize));
            }
            else
            {
                Buffer.BlockCopy(decrypted, 0, outPage, 0, Math.Min(decrypted.Length, PageSize - ReserveSize));
            }
            output.Write(outPage);
        }
        return tmp;
    }

    private IEnumerable<MessageRow> QueryMessageRows(string table, DateTime start, DateTime end, string? keyword, string conversationId)
    {
        var hasRealSender = HasColumn(table, "real_sender_id");
        var columns = ListColumns(_messageConnection!, table);
        var directionColumn = PickExactColumn(columns, "is_sender", "isSend", "is_send", "issend", "is_from_me", "from_me", "fromMe");
        var isChatRoom = conversationId.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase);
        var idNameMap = hasRealSender ? LoadIdNameMap() : [];
        var (startValue, endValue) = TimeBounds(table, start, end);
        using var cmd = _messageConnection!.CreateCommand();
        var selectColumns = new List<string> { "create_time", "local_type", "message_content" };
        if (hasRealSender) selectColumns.Add("real_sender_id");
        if (directionColumn is not null) selectColumns.Add(directionColumn);
        cmd.CommandText = $"SELECT {string.Join(", ", selectColumns.Select(Quote))} FROM \"{table}\" WHERE create_time >= $start AND create_time <= $end ORDER BY create_time ASC";
        cmd.Parameters.AddWithValue("$start", startValue);
        cmd.Parameters.AddWithValue("$end", endValue);
        using var reader = cmd.ExecuteReader();
        var rows = new List<MessageRow>();
        while (reader.Read())
        {
            try
            {
                var rawTs = SafeReadInt64(reader.GetValue(0));
                var localType = reader.IsDBNull(1) ? 0 : SafeReadInt32(reader.GetValue(1));
                var content = DecodeDbValue(reader.GetValue(2));
                var realSenderIndex = hasRealSender ? 3 : -1;
                var directionIndex = directionColumn is null ? -1 : hasRealSender ? 4 : 3;
                var realSender = realSenderIndex >= 0 && !reader.IsDBNull(realSenderIndex) ? reader.GetValue(realSenderIndex)?.ToString() : null;
                var isOutgoing = directionIndex >= 0 && !reader.IsDBNull(directionIndex) && IsOutgoingValue(reader.GetValue(directionIndex));
                var (sender, bodyRaw) = SenderAndBody(content);
                var resolved = ResolveRealSender(realSender, idNameMap);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    if (isChatRoom && !IsSameWeChatId(sender, resolved))
                    {
                        AddChatroomMemberDisplayName(resolved, sender);
                    }
                    sender = resolved;
                }
                if (isChatRoom && (isOutgoing || IsSelfSender(sender)))
                {
                    sender = "我/本机";
                }
                if (isChatRoom && string.IsNullOrWhiteSpace(sender))
                {
                    sender = "未知成员";
                }
                if (!isChatRoom)
                {
                    sender = directionColumn is null
                        ? IsSameWeChatId(sender, conversationId) ? conversationId : "我/本机"
                        : isOutgoing ? "我/本机" : conversationId;
                }
                var body = FormatMessageBody(localType, bodyRaw);
                if (string.IsNullOrWhiteSpace(body)) continue;
                if (!string.IsNullOrWhiteSpace(keyword) && localType != 3 &&
                    !body.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                    !sender.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (TryDisplayTime(rawTs, out var time))
                {
                    rows.Add(new MessageRow(time, sender, body, MessageTypeLabel(localType), localType, bodyRaw));
                }
            }
            catch
            {
                // A few WeChat system rows can contain malformed timestamps or payloads.
                // Skip the row instead of aborting the whole summary.
            }
        }
        return rows;
    }

    private bool TableExists(string table)
    {
        using var cmd = _messageConnection!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private int CountRows(string table)
    {
        if (!TableExists(table)) return 0;
        using var cmd = _messageConnection!.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private bool HasColumn(string table, string column)
    {
        using var cmd = _messageConnection!.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string CleanContactValue(string value)
    {
        return (value ?? string.Empty)
            .Replace("\0", "")
            .Replace("\u200B", "")
            .Replace("\uFEFF", "")
            .Trim();
    }

    private Dictionary<string, string> LoadIdNameMap()
    {
        var map = new Dictionary<string, string>();
        using var cmd = _messageConnection!.CreateCommand();
        cmd.CommandText = "SELECT rowid, user_name FROM Name2Id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetValue(0).ToString() ?? ""] = reader.GetString(1);
        }
        return map;
    }

    private (long Start, long End) TimeBounds(string table, DateTime start, DateTime end)
    {
        using var cmd = _messageConnection!.CreateCommand();
        cmd.CommandText = $"SELECT MAX(create_time) FROM \"{table}\"";
        var max = SafeReadInt64(cmd.ExecuteScalar() ?? 0);
        var s = new DateTimeOffset(start).ToUnixTimeSeconds();
        var e = new DateTimeOffset(end).ToUnixTimeSeconds();
        return max > 100000000000 ? (s * 1000, e * 1000) : (s, e);
    }

    private static bool TryDisplayTime(long rawTs, out DateTime time)
    {
        time = default;
        if (rawTs <= 0)
        {
            return false;
        }

        var seconds = rawTs switch
        {
            > 10000000000000000 => rawTs / 10000000,
            > 100000000000000 => rawTs / 1000000,
            > 100000000000 => rawTs / 1000,
            _ => rawTs
        };

        try
        {
            time = DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
            return time.Year is >= 2000 and <= 2100;
        }
        catch
        {
            return false;
        }
    }

    private static long SafeReadInt64(object value)
    {
        return value switch
        {
            null => 0,
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            ulong ul when ul <= long.MaxValue => (long)ul,
            uint ui => ui,
            string text when long.TryParse(text, out var parsed) => parsed,
            _ => 0
        };
    }

    private static int SafeReadInt32(object value)
    {
        var number = SafeReadInt64(value);
        if (number > int.MaxValue) return int.MaxValue;
        if (number < int.MinValue) return int.MinValue;
        return (int)number;
    }

    private static bool IsOutgoingValue(object value)
    {
        return value switch
        {
            bool b => b,
            byte b => b != 0,
            short s => s != 0,
            int i => i != 0,
            long l => l != 0,
            string text when bool.TryParse(text, out var parsed) => parsed,
            string text when long.TryParse(text, out var parsed) => parsed != 0,
            _ => false
        };
    }

    private static string GetConversationTable(string userName)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(userName));
        return "Msg_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeChatroomId(string id) => id.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) ? id : $"{id}@chatroom";

    private static bool IsSameWeChatId(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSelfSender(string sender)
    {
        if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(_selfWechatId))
        {
            return false;
        }

        if (IsSameWeChatId(sender, _selfWechatId))
        {
            return true;
        }

        if (_contactDisplayNames.TryGetValue(_selfWechatId, out var selfName) &&
            !string.IsNullOrWhiteSpace(selfName) &&
            sender.Trim().Equals(selfName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _chatroomMemberDisplayNames.TryGetValue(_selfWechatId, out var selfMemberName) &&
               !string.IsNullOrWhiteSpace(selfMemberName) &&
               sender.Trim().Equals(selfMemberName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveDisplayName(string id)
    {
        if (id.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) &&
            _chatroomDisplayNames.TryGetValue(id, out var chatroomName) &&
            !string.IsNullOrWhiteSpace(chatroomName))
        {
            return chatroomName;
        }
        if (!id.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) &&
            _contactDisplayNames.TryGetValue(id, out var contactName) &&
            !string.IsNullOrWhiteSpace(contactName))
        {
            return contactName;
        }
        if (!id.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) &&
            _chatroomMemberDisplayNames.TryGetValue(id, out var memberName) &&
            !string.IsNullOrWhiteSpace(memberName))
        {
            return memberName;
        }
        if (id == "我/本机") return id;
        if (id.All(char.IsDigit)) return $"未知成员 {id}";
        return id.Replace("@chatroom", "");
    }

    private static (string Sender, string Body) SenderAndBody(string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        var idx = normalized.IndexOf(":\n", StringComparison.Ordinal);
        if (idx > 0)
        {
            return (normalized[..idx].Trim(), normalized[(idx + 2)..].Trim());
        }

        var firstLineBreak = normalized.IndexOf('\n');
        var firstLine = firstLineBreak > 0 ? normalized[..firstLineBreak].Trim() : "";
        if (firstLine.Length is > 0 and <= 80 && (firstLine.EndsWith(':') || firstLine.EndsWith('：')))
        {
            return (firstLine.TrimEnd(':', '：').Trim(), normalized[(firstLineBreak + 1)..].Trim());
        }

        return ("", normalized.Trim());
    }

    private static string ResolveRealSender(string? realSender, Dictionary<string, string> map)
    {
        if (string.IsNullOrWhiteSpace(realSender)) return "";
        var raw = realSender.Trim();
        if (map.TryGetValue(raw, out var mapped)) return mapped;
        return raw.All(char.IsDigit) ? "" : raw;
    }

    private static string DecodeDbValue(object value) => value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : value?.ToString() ?? "";

    private async Task<string> EnrichMessageBodyAsync(MessageRow row, CancellationToken cancellationToken)
    {
        if (row.LocalType != 3)
        {
            return row.Body;
        }

        var imagePath = FindImagePath(row.RawBody, row.Time);
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            WriteOcrDiagnostic($"image-not-found time={row.Time:yyyy-MM-dd HH:mm:ss} raw={Truncate(row.RawBody, 220)}");
            return $"{row.Body}（本地图片未定位，暂未 OCR）";
        }

        if (!_ocrService.IsAvailable)
        {
            WriteOcrDiagnostic($"ocr-unavailable status={_ocrService.Status} image={imagePath}");
            return $"{row.Body}（已找到本地图片，但本机 OCR 服务不可用）";
        }

        var ocrText = await _ocrService.RecognizeAsync(imagePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            WriteOcrDiagnostic($"ocr-empty image={imagePath}");
            return $"{row.Body}（已找到本地图片，但未识别出文字）";
        }

        WriteOcrDiagnostic($"ocr-ok image={imagePath} chars={ocrText.Length}");
        return $"{row.Body}\n[图片 OCR]\n{Truncate(ocrText, 1200)}";
    }

    private static void WriteOcrDiagnostic(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "chatbrief-ocr-diagnostics.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private string? FindImagePath(string raw, DateTime messageTime)
    {
        foreach (var candidate in ExtractImagePathCandidates(raw))
        {
            var fullPath = Path.IsPathRooted(candidate)
                ? candidate
                : _wechatUserDir is null ? candidate : Path.Combine(_wechatUserDir, candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return FindClosestImageByTime(messageTime);
    }

    private IEnumerable<string> ExtractImagePathCandidates(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        foreach (var field in new[] { "path", "filepath", "filePath", "thumbpath", "thumbPath", "cdnthumbpath", "cdnthumbPath", "md5" })
        {
            var value = ExtractXml(raw, field);
            if (LooksLikeImagePath(value))
            {
                yield return value!;
            }
        }

        foreach (Match match in Regex.Matches(raw, @"[A-Za-z]:\\[^<>'""]+\.(?:jpg|jpeg|png|bmp|webp|gif|dat)", RegexOptions.IgnoreCase))
        {
            yield return match.Value;
        }

        foreach (Match match in Regex.Matches(raw, @"(?:FileStorage|MsgAttach|Image|image|Thumb|thumb)[^<>'""]+\.(?:jpg|jpeg|png|bmp|webp|gif|dat)", RegexOptions.IgnoreCase))
        {
            yield return match.Value.Replace('/', Path.DirectorySeparatorChar);
        }
    }

    private static bool LooksLikeImagePath(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               Regex.IsMatch(value, @"\.(jpg|jpeg|png|bmp|webp|gif|dat)$", RegexOptions.IgnoreCase);
    }

    private string? FindClosestImageByTime(DateTime messageTime)
    {
        if (_wechatUserDir is null)
        {
            return null;
        }

        _imageFileIndex ??= BuildImageFileIndex(_wechatUserDir);
        if (_imageFileIndex.Count == 0)
        {
            return null;
        }

        var target = messageTime.ToUniversalTime();
        return _imageFileIndex
            .Where(file => Math.Abs((file.LastWriteTimeUtc - target).TotalMinutes) <= 30)
            .OrderBy(file => Math.Abs((file.LastWriteTimeUtc - target).TotalSeconds))
            .Select(file => file.Path)
            .FirstOrDefault();
    }

    private static List<ImageFileCandidate> BuildImageFileIndex(string root)
    {
        var results = new List<ImageFileCandidate>();
        foreach (var directory in CandidateImageDirectories(root).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in EnumerateImageFiles(directory).Take(3000))
            {
                try
                {
                    results.Add(new ImageFileCandidate(file, File.GetLastWriteTimeUtc(file)));
                }
                catch
                {
                    // Ignore inaccessible files.
                }
            }
        }

        return results
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(3000)
            .ToList();
    }

    private static IEnumerable<string> CandidateImageDirectories(string root)
    {
        yield return Path.Combine(root, "FileStorage", "Image");
        yield return Path.Combine(root, "FileStorage", "MsgAttach");
        yield return Path.Combine(root, "msg", "image");
        yield return Path.Combine(root, "msg", "video");
        yield return Path.Combine(root, "msg", "file");
        yield return Path.Combine(root, "resource");
        yield return Path.Combine(root, "cache");
        yield return Path.Combine(root, "MsgAttach");
        yield return Path.Combine(root, "Image");
        yield return root;
    }

    private static IEnumerable<string> EnumerateImageFiles(string root)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif"
        };

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (extensions.Contains(Path.GetExtension(file)))
            {
                yield return file;
            }
        }
    }

    private static string FormatMessageBody(int localType, string raw)
    {
        if (localType == 1)
        {
            return string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('<') ? "" : raw.Trim();
        }
        var label = MessageTypeLabel(localType);
        var clean = CleanXmlText(raw);
        if (localType == 10000)
        {
            return string.IsNullOrWhiteSpace(clean) ? $"[{label}]" : $"[{label}] {Truncate(clean, 160)}";
        }
        var file = ExtractXml(raw, "filename") ?? ExtractXml(raw, "fileName") ?? ExtractXml(raw, "title");
        var title = ExtractXml(raw, "title") ?? ExtractXml(raw, "des") ?? ExtractXml(raw, "appname");
        var url = Regex.Match(raw ?? "", "https?://[^\\s<>'\"]+", RegexOptions.IgnoreCase).Value;
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(file)) details.Add(Truncate(file, 120));
        else if (!string.IsNullOrWhiteSpace(title)) details.Add(Truncate(title, 120));
        else if (!string.IsNullOrWhiteSpace(clean) && clean.Length <= 120) details.Add(clean);
        if (!string.IsNullOrWhiteSpace(url)) details.Add(Truncate(url, 160));
        return string.Join(" ", new[] { $"[{label}]" }.Concat(details)).Trim();
    }

    private static string MessageTypeLabel(int localType) => localType switch
    {
        3 => "图片",
        34 => "语音",
        43 => "视频",
        47 => "表情",
        48 => "位置",
        49 => "文件/链接",
        10000 => "系统消息",
        _ => "非文本消息"
    };

    private static string? ExtractXml(string text, string field)
    {
        var match = Regex.Match(text ?? "", $"<{field}[^>]*>(.*?)</{field}>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? CleanXmlText(match.Groups[1].Value) : null;
    }

    private static string CleanXmlText(string text)
    {
        text = System.Net.WebUtility.HtmlDecode(text ?? "");
        text = Regex.Replace(text, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    private static long SafeWorkingSet(Process process)
    {
        try { return process.WorkingSet64; } catch { return 0; }
    }

    private static FileStream OpenSharedRead(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }

    private static byte[] ReadShared(string path, int length)
    {
        using var fs = OpenSharedRead(path);
        var buffer = new byte[length];
        var read = fs.Read(buffer, 0, length);
        return read == length ? buffer : buffer[..read];
    }

    private static byte[] ReadAllShared(string path)
    {
        using var fs = OpenSharedRead(path);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static IEnumerable<MemoryRegion> EnumerateReadableRegions(IntPtr handle)
    {
        var address = 0L;
        while (address < 0x7FFFFFFFFFFF)
        {
            if (VirtualQueryEx(handle, new IntPtr(address), out var mbi, (uint)Marshal.SizeOf<MemoryBasicInformation>()) == 0) break;
            var size = mbi.RegionSize.ToInt64();
            if (mbi.State == MemCommit && ReadableProtects.Contains(mbi.Protect) && size > 0 && size < 500L * 1024 * 1024)
            {
                yield return new MemoryRegion(mbi.BaseAddress, (int)size);
            }
            var next = mbi.BaseAddress.ToInt64() + size;
            if (next <= address) break;
            address = next;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesRead);

    [DllImport("kernel32.dll")]
    private static extern int VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation buffer, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private sealed record DbFile(string Path, byte[] Page)
    {
        public string SaltHex { get; } = Convert.ToHexString(Page[..SaltSize]).ToLowerInvariant();
    }

    private sealed record MemoryRegion(IntPtr BaseAddress, int Size);
    private sealed record MessageRow(DateTime Time, string Sender, string Body, string MessageType, int LocalType, string RawBody);

    private sealed record ImageFileCandidate(string Path, DateTime LastWriteTimeUtc);
    private sealed record ContactInfo(string Id, string DisplayName, string Remark, string NickName, string Alias);
    private enum ContactAcceptResult
    {
        Accepted,
        GroupCache,
        NotFriend
    }
}
