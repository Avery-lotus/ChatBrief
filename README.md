# ChatBrief

ChatBrief 是面向个人本地使用的微信聊天整理工具，用于把聊天记录整理成可核对、可导出的总结与洞察。

## 当前能力

- 微信 4.1.9.57 本地数据读取
- 聊天文本导入
- 群聊与好友聚焦分析
- 同类对象对照
- 洞察统计与本地提问
- Markdown 导出

## 开发

项目基于 .NET 8 WPF。

```powershell
.\.dotnet\dotnet.exe build .\WeChatSummary.Desktop\WeChatSummary.Desktop.csproj -c Release
```

API Key 只应保存在用户本机配置中，不要提交到仓库。
