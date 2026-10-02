using System.IO;
using System.Text.Json;
using WeChatSummary.Desktop.Models;

namespace WeChatSummary.Desktop.Services;

public sealed class MemoService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _memoPath;

    public MemoService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folder = Path.Combine(appData, "WeChatSummary");
        Directory.CreateDirectory(folder);
        _memoPath = Path.Combine(folder, "memos.json");
    }

    public List<MemoEntry> Load()
    {
        if (!File.Exists(_memoPath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_memoPath);
            return JsonSerializer.Deserialize<List<MemoEntry>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<MemoEntry> memos)
    {
        var json = JsonSerializer.Serialize(memos, JsonOptions);
        File.WriteAllText(_memoPath, json);
    }
}
