using WeChatSummary.Desktop.Services;

var service = new NativeWeChatDataService();
var friends = await service.ListFriendsAsync();
Console.WriteLine($"FRIENDS={friends.Count}");
foreach (var friend in friends.Take(30))
{
    Console.WriteLine($"{friend.DisplayName} | remark={friend.Remark} | nick={friend.NickName} | id={friend.Id}");
}

var diagnosticsPath = Path.Combine(Path.GetTempPath(), "chatbrief-contact-diagnostics.txt");
Console.WriteLine($"DIAG={diagnosticsPath}");
if (File.Exists(diagnosticsPath))
{
    Console.WriteLine(File.ReadAllText(diagnosticsPath));
}
