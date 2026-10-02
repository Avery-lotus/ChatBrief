namespace WeChatSummary.Desktop.Models;

public sealed class AppConfig
{
    public string ApiKey { get; set; } = "";
    public string CurrentGroupTemplate { get; set; } = "项目工作追踪";
    public string CurrentPersonTemplate { get; set; } = "工作同事单人分析";
    public string VersionLabel { get; set; } = "测试版";
    public string ContactEmail { get; set; } = "aezakmiavery@gmail.com";
    public string SelfAliases { get; set; } = "";
    public string WeChatDataDirectory { get; set; } = "";
}
