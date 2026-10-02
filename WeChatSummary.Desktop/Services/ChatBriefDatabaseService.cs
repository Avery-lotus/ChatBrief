using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WeChatSummary.Desktop.Models;

namespace WeChatSummary.Desktop.Services;

public sealed class ChatBriefDatabaseService
{
    private static readonly TimeSpan SegmentGap = TimeSpan.FromMinutes(30);
    private readonly string _dbPath;

    public ChatBriefDatabaseService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "WeChatSummary");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "ChatBrief.db");
    }

    public string DatabasePath => _dbPath;

    public void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        Execute(connection, tx, """
            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
        Execute(connection, tx, """
            CREATE TABLE IF NOT EXISTS conversation (
                id TEXT PRIMARY KEY,
                source TEXT NOT NULL,
                type TEXT NOT NULL,
                display_name TEXT NOT NULL,
                message_count INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );
            """);
        Execute(connection, tx, """
            CREATE TABLE IF NOT EXISTS member (
                id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                remark TEXT,
                nick_name TEXT,
                updated_at TEXT NOT NULL
            );
            """);
        Execute(connection, tx, """
            CREATE TABLE IF NOT EXISTS message (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id TEXT NOT NULL,
                source_message_key TEXT NOT NULL,
                sender_id TEXT NOT NULL,
                sender_name TEXT NOT NULL,
                sent_at TEXT NOT NULL,
                message_type TEXT NOT NULL,
                content TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(conversation_id, source_message_key)
            );
            """);
        Execute(connection, tx, """
            CREATE TABLE IF NOT EXISTS segment (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id TEXT NOT NULL,
                start_at TEXT NOT NULL,
                end_at TEXT NOT NULL,
                message_count INTEGER NOT NULL DEFAULT 0,
                summary TEXT,
                summary_message_count INTEGER
            );
            """);
        Execute(connection, tx, """
            CREATE TABLE IF NOT EXISTS message_context (
                message_id INTEGER PRIMARY KEY,
                segment_id INTEGER NOT NULL
            );
            """);
        Execute(connection, tx, "CREATE INDEX IF NOT EXISTS idx_message_conversation_time ON message(conversation_id, sent_at);");
        Execute(connection, tx, "CREATE INDEX IF NOT EXISTS idx_message_sender ON message(sender_id);");
        Execute(connection, tx, "CREATE INDEX IF NOT EXISTS idx_message_content_hash ON message(content_hash);");
        Execute(connection, tx, "CREATE INDEX IF NOT EXISTS idx_segment_conversation_time ON segment(conversation_id, start_at);");
        UpsertMeta(connection, tx, "schema_version", "1");
        tx.Commit();
    }

    public void UpsertCatalog(IEnumerable<ChatRoom> rooms, IEnumerable<FriendContact> friends)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        foreach (var room in rooms)
        {
            UpsertConversation(connection, tx, room.Id, "wechat", "group", room.DisplayName, room.MessageCount);
        }

        foreach (var friend in friends)
        {
            UpsertConversation(connection, tx, friend.Id, "wechat", "private", friend.DisplayName, 0);
            UpsertMember(connection, tx, friend.Id, friend.DisplayName, friend.Remark, friend.NickName);
        }

        UpsertMeta(connection, tx, "last_catalog_sync_at", DateTime.UtcNow.ToString("O"));
        tx.Commit();
    }

    public void SyncConversationMessages(string conversationId, string type, string displayName, IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        EnsureSchema();
        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        UpsertConversation(connection, tx, conversationId, "wechat", type, displayName, messages.Count);

        foreach (var message in messages)
        {
            UpsertMember(connection, tx, message.SenderId, message.SenderDisplayName, null, null);
            UpsertMessage(connection, tx, conversationId, message);
        }

        RebuildSegments(connection, tx, conversationId);
        UpsertMeta(connection, tx, "last_message_sync_at", DateTime.UtcNow.ToString("O"));
        tx.Commit();
    }

    public IReadOnlyList<ChatMessage> QueryMessages(DateTime start, DateTime end, string? conversationId = null, string? keyword = null)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        var where = new List<string> { "sent_at >= $start", "sent_at <= $end" };
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            where.Add("conversation_id = $conversationId");
            cmd.Parameters.AddWithValue("$conversationId", conversationId);
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            where.Add("(content LIKE $keyword OR sender_name LIKE $keyword)");
            cmd.Parameters.AddWithValue("$keyword", $"%{keyword.Trim()}%");
        }

        cmd.CommandText = $"""
            SELECT sent_at, sender_id, sender_name, content, message_type
            FROM message
            WHERE {string.Join(" AND ", where)}
            ORDER BY sent_at ASC, id ASC
            LIMIT 5000;
            """;
        using var reader = cmd.ExecuteReader();
        var messages = new List<ChatMessage>();
        while (reader.Read())
        {
            messages.Add(new ChatMessage(
                DateTime.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return messages;
    }

    public IReadOnlyList<ChatMessage> QueryScopedMessages(DateTime start, DateTime end, string scopeKey)
    {
        if (scopeKey.StartsWith("group:", StringComparison.OrdinalIgnoreCase) || scopeKey.StartsWith("friend:", StringComparison.OrdinalIgnoreCase))
        {
            return QueryMessages(start, end, ConversationIdFromScope(scopeKey));
        }

        EnsureSchema();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        var where = BuildScopeWhere(cmd, scopeKey, "m");
        cmd.CommandText = $"""
            SELECT m.sent_at, m.sender_id, m.sender_name, m.content, m.message_type
            FROM message m
            JOIN conversation c ON c.id = m.conversation_id
            WHERE m.sent_at >= $start AND m.sent_at <= $end {where}
            ORDER BY m.sent_at ASC, m.id ASC
            LIMIT 5000;
            """;
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));
        using var reader = cmd.ExecuteReader();
        var messages = new List<ChatMessage>();
        while (reader.Read())
        {
            messages.Add(new ChatMessage(
                DateTime.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return messages;
    }

    public IReadOnlyList<InsightRankItem> RankSpeakers(DateTime start, DateTime end, string? conversationId = null, string? keyword = null, int limit = 8)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        var conversationFilter = string.IsNullOrWhiteSpace(conversationId) ? "" : "AND conversation_id = $conversationId";
        cmd.CommandText = $"""
            SELECT sender_name, COUNT(*) AS count
            FROM message
            WHERE sent_at >= $start AND sent_at <= $end
              {conversationFilter}
              AND ($keyword = '' OR content LIKE $likeKeyword OR sender_name LIKE $likeKeyword)
            GROUP BY sender_name
            ORDER BY count DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            cmd.Parameters.AddWithValue("$conversationId", conversationId);
        }
        var trimmed = keyword?.Trim() ?? "";
        cmd.Parameters.AddWithValue("$keyword", trimmed);
        cmd.Parameters.AddWithValue("$likeKeyword", $"%{trimmed}%");
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadRank(cmd);
    }

    public IReadOnlyList<InsightRankItem> RankConversations(DateTime start, DateTime end, string? keyword = null, int limit = 8)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.display_name, COUNT(*) AS count
            FROM message m
            JOIN conversation c ON c.id = m.conversation_id
            WHERE m.sent_at >= $start AND m.sent_at <= $end
              AND ($keyword = '' OR m.content LIKE $likeKeyword OR m.sender_name LIKE $likeKeyword)
            GROUP BY c.id, c.display_name
            ORDER BY count DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));
        var trimmed = keyword?.Trim() ?? "";
        cmd.Parameters.AddWithValue("$keyword", trimmed);
        cmd.Parameters.AddWithValue("$likeKeyword", $"%{trimmed}%");
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var result = new List<InsightRankItem>();
        while (reader.Read())
        {
            result.Add(new InsightRankItem(reader.GetString(0), reader.GetInt32(1)));
        }
        return result;
    }

    public InsightOverviewStats GetOverviewStats(DateTime start, DateTime end, string scopeKey, IEnumerable<string> selfNames)
    {
        var messages = QueryScopedMessages(start, end, scopeKey);

        var filteredSelfNames = selfNames.Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var topSpeaker = messages
            .Where(message => !filteredSelfNames.Contains(message.SenderDisplayName))
            .GroupBy(message => message.SenderDisplayName)
            .Select(group => new { Name = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .FirstOrDefault();
        var peakHour = messages
            .GroupBy(message => message.Time.Hour)
            .Select(group => new { Hour = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .FirstOrDefault();
        var objectCount = CountObjects(start, end, scopeKey);
        return new InsightOverviewStats(
            messages.Count,
            objectCount,
            topSpeaker?.Name ?? "-",
            topSpeaker?.Count ?? 0,
            peakHour?.Hour ?? -1,
            peakHour?.Count ?? 0);
    }

    public IReadOnlyList<InsightDailyCount> CountByDay(DateTime start, DateTime end, string scopeKey)
    {
        var raw = QueryDatePart("date(sent_at)", start, end, scopeKey);
        return raw.Select(item => new InsightDailyCount(DateTime.Parse(item.Name), item.Count)).ToList();
    }

    public IReadOnlyList<InsightHourCount> CountByHour(DateTime start, DateTime end, string scopeKey)
    {
        var raw = QueryDatePart("strftime('%H', sent_at)", start, end, scopeKey);
        return raw.Select(item => new InsightHourCount(int.Parse(item.Name), item.Count)).ToList();
    }

    public IReadOnlyList<InsightRankItem> RankMessageTypes(DateTime start, DateTime end, string scopeKey, int limit = 5)
    {
        return QueryScopedRank("message_type", start, end, scopeKey, limit);
    }

    public IReadOnlyList<InsightRankItem> RankObjects(DateTime start, DateTime end, string scopeKey, int limit = 8)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        var where = BuildScopeWhere(cmd, scopeKey, "m");
        cmd.CommandText = $"""
            SELECT c.display_name, COUNT(*) AS count
            FROM message m
            JOIN conversation c ON c.id = m.conversation_id
            WHERE m.sent_at >= $start AND m.sent_at <= $end {where}
            GROUP BY c.id, c.display_name
            ORDER BY count DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadRank(cmd);
    }

    public IReadOnlyList<InsightRankItem> RankRelations(DateTime start, DateTime end, string scopeKey, string relationMode, int limit = 7)
    {
        if (relationMode == "好友")
        {
            return RankObjects(start, end, scopeKey == "groups" ? "friends" : scopeKey, limit)
                .Select(item => new InsightRankItem($"我/本机 ↔ {item.Name}", item.Count))
                .ToList();
        }

        return RankObjects(start, end, scopeKey == "friends" ? "groups" : scopeKey, limit);
    }

    public NaturalInsightAnswer AskLocalInsight(string question, DateTime now)
    {
        EnsureSchema();
        var (start, end) = ResolveQuestionRange(question, now);
        var keyword = ExtractQuestionKeyword(question);
        var asksWho = question.Contains('谁') || question.Contains("发言") || question.Contains("提到");
        var items = asksWho
            ? RankSpeakers(start, end, keyword: keyword, limit: 6)
            : RankConversations(start, end, keyword: keyword, limit: 6);
        var target = asksWho ? "发言人" : "对象";
        var condition = string.IsNullOrWhiteSpace(keyword) ? "" : $"，关键词「{keyword}」";
        var answer = items.Count == 0
            ? $"在 {start:MM/dd} - {end:MM/dd}{condition} 没有找到可统计的消息。"
            : $"在 {start:MM/dd} - {end:MM/dd}{condition}，最高频的{target}是「{items[0].Name}」，共 {items[0].Count} 条相关消息。";
        return new NaturalInsightAnswer(question, answer, items, start, end, keyword);
    }

    private IReadOnlyList<InsightRankItem> QueryRank(string prefixSql, string groupColumn, DateTime start, DateTime end, string? conversationId, string? keyword, int limit)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        var sql = new StringBuilder(prefixSql);
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            sql.AppendLine("AND conversation_id = $conversationId");
            cmd.Parameters.AddWithValue("$conversationId", conversationId);
        }
        var trimmed = keyword?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            sql.AppendLine("AND (content LIKE $keyword OR sender_name LIKE $keyword)");
            cmd.Parameters.AddWithValue("$keyword", $"%{trimmed}%");
        }
        sql.AppendLine();
        sql.AppendLine($"GROUP BY {groupColumn} ORDER BY count DESC LIMIT $limit;");
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var result = new List<InsightRankItem>();
        while (reader.Read())
        {
            result.Add(new InsightRankItem(reader.GetString(0), reader.GetInt32(1)));
        }
        return result;
    }

    private IReadOnlyList<InsightRankItem> QueryScopedRank(string column, DateTime start, DateTime end, string scopeKey, int limit)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        var where = BuildScopeWhere(cmd, scopeKey, "m");
        cmd.CommandText = $"""
            SELECT m.{column}, COUNT(*) AS count
            FROM message m
            JOIN conversation c ON c.id = m.conversation_id
            WHERE m.sent_at >= $start AND m.sent_at <= $end {where}
            GROUP BY m.{column}
            ORDER BY count DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadRank(cmd);
    }

    private IReadOnlyList<InsightRankItem> QueryDatePart(string expression, DateTime start, DateTime end, string scopeKey)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        var where = BuildScopeWhere(cmd, scopeKey, "m");
        cmd.CommandText = $"""
            SELECT {expression} AS bucket, COUNT(*) AS count
            FROM message m
            JOIN conversation c ON c.id = m.conversation_id
            WHERE m.sent_at >= $start AND m.sent_at <= $end {where}
            GROUP BY bucket
            ORDER BY bucket ASC;
            """;
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));
        return ReadRank(cmd);
    }

    private int CountObjects(DateTime start, DateTime end, string scopeKey)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        var where = BuildScopeWhere(cmd, scopeKey, "m");
        cmd.CommandText = $"""
            SELECT COUNT(DISTINCT m.conversation_id)
            FROM message m
            JOIN conversation c ON c.id = m.conversation_id
            WHERE m.sent_at >= $start AND m.sent_at <= $end {where};
            """;
        cmd.Parameters.AddWithValue("$start", start.ToString("O"));
        cmd.Parameters.AddWithValue("$end", end.ToString("O"));
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static string BuildScopeWhere(SqliteCommand cmd, string scopeKey, string messageAlias)
    {
        if (scopeKey == "groups") return "AND c.type = 'group'";
        if (scopeKey == "friends") return "AND c.type = 'private'";
        if (scopeKey.StartsWith("group:", StringComparison.OrdinalIgnoreCase) || scopeKey.StartsWith("friend:", StringComparison.OrdinalIgnoreCase))
        {
            var id = scopeKey[(scopeKey.IndexOf(':') + 1)..];
            cmd.Parameters.AddWithValue("$scopeConversationId", id);
            return $"AND {messageAlias}.conversation_id = $scopeConversationId";
        }
        return "";
    }

    private static string? ConversationIdFromScope(string scopeKey)
    {
        return scopeKey.StartsWith("group:", StringComparison.OrdinalIgnoreCase) || scopeKey.StartsWith("friend:", StringComparison.OrdinalIgnoreCase)
            ? scopeKey[(scopeKey.IndexOf(':') + 1)..]
            : null;
    }

    private static IReadOnlyList<InsightRankItem> ReadRank(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var result = new List<InsightRankItem>();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                result.Add(new InsightRankItem(reader.GetString(0), reader.GetInt32(1)));
            }
        }
        return result;
    }


    private static (DateTime Start, DateTime End) ResolveQuestionRange(string question, DateTime now)
    {
        var end = now;
        if (question.Contains("上周"))
        {
            var today = now.Date;
            var dayOfWeek = (int)today.DayOfWeek;
            var thisMonday = today.AddDays(-((dayOfWeek + 6) % 7));
            return (thisMonday.AddDays(-7), thisMonday.AddTicks(-1));
        }
        if (question.Contains("昨天"))
        {
            return (now.Date.AddDays(-1), now.Date.AddTicks(-1));
        }
        if (question.Contains("今天"))
        {
            return (now.Date, now);
        }
        if (question.Contains("30"))
        {
            return (now.AddDays(-30), end);
        }
        return (now.AddDays(-7), end);
    }

    private static string ExtractQuestionKeyword(string question)
    {
        var quoted = Regex.Match(question, "[「『\\\"'](?<keyword>[^」』\\\"']+)[」』\\\"']");
        if (quoted.Success)
        {
            return quoted.Groups["keyword"].Value.Trim();
        }

        var mention = Regex.Match(question, "(提到|关于|包含|搜索|关键词)(?<keyword>[\\u4e00-\\u9fa5A-Za-z0-9_\\-]{2,20})");
        if (mention.Success)
        {
            var keyword = mention.Groups["keyword"].Value.Trim();
            return Regex.Replace(keyword, "(最多|最高|最频繁|的人|的是)$", "");
        }

        var whoMention = Regex.Match(question, "谁[^，。？！?]*?(?<keyword>[\\u4e00-\\u9fa5A-Za-z0-9_\\-]{2,20})(最多|最高|最频繁)");
        return whoMention.Success ? whoMention.Groups["keyword"].Value.Trim() : "";
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }

    private static void UpsertConversation(SqliteConnection connection, SqliteTransaction tx, string id, string source, string type, string displayName, int messageCount)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO conversation(id, source, type, display_name, message_count, updated_at)
            VALUES ($id, $source, $type, $displayName, $messageCount, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                source = excluded.source,
                type = excluded.type,
                display_name = excluded.display_name,
                message_count = CASE WHEN excluded.message_count > conversation.message_count THEN excluded.message_count ELSE conversation.message_count END,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$displayName", displayName);
        cmd.Parameters.AddWithValue("$messageCount", messageCount);
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void UpsertMember(SqliteConnection connection, SqliteTransaction tx, string id, string displayName, string? remark, string? nickName)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO member(id, display_name, remark, nick_name, updated_at)
            VALUES ($id, $displayName, $remark, $nickName, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                remark = COALESCE(excluded.remark, member.remark),
                nick_name = COALESCE(excluded.nick_name, member.nick_name),
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$displayName", displayName);
        cmd.Parameters.AddWithValue("$remark", (object?)remark ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$nickName", (object?)nickName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void UpsertMessage(SqliteConnection connection, SqliteTransaction tx, string conversationId, ChatMessage message)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO message(conversation_id, source_message_key, sender_id, sender_name, sent_at, message_type, content, content_hash, created_at)
            VALUES ($conversationId, $sourceMessageKey, $senderId, $senderName, $sentAt, $messageType, $content, $contentHash, $createdAt)
            ON CONFLICT(conversation_id, source_message_key) DO UPDATE SET
                sender_name = excluded.sender_name,
                message_type = excluded.message_type,
                content = excluded.content,
                content_hash = excluded.content_hash;
            """;
        cmd.Parameters.AddWithValue("$conversationId", conversationId);
        cmd.Parameters.AddWithValue("$sourceMessageKey", BuildSourceMessageKey(message));
        cmd.Parameters.AddWithValue("$senderId", message.SenderId);
        cmd.Parameters.AddWithValue("$senderName", message.SenderDisplayName);
        cmd.Parameters.AddWithValue("$sentAt", message.Time.ToString("O"));
        cmd.Parameters.AddWithValue("$messageType", message.MessageType);
        cmd.Parameters.AddWithValue("$content", message.Content);
        cmd.Parameters.AddWithValue("$contentHash", Sha256(message.Content));
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void RebuildSegments(SqliteConnection connection, SqliteTransaction tx, string conversationId)
    {
        Execute(connection, tx, """
            DELETE FROM message_context
            WHERE segment_id IN (SELECT id FROM segment WHERE conversation_id = $conversationId);
            """, ("$conversationId", conversationId));
        Execute(connection, tx, "DELETE FROM segment WHERE conversation_id = $conversationId;", ("$conversationId", conversationId));

        var rows = new List<(long Id, DateTime SentAt)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id, sent_at FROM message WHERE conversation_id = $conversationId ORDER BY sent_at ASC, id ASC;";
            cmd.Parameters.AddWithValue("$conversationId", conversationId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind)));
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        var segmentMessages = new List<(long Id, DateTime SentAt)>();
        DateTime? previous = null;
        foreach (var row in rows)
        {
            if (previous is not null && row.SentAt - previous.Value > SegmentGap)
            {
                InsertSegment(connection, tx, conversationId, segmentMessages);
                segmentMessages.Clear();
            }

            segmentMessages.Add(row);
            previous = row.SentAt;
        }

        InsertSegment(connection, tx, conversationId, segmentMessages);
    }

    private static void InsertSegment(SqliteConnection connection, SqliteTransaction tx, string conversationId, IReadOnlyList<(long Id, DateTime SentAt)> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        long segmentId;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO segment(conversation_id, start_at, end_at, message_count, summary, summary_message_count)
                VALUES ($conversationId, $startAt, $endAt, $messageCount, NULL, NULL);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$conversationId", conversationId);
            cmd.Parameters.AddWithValue("$startAt", messages[0].SentAt.ToString("O"));
            cmd.Parameters.AddWithValue("$endAt", messages[^1].SentAt.ToString("O"));
            cmd.Parameters.AddWithValue("$messageCount", messages.Count);
            segmentId = (long)(cmd.ExecuteScalar() ?? 0L);
        }

        foreach (var message in messages)
        {
            Execute(
                connection,
                tx,
                "INSERT INTO message_context(message_id, segment_id) VALUES ($messageId, $segmentId);",
                ("$messageId", message.Id),
                ("$segmentId", segmentId));
        }
    }

    private static void UpsertMeta(SqliteConnection connection, SqliteTransaction tx, string key, string value)
    {
        Execute(
            connection,
            tx,
            "INSERT INTO meta(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
            ("$key", key),
            ("$value", value));
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var parameter in parameters)
        {
            cmd.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }

    private static string BuildSourceMessageKey(ChatMessage message)
    {
        return Sha256($"{message.Time.Ticks}|{message.SenderId}|{message.MessageType}|{message.Content}");
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
