using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using WeChatSummary.Desktop.Models;
using WeChatSummary.Desktop.Services;

namespace WeChatSummary.Desktop;

public partial class MainWindow : Window
{
    private const string CreateTemplateAction = "＋ 新建模板...";
    private static readonly Brush[] PersonHighlightBrushes =
    [
        new SolidColorBrush(Color.FromRgb(7, 193, 96)),
        new SolidColorBrush(Color.FromRgb(37, 99, 235)),
        new SolidColorBrush(Color.FromRgb(217, 119, 6)),
        new SolidColorBrush(Color.FromRgb(147, 51, 234))
    ];
    private static readonly Color[] InsightChartColors =
    [
        Color.FromRgb(20, 184, 166),
        Color.FromRgb(99, 102, 241),
        Color.FromRgb(56, 189, 248),
        Color.FromRgb(245, 158, 11),
        Color.FromRgb(244, 114, 182),
        Color.FromRgb(132, 204, 22)
    ];
    private readonly Brush _selectedBackground = new SolidColorBrush(Color.FromRgb(231, 248, 239));
    private readonly Brush _transparent = Brushes.Transparent;
    private readonly AppConfigService _configService = new();
    private readonly MemoService _memoService = new();
    private readonly PromptTemplateService _templateService = new();
    private readonly NativeWeChatDataService _dataService = new();
    private readonly ChatBriefDatabaseService _chatBriefDb = new();
    private readonly IAiSummaryService _summaryService = new DeepSeekSummaryService();
    private readonly DeepSeekSummaryService _deepSeekService = new();
    private readonly List<ChatRoom> _chatRooms = [];
    private readonly List<FriendContact> _friends = [];
    private readonly Dictionary<string, List<ChatMessage>> _importedGroupMessages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SummaryState> _groupSummaryStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SummaryState> _personSummaryStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WorkspaceEntry> _groupWorkspaceEntries = [];
    private readonly List<WorkspaceEntry> _personWorkspaceEntries = [];
    private List<MemoEntry> _memos = [];
    private AppConfig _config = new();
    private string _currentMode = "群聊分析";
    private string _memoMode = "群聊";
    private string _analysisModuleMode = "聚焦";
    private string _compareMode = "群聊";
    private bool _isRefreshingCompareSelectors;
    private string _pendingMemoText = "";
    private PromptTemplate? _selectedTemplate;
    private string _groupSummaryMarkdown = "";
    private string _personSummaryMarkdown = "";
    private bool _groupEmptyStateVisible;
    private bool _personEmptyStateVisible;
    private bool _isNavCollapsed;
    private bool _isSummaryFullscreen;
    private bool _isContextDrawerCollapsed;
    private string _insightObjectMode = "好友";
    private int _globalInsightDays = 30;
    private int _globalInsightObjectCount;
    private string _globalInsightView = "总览";
    private readonly List<ChatMessage> _globalInsightMessages = [];
    private readonly Dictionary<string, (string Name, List<ChatMessage> Messages)> _globalInsightGroupMessages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Name, List<ChatMessage> Messages)> _globalInsightFriendMessages = new(StringComparer.OrdinalIgnoreCase);
    private string _globalInsightRelationMode = "群聊";
    private string _globalInsightScopeKey = "all";
    private bool _isRefreshingGlobalInsightScope;
    private readonly HashSet<string> _groupSpeakerFilter = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _personSpeakerFilter = new(StringComparer.OrdinalIgnoreCase);
    private bool _isRefreshingSpeakerFilter;
    private const int SpeakerSearchThreshold = 12;
    private const int SpeakerVisibleLimit = 36;
    private int _copyBunnyRunId;
    private Point _workspaceDragStart;
    private UIElement? _currentPage;
    private bool _isRefreshingBalance;

    public MainWindow()
    {
        InitializeComponent();
        _config = _configService.Load();
        _memos = _memoService.Load();
        TryEnsureLocalDb();
        ApiKeyBox.Password = _config.ApiKey;
        SelfAliasesBox.Text = _config.SelfAliases;
        WeChatDataDirectoryBox.Text = string.IsNullOrWhiteSpace(_config.WeChatDataDirectory) ? "自动检测微信数据目录" : _config.WeChatDataDirectory;
        _dataService.SetPreferredWechatRoot(_config.WeChatDataDirectory);
        _groupSummaryMarkdown = "选择群聊后生成总结。";
        _personSummaryMarkdown = "选择好友后生成总结。";
        ApplyGroupSummaryView();
        ApplyPersonSummaryView();
        InitializeDateRanges();
        LoadTemplates();
        ShowPage(GuidePage, GuideButton);
    }

    private void InitializeDateRanges()
    {
        GroupStartCalendar.SelectedDate = DateTime.Today.AddDays(-7);
        GroupEndCalendar.SelectedDate = DateTime.Today;
        GroupStartTimeBox.Text = "00:00";
        GroupEndTimeBox.Text = "23:59";
        PersonStartCalendar.SelectedDate = DateTime.Today.AddDays(-30);
        PersonEndCalendar.SelectedDate = DateTime.Today;
        PersonStartTimeBox.Text = "00:00";
        PersonEndTimeBox.Text = "23:59";
        UpdateDateTimeButtons();
    }

    private void OpenGroupStartDateTimePopupClicked(object sender, RoutedEventArgs e) => GroupStartDateTimePopup.IsOpen = true;

    private void OpenGroupEndDateTimePopupClicked(object sender, RoutedEventArgs e) => GroupEndDateTimePopup.IsOpen = true;

    private void OpenPersonStartDateTimePopupClicked(object sender, RoutedEventArgs e) => PersonStartDateTimePopup.IsOpen = true;

    private void OpenPersonEndDateTimePopupClicked(object sender, RoutedEventArgs e) => PersonEndDateTimePopup.IsOpen = true;

    private void DateTimeCalendarChanged(object sender, SelectionChangedEventArgs e) => UpdateDateTimeButtons();

    private void TimeBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDateTimeButtons();
    }

    private void UpdateDateTimeButtons()
    {
        SetDateTimeButtonContent(GroupStartDateTimeButton, GroupStartCalendar, GroupStartTimeBox, "开始时间", "00:00");
        SetDateTimeButtonContent(GroupEndDateTimeButton, GroupEndCalendar, GroupEndTimeBox, "结束时间", "23:59");
        SetDateTimeButtonContent(PersonStartDateTimeButton, PersonStartCalendar, PersonStartTimeBox, "开始时间", "00:00");
        SetDateTimeButtonContent(PersonEndDateTimeButton, PersonEndCalendar, PersonEndTimeBox, "结束时间", "23:59");
    }

    private void ApplyGroupDateRange(DateTime start, DateTime end)
    {
        GroupStartCalendar.SelectedDate = start.Date;
        GroupEndCalendar.SelectedDate = end.Date;
        GroupStartTimeBox.Text = start.ToString("HH:mm", CultureInfo.InvariantCulture);
        GroupEndTimeBox.Text = end.ToString("HH:mm", CultureInfo.InvariantCulture);
        UpdateDateTimeButtons();
    }

    private void ShowGuide(object sender, RoutedEventArgs e) => ShowPage(GuidePage, GuideButton);

    private void ShowAnalysis(object sender, RoutedEventArgs e) => ShowPage(GroupPage, GroupButton);

    private void ShowGroups(object sender, RoutedEventArgs e) => ShowPage(GroupPage, GroupSubButton);

    private void ShowPerson(object sender, RoutedEventArgs e) => ShowPage(PersonPage, PersonButton);

    private void ShowSingleAnalysisFromNav(object sender, RoutedEventArgs e)
    {
        _analysisModuleMode = "聚焦";
        ShowPage(GroupPage, GroupSubButton);
    }

    private void ShowCompareAnalysisFromNav(object sender, RoutedEventArgs e)
    {
        _analysisModuleMode = "对照";
        ShowPage(AnalysisPage, PersonButton);
    }

    private void ShowInsights(object sender, RoutedEventArgs e) => ShowPage(GlobalInsightPage, InsightButton);

    private void ShowMemos(object sender, RoutedEventArgs e) => ShowPage(MemoPage, MemoButton);

    private void ShowTemplates(object sender, RoutedEventArgs e) => ShowPage(TemplatePage, TemplateButton);

    private void ShowSettings(object sender, RoutedEventArgs e) => ShowPage(SettingsPage, SettingsButton);

    private async void RefreshAiBalanceClicked(object sender, RoutedEventArgs e) => await RefreshAiBalanceAsync();

    private async void RefreshGlobalInsightClicked(object sender, RoutedEventArgs e) => await ScanGlobalInsightAsync();

    private void SetGlobalInsightSevenDaysClicked(object sender, RoutedEventArgs e)
    {
        _globalInsightDays = 7;
        ApplyGlobalInsightRangeButtons();
        GlobalInsightStatusText.Text = "已选择近 7 天，点击扫描洞察。";
    }

    private void SetGlobalInsightThirtyDaysClicked(object sender, RoutedEventArgs e)
    {
        _globalInsightDays = 30;
        ApplyGlobalInsightRangeButtons();
        GlobalInsightStatusText.Text = "已选择近 30 天，点击扫描洞察。";
    }

    private void ShowGlobalInsightGroupRelations(object sender, RoutedEventArgs e)
    {
        _globalInsightRelationMode = "群聊";
        RenderGlobalInsightRelations(_globalInsightMessages);
    }

    private void ShowGlobalInsightFriendRelations(object sender, RoutedEventArgs e)
    {
        _globalInsightRelationMode = "好友";
        RenderGlobalInsightRelations(_globalInsightMessages);
    }

    private void ShowGlobalOverviewInsight(object sender, RoutedEventArgs e)
    {
        _globalInsightView = "总览";
        RefreshGlobalInsightPage();
    }

    private void ShowGlobalAnnualInsight(object sender, RoutedEventArgs e)
    {
        _globalInsightView = "年度总结";
        RefreshGlobalInsightPage();
    }

    private void ShowGlobalTimeInvestment(object sender, RoutedEventArgs e)
    {
        _globalInsightView = "时间投入";
        RefreshGlobalInsightPage();
    }

    private void ShowGlobalRelationshipInsight(object sender, RoutedEventArgs e)
    {
        _globalInsightView = "关系变化";
        RefreshGlobalInsightPage();
    }

    private void GlobalInsightScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingGlobalInsightScope)
        {
            return;
        }

        _globalInsightScopeKey = (GlobalInsightScopeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        RefreshGlobalInsightPage();
    }

    private async void AskGlobalInsightClicked(object sender, RoutedEventArgs e)
    {
        var question = GlobalInsightQuestionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            GlobalInsightAnswerText.Text = "先输入一个问题，比如：上周谁最常提到作业？";
            GlobalInsightAnswerList.Items.Clear();
            return;
        }

        try
        {
            var answer = _chatBriefDb.AskLocalInsight(question, DateTime.Now);
            GlobalInsightAnswerList.Items.Clear();
            var max = Math.Max(1, answer.Items.Select(item => item.Count).DefaultIfEmpty(1).Max());
            foreach (var item in answer.Items)
            {
                GlobalInsightAnswerList.Items.Add(CreateInsightBarRow(item.Name, item.Count, max, new SolidColorBrush(InsightChartColors[1])));
            }

            GlobalInsightAnswerText.Text = "正在结合本地统计和相关原文询问 AI...";
            var evidence = _chatBriefDb.QueryMessages(answer.Start, answer.End, keyword: answer.Keyword)
                .Take(80)
                .ToList();
            var aiAnswer = await _summaryService.AnswerQuestionAsync(question, answer, evidence, CurrentConfigFromUi());
            GlobalInsightAnswerText.Text = string.IsNullOrWhiteSpace(aiAnswer) ? answer.Answer : aiAnswer;
        }
        catch (Exception ex)
        {
            GlobalInsightAnswerText.Text = $"提问失败：{ex.Message}";
            GlobalInsightAnswerList.Items.Clear();
        }
    }

    private void ShowInsightGroupObjects(object sender, RoutedEventArgs e)
    {
        _insightObjectMode = "群聊";
        RenderInsightObjectRank();
    }

    private void ShowInsightPersonObjects(object sender, RoutedEventArgs e)
    {
        _insightObjectMode = "好友";
        RenderInsightObjectRank();
    }

    private void ShowSingleAnalysisModuleClicked(object sender, RoutedEventArgs e)
    {
        _analysisModuleMode = "聚焦";
        RefreshAnalysisModuleView();
    }

    private void ShowCompareAnalysisModuleClicked(object sender, RoutedEventArgs e)
    {
        _analysisModuleMode = "对照";
        RefreshAnalysisModuleView();
    }

    private void ShowGroupCompareClicked(object sender, RoutedEventArgs e)
    {
        _compareMode = "群聊";
        RefreshCompareSelectors();
    }

    private void ShowPersonCompareClicked(object sender, RoutedEventArgs e)
    {
        _compareMode = "好友";
        RefreshCompareSelectors();
    }

    private void CompareSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isRefreshingCompareSelectors)
        {
            RefreshCompareResult();
        }
    }

    private void RefreshCompareClicked(object sender, RoutedEventArgs e) => RefreshCompareResult();

    private void SaveSettingsClicked(object sender, RoutedEventArgs e)
    {
        CurrentConfigFromUi();
        ShowAppNotice("设置已保存", "AI Key 只保存在本机。", "success");
    }

    private async void CopyDeveloperEmailClicked(object sender, RoutedEventArgs e)
    {
        var runId = ++_copyBunnyRunId;
        Clipboard.SetText("aezakmiavery@gmail.com");
        CopyBunnyToast.BeginAnimation(OpacityProperty, null);
        CopyBunnyToast.Visibility = Visibility.Visible;
        CopyBunnyToast.Opacity = 1;

        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        if (runId != _copyBunnyRunId)
        {
            return;
        }

        CopyBunnyAnimation.StopAnimation();
        CopyBunnyAnimation.PlayAnimation();

        await Task.Delay(3600);
        if (runId != _copyBunnyRunId)
        {
            return;
        }

        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            CopyBunnyAnimation.StopAnimation();
            CopyBunnyToast.Visibility = Visibility.Collapsed;
            CopyBunnyToast.Opacity = 1;
        };
        CopyBunnyToast.BeginAnimation(OpacityProperty, fade);
    }

    private void ToggleNavCollapsedClicked(object sender, RoutedEventArgs e)
    {
        SetNavCollapsed(!_isNavCollapsed);
    }

    private void SetNavCollapsed(bool collapsed)
    {
        _isNavCollapsed = collapsed;
        AnimateColumnWidth(NavColumn, NavColumn.ActualWidth > 0 ? NavColumn.ActualWidth : NavColumn.Width.Value, collapsed ? 76 : 218);
        CollapseNavIcon.Text = collapsed ? "\uE76C" : "\uE76B";

        BrandText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        BrandPanel.Margin = collapsed ? new Thickness(17, 79, 17, 74) : new Thickness(28, 79, 0, 74);
        BrandPanel.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        GuideNavText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        GroupNavText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        GroupSubNavText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        PersonNavText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        InsightNavText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        MemoNavText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        TemplateNavText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SettingsNavText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        InitStatusCard.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        InitializeIconButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        UserInfoPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        UserMoreIcon.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

        NavBottomPanel.Margin = collapsed ? new Thickness(17, 0, 17, 18) : new Thickness(25, 0, 25, 18);
        foreach (var button in new[] { GuideButton, GroupButton, GroupSubButton, PersonButton, InsightButton, MemoButton, TemplateButton, SettingsButton })
        {
            button.Padding = collapsed ? new Thickness(26, 0, 0, 0) :
                button == GroupSubButton || button == PersonButton ? new Thickness(18, 0, 0, 0) :
                new Thickness(28, 0, 0, 0);
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
        }

        RefreshContextColumnForCurrentPage();
    }

    private static void AnimateColumnWidth(ColumnDefinition column, double from, double to)
    {
        var animation = new GridLengthAnimation
        {
            From = new GridLength(from),
            To = new GridLength(to),
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        column.BeginAnimation(ColumnDefinition.WidthProperty, animation);
    }

    private async void InitializeClicked(object sender, RoutedEventArgs e)
    {
        CurrentConfigFromUi();
        _dataService.SetPreferredWechatRoot(_config.WeChatDataDirectory);
        SetInitializing(true, "准备读取微信数据...");
        WorkspaceObjectList.Items.Clear();
        _groupWorkspaceEntries.Clear();
        _personWorkspaceEntries.Clear();
        _chatRooms.Clear();
        _friends.Clear();
        _importedGroupMessages.Clear();
        _groupSummaryStates.Clear();
        _personSummaryStates.Clear();

        try
        {
            SetInitStep("正在定位微信数据目录...");
            await Task.Delay(80);
            SetInitStep("正在扫描微信进程并提取数据库密钥...");
            var rooms = await Task.Run(() => _dataService.ListChatRoomsAsync());
            SetInitStep("正在读取好友与群聊名称...");
            var friends = await Task.Run(() => _dataService.ListFriendsAsync());
            SetInitStep("正在整理工作区列表...");
            _chatRooms.AddRange(rooms);
            _friends.AddRange(friends);
            if (string.IsNullOrWhiteSpace(_config.WeChatDataDirectory) && !string.IsNullOrWhiteSpace(_dataService.CurrentWechatUserDir))
            {
                _config.WeChatDataDirectory = _dataService.CurrentWechatUserDir;
                WeChatDataDirectoryBox.Text = _config.WeChatDataDirectory;
                _configService.Save(_config);
            }
            var catalogSynced = TrySyncCatalogToLocalDb();
            RefreshObjectSelector();
            BackendStatusText.Text = "已连接";
            if (catalogSynced)
            {
                SettingsDataStatusText.Text = $"已读取 {_chatRooms.Count} 个群聊、{_friends.Count} 位好友。";
            }
            SetInitStep($"初始化完成：{_chatRooms.Count} 个群聊，{_friends.Count} 位好友。");
        }
        catch (Exception ex)
        {
            BackendStatusText.Text = "初始化失败";
            var hasSelectedDirectory = !string.IsNullOrWhiteSpace(_config.WeChatDataDirectory) && Directory.Exists(_config.WeChatDataDirectory);
            SettingsDataStatusText.Text = hasSelectedDirectory
                ? "已找到微信数据目录，但聊天数据库暂时无法自动读取。"
                : "自动读取失败。新版微信可使用导入聊天文本继续分析。";
            SetInitStep(hasSelectedDirectory
                ? "目录已找到，但当前微信版本暂不支持自动读取。可粘贴聊天文本继续分析。"
                : "自动读取失败，支持版本为微信 4.1.9.57。新版微信可导入聊天文本。");
            ShowAppNotice("自动读取失败", hasSelectedDirectory
                ? $"已找到目录：{_config.WeChatDataDirectory}\n\n当前自动读取支持微信 4.1.9.57。新版微信的聊天数据库暂时无法直接解析，你可以使用“粘贴文本”或“选择文本文件”继续分析。"
                : $"{ex.Message}\n\n当前自动读取支持微信 4.1.9.57。新版微信可先导入聊天文本继续使用。", "warning");
        }
        finally
        {
            SetInitializing(false, InitStepText.Text);
        }
    }

    private void SetInitializing(bool isInitializing, string message)
    {
        InitializeButton.IsEnabled = !isInitializing;
        InitLoadingAnimation.Visibility = isInitializing ? Visibility.Visible : Visibility.Collapsed;
        GuideInitLoadingAnimation.Visibility = isInitializing ? Visibility.Visible : Visibility.Collapsed;
        BackendStatusText.Text = isInitializing ? "读取中" : BackendStatusText.Text;
        SetInitStep(message);
    }

    private void BrowseWeChatDataDirectoryClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择微信数据目录",
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(_config.WeChatDataDirectory) && Directory.Exists(_config.WeChatDataDirectory))
        {
            dialog.InitialDirectory = _config.WeChatDataDirectory;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var resolved = NativeWeChatDataService.ResolveWechatUserDir(dialog.FolderName);
        _config.WeChatDataDirectory = resolved ?? dialog.FolderName;
        WeChatDataDirectoryBox.Text = _config.WeChatDataDirectory;
        _configService.Save(_config);
        _dataService.SetPreferredWechatRoot(_config.WeChatDataDirectory);

        if (resolved is null)
        {
            ShowAppNotice("微信数据目录", "已记录该目录，但没有在里面找到 db_storage/message/message_0.db。初始化时会继续尝试自动检测。", "warning");
            return;
        }

        SetInitStep("已选择微信数据目录，正在读取...");
        InitializeClicked(sender, e);
    }

    private void UseAutoWeChatDataDirectoryClicked(object sender, RoutedEventArgs e)
    {
        SetInitStep("正在自动检测微信数据目录...");
        var detected = NativeWeChatDataService.AutoDetectWechatUserDir();
        if (string.IsNullOrWhiteSpace(detected))
        {
            _config.WeChatDataDirectory = "";
            WeChatDataDirectoryBox.Text = "未找到微信数据目录";
            _configService.Save(_config);
            _dataService.SetPreferredWechatRoot(null);
            SetInitStep("未自动找到微信数据目录，可手动选择目录或导入聊天文本。");
            ShowAppNotice("自动检测", "没有在常见位置找到微信数据目录。你可以手动选择目录，或直接粘贴聊天文本导入。", "warning");
            return;
        }

        _config.WeChatDataDirectory = detected;
        WeChatDataDirectoryBox.Text = detected;
        _configService.Save(_config);
        _dataService.SetPreferredWechatRoot(detected);
        SetInitStep("已找到微信数据目录，正在读取...");
        InitializeClicked(sender, e);
    }

    private void ImportChatTextClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入聊天文本",
            Filter = "聊天文本 (*.txt;*.md;*.csv)|*.txt;*.md;*.csv|所有文件 (*.*)|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(dialog.FileName);
            var title = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
            ImportChatText(title, text);
        }
        catch (Exception ex)
        {
            ShowAppNotice("导入失败", ex.Message, "error");
        }
    }

    private void PasteChatTextClicked(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 260,
            Tag = "把聊天内容粘贴到这里"
        };
        if (Clipboard.ContainsText())
        {
            input.Text = Clipboard.GetText();
        }

        var titleBox = new TextBox
        {
            Height = 42,
            Margin = new Thickness(0, 0, 0, 12),
            Tag = "记录名称，例如：产品群 10月讨论"
        };

        var dialog = CreateInputDialog("粘贴聊天文本", new StackPanel
        {
            Children =
            {
                titleBox,
                input
            }
        }, "导入");

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(titleBox.Text) ? $"导入记录 {DateTime.Now:MM-dd HHmm}" : titleBox.Text.Trim();
        ImportChatText(title, input.Text);
    }

    private void ImportChatText(string title, string text)
    {
        var messages = ParseImportedChatText(text);
        if (messages.Count == 0)
        {
            ShowAppNotice("导入失败", "没有识别到可分析的聊天内容。", "warning");
            return;
        }

        var id = $"import:{Guid.NewGuid():N}";
        _importedGroupMessages[id] = messages;
        var room = new ChatRoom(id, title, messages.Count);
        _chatRooms.Insert(0, room);
        _groupWorkspaceEntries.Insert(0, new WorkspaceEntry(title, room));

        BackendStatusText.Text = "导入模式";
        SettingsDataStatusText.Text = $"已导入「{title}」：{messages.Count} 条消息。";
        SetInitStep($"已导入「{title}」，可进入群聊工作台生成总结。");
        ShowAppNotice("导入完成", $"已导入 {messages.Count} 条消息。", "success");
        ShowPage(GroupPage, GroupSubButton);
        ApplyGroupDateRange(messages.Min(message => message.Time), messages.Max(message => message.Time));
        RenderWorkspaceObjects(room);
    }

    private static List<ChatMessage> ParseImportedChatText(string text)
    {
        var messages = new List<ChatMessage>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var fallbackTime = DateTime.Now.AddMinutes(-lines.Length);
        var currentSender = "导入文本";
        ChatMessage? lastMessage = null;
        var timestampWithSender = new Regex(@"^(?<time>\d{4}[-/年]\d{1,2}[-/月]\d{1,2}(?:日)?\s+\d{1,2}:\d{2}(?::\d{2})?)\s+(?<sender>[^:：]{1,40})[:：]\s*(?<content>.+)$");
        var senderLine = new Regex(@"^(?<sender>[^:：]{1,40})[:：]\s*(?<content>.+)$");

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var time = fallbackTime = fallbackTime.AddMinutes(1);
            var sender = currentSender;
            var content = line;
            var match = timestampWithSender.Match(line);
            if (match.Success)
            {
                sender = match.Groups["sender"].Value.Trim();
                content = match.Groups["content"].Value.Trim();
                time = ParseImportedTime(match.Groups["time"].Value) ?? time;
                currentSender = sender;
            }
            else
            {
                match = senderLine.Match(line);
                if (match.Success && !LooksLikeUrlPrefix(match.Groups["sender"].Value))
                {
                    sender = match.Groups["sender"].Value.Trim();
                    content = match.Groups["content"].Value.Trim();
                    currentSender = sender;
                }
                else if (lastMessage is not null)
                {
                    var merged = lastMessage.Content + Environment.NewLine + line;
                    messages[^1] = lastMessage with { Content = merged };
                    lastMessage = messages[^1];
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            lastMessage = new ChatMessage(time, sender, sender, content);
            messages.Add(lastMessage);
        }

        return messages;
    }

    private static DateTime? ParseImportedTime(string value)
    {
        var normalized = value
            .Replace("年", "/", StringComparison.Ordinal)
            .Replace("月", "/", StringComparison.Ordinal)
            .Replace("日", "", StringComparison.Ordinal)
            .Replace("-", "/", StringComparison.Ordinal);
        return DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }

    private static bool LooksLikeUrlPrefix(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Equals("http", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("https", StringComparison.OrdinalIgnoreCase);
    }

    private void SetInitStep(string message)
    {
        InitStepText.Text = message;
        GuideInitStepText.Text = message;
    }

    private void ChooseObjectClicked(object sender, RoutedEventArgs e)
    {
        if (_currentMode == "好友分析")
        {
            ShowFriendPickerDialog();
            return;
        }

        ShowChatroomPickerDialog();
    }

    private void AddWorkspaceLabel(string label)
    {
        if (!WorkspaceObjectList.Items.Contains(label))
        {
            WorkspaceObjectList.Items.Add(label);
        }
    }

    private void AddWorkspaceObject(string label, object tag)
    {
        var entries = WorkspaceEntriesFor(tag);
        if (entries.All(entry => !Equals(entry.Tag, tag)))
        {
            entries.Add(new WorkspaceEntry(label, tag));
        }

        RenderWorkspaceObjects(tag);
    }

    private void RenderWorkspaceObjects(object? selectedTag = null)
    {
        WorkspaceObjectList.Items.Clear();
        foreach (var entry in CurrentWorkspaceEntries())
        {
            WorkspaceObjectList.Items.Add(CreateWorkspaceListItem(entry));
        }

        RefreshObjectSelector();

        if (selectedTag is not null)
        {
            SelectWorkspaceObject(selectedTag);
        }
    }

    private ListBoxItem CreateWorkspaceListItem(WorkspaceEntry entry)
    {
        var listItem = new ListBoxItem { Tag = entry.Tag, AllowDrop = true };
        listItem.Content = CreateWorkspaceObjectContent(entry, listItem);
        listItem.ContextMenu = CreateWorkspaceContextMenu(entry, listItem);
        listItem.PreviewMouseLeftButtonDown += WorkspaceObjectPreviewMouseLeftButtonDown;
        listItem.PreviewMouseMove += WorkspaceObjectPreviewMouseMove;
        listItem.Drop += WorkspaceObjectItemDrop;
        return listItem;
    }

    private void SelectWorkspaceObject(object tag)
    {
        foreach (var item in WorkspaceObjectList.Items.OfType<ListBoxItem>())
        {
            if (!Equals(item.Tag, tag))
            {
                continue;
            }

            WorkspaceObjectList.SelectedItem = item;
            FlashWorkspaceSelection(item);
            return;
        }
    }

    private List<WorkspaceEntry> CurrentWorkspaceEntries() =>
        _currentMode == "好友分析" ? _personWorkspaceEntries : _groupWorkspaceEntries;

    private List<WorkspaceEntry> WorkspaceEntriesFor(object tag) =>
        tag is FriendContact ? _personWorkspaceEntries : _groupWorkspaceEntries;

    private Border CreateWorkspaceObjectContent(WorkspaceEntry entry, ListBoxItem owner)
    {
        var showInlineActions = entry.Label.Length <= 14;
        const double actionColumnWidth = 66;
        var grid = new Grid
        {
            ClipToBounds = true
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(showInlineActions ? actionColumnWidth : 0) });

        var title = new TextBlock
        {
            Text = entry.Label,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = entry.IsPinned ? new SolidColorBrush(Color.FromRgb(6, 173, 86)) : new SolidColorBrush(Color.FromRgb(31, 35, 41)),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = entry.Label
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Width = showInlineActions ? actionColumnWidth : 0,
            Visibility = showInlineActions ? Visibility.Visible : Visibility.Collapsed
        };
        actions.Children.Add(CreateWorkspaceActionButton(CreatePinIcon(entry.IsPinned), entry.IsPinned ? "取消置顶" : "置顶", owner, PinWorkspaceObjectClicked, 28, entry.IsPinned));
        actions.Children.Add(CreateWorkspaceActionButton("×", "移除", owner, RemoveWorkspaceObjectClicked));

        Grid.SetColumn(title, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(title);
        grid.Children.Add(actions);

        return new Border
        {
            Background = entry.IsPinned ? new SolidColorBrush(Color.FromRgb(231, 248, 239)) : Brushes.Transparent,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(0),
            Child = grid
        };
    }

    private ContextMenu CreateWorkspaceContextMenu(WorkspaceEntry entry, ListBoxItem owner)
    {
        var menu = new ContextMenu();
        var pinItem = new MenuItem
        {
            Header = entry.IsPinned ? "取消置顶" : "置顶",
            Tag = owner
        };
        pinItem.Click += PinWorkspaceObjectClicked;

        var removeItem = new MenuItem
        {
            Header = "删除",
            Tag = owner
        };
        removeItem.Click += RemoveWorkspaceObjectClicked;

        menu.Items.Add(pinItem);
        menu.Items.Add(removeItem);
        return menu;
    }

    private static TextBlock CreatePinIcon(bool isPinned) => new()
    {
        Text = isPinned ? "\uE840" : "\uE718",
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 13,
        Foreground = isPinned ? new SolidColorBrush(Color.FromRgb(7, 193, 96)) : new SolidColorBrush(Color.FromRgb(107, 114, 128)),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Button CreateWorkspaceActionButton(object content, string tooltip, ListBoxItem owner, RoutedEventHandler click, double width = 24, bool isActive = false)
    {
        var button = new Button
        {
            Content = content,
            Width = width,
            Height = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 15,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            Background = isActive ? new SolidColorBrush(Color.FromRgb(216, 245, 229)) : Brushes.Transparent,
            ToolTip = tooltip,
            Tag = owner,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        button.Click += click;
        return button;
    }

    private void RemoveWorkspaceObjectClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: ListBoxItem item })
        {
            RemoveWorkspaceItem(item);
        }
    }

    private void PinWorkspaceObjectClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: ListBoxItem item })
        {
            TogglePinnedWorkspaceItem(item);
        }
    }

    private void TogglePinnedWorkspaceItem(ListBoxItem item)
    {
        var entries = WorkspaceEntriesFor(item.Tag);
        var entryIndex = entries.FindIndex(entry => Equals(entry.Tag, item.Tag));
        if (entryIndex < 0)
        {
            return;
        }

        var entry = entries[entryIndex];
        entries.RemoveAt(entryIndex);
        var updated = entry with { IsPinned = !entry.IsPinned };
        var targetIndex = updated.IsPinned
            ? entries.TakeWhile(existing => existing.IsPinned).Count()
            : entries.FindLastIndex(existing => existing.IsPinned) + 1;
        entries.Insert(Math.Clamp(targetIndex, 0, entries.Count), updated);
        RenderWorkspaceObjects(updated.Tag);
    }

    private void MoveWorkspaceItem(ListBoxItem item, int targetIndex)
    {
        var currentIndex = WorkspaceObjectList.Items.IndexOf(item);
        if (currentIndex < 0 || targetIndex < 0 || currentIndex == targetIndex)
        {
            return;
        }

        var entries = WorkspaceEntriesFor(item.Tag);
        var entryIndex = entries.FindIndex(entry => Equals(entry.Tag, item.Tag));
        if (entryIndex < 0)
        {
            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, entries.Count - 1);
        var entry = entries[entryIndex];
        entries.RemoveAt(entryIndex);
        if (entry.IsPinned)
        {
            var pinnedCount = entries.Count(existing => existing.IsPinned);
            targetIndex = Math.Clamp(targetIndex, 0, pinnedCount);
        }
        else
        {
            var pinnedCount = entries.Count(existing => existing.IsPinned);
            targetIndex = Math.Clamp(targetIndex, pinnedCount, entries.Count);
        }
        entries.Insert(targetIndex, entry);
        RenderWorkspaceObjects(entry.Tag);
        if (WorkspaceObjectList.SelectedItem is ListBoxItem selectedItem)
        {
            WorkspaceObjectList.ScrollIntoView(selectedItem);
        }
    }

    private void WorkspaceObjectPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _workspaceDragStart = e.GetPosition(null);
    }

    private void WorkspaceObjectPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not ListBoxItem item || FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _workspaceDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _workspaceDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop(item, item, DragDropEffects.Move);
    }

    private void WorkspaceObjectItemDrop(object sender, DragEventArgs e)
    {
        if (sender is ListBoxItem target)
        {
            DropWorkspaceObject(e, target);
        }
    }

    private void WorkspaceObjectListDrop(object sender, DragEventArgs e)
    {
        DropWorkspaceObject(e, null);
    }

    private void DropWorkspaceObject(DragEventArgs e, ListBoxItem? target)
    {
        if (e.Data.GetData(typeof(ListBoxItem)) is not ListBoxItem source || ReferenceEquals(source, target))
        {
            return;
        }

        var targetIndex = target is null ? WorkspaceObjectList.Items.Count - 1 : WorkspaceObjectList.Items.IndexOf(target);
        MoveWorkspaceItem(source, targetIndex);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static void FlashWorkspaceSelection(ListBoxItem item)
    {
        if (item.Content is not Border border)
        {
            return;
        }

        var brush = new SolidColorBrush(Color.FromArgb(80, 7, 193, 96));
        border.Background = brush;
        var animation = new ColorAnimation
        {
            From = Color.FromArgb(80, 7, 193, 96),
            To = Colors.Transparent,
            Duration = new Duration(TimeSpan.FromMilliseconds(420)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private void ShowChatroomPickerDialog()
    {
        if (_chatRooms.Count == 0)
        {
            ShowAppNotice("选择群聊", "请先初始化微信数据。");
            return;
        }

        var dialog = CreatePickerDialog("选择群聊", "从微信群聊中选择一个对象加入工作区");
        var list = new ListBox
        {
            VerticalAlignment = VerticalAlignment.Stretch
        };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        var search = CreateSearchBox("搜索群聊名称");
        void Fill(string keyword = "")
        {
            list.Items.Clear();
            foreach (var room in _chatRooms.Where(room => string.IsNullOrWhiteSpace(keyword) || room.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                list.Items.Add(CreateContactItem(room.DisplayName, $"{room.MessageCount} 条消息", room));
            }
        }

        search.Text.TextChanged += (_, _) => Fill(search.Text.Text);
        Fill();

        var listHost = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(230, 232, 235)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6),
            Child = list
        };
        dialog.Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialog.Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(search.Host, 0);
        Grid.SetRow(listHost, 1);
        dialog.Body.Children.Add(search.Host);
        dialog.Body.Children.Add(listHost);
        dialog.Confirm.Click += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: ChatRoom room })
            {
                AddWorkspaceObject($"{room.DisplayName}（{room.MessageCount} 条）", room);
                dialog.Window.Close();
            }
        };
        dialog.Window.ShowDialog();
    }

    private void ShowFriendPickerDialog()
    {
        if (_friends.Count == 0)
        {
            ShowAppNotice("选择好友", "请先初始化微信数据。");
            return;
        }

        var dialog = CreatePickerDialog("选择好友", "像微信通讯录一样按备注、昵称或首字母定位好友");
        var search = CreateSearchBox("搜索好友备注或昵称");
        var list = new ListBox
        {
            VerticalAlignment = VerticalAlignment.Stretch
        };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        var index = new StackPanel
        {
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        Grid.SetColumn(list, 0);
        Grid.SetColumn(index, 1);
        var listHost = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(230, 232, 235)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6),
            Child = list
        };
        Grid.SetColumn(listHost, 0);
        content.Children.Add(listHost);
        content.Children.Add(index);

        void Fill(string keyword = "")
        {
            list.Items.Clear();
            foreach (var group in _friends
                         .Where(friend => string.IsNullOrWhiteSpace(keyword) || friend.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                         .GroupBy(friend => GetInitial(friend.DisplayName))
                         .OrderBy(g => g.Key == "#" ? "ZZZ" : g.Key))
            {
                list.Items.Add(new ListBoxItem
                {
                    Content = group.Key,
                    IsEnabled = false,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128))
                });
                foreach (var friend in group.OrderBy(f => f.DisplayName))
                {
                    var secondary = friend.MessageCount > 0
                        ? $"{friend.MessageCount} 条消息"
                        : "本机暂无聊天记录";
                    list.Items.Add(CreateContactItem(friend.DisplayName, secondary, friend));
                }
            }
        }

        foreach (var letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ#")
        {
            var button = new Button
            {
                Content = letter.ToString(),
                Style = (Style)FindResource("InitialButton")
            };
            button.Click += (_, _) => JumpToInitial(list, letter.ToString());
            index.Children.Add(button);
        }

        search.Text.TextChanged += (_, _) => Fill(search.Text.Text);
        Fill();

        dialog.Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialog.Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(search.Host, 0);
        Grid.SetRow(content, 1);
        dialog.Body.Children.Add(search.Host);
        dialog.Body.Children.Add(content);
        dialog.Confirm.Click += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: FriendContact friend })
            {
                AddWorkspaceObject(friend.DisplayName, friend);
                dialog.Window.Close();
            }
        };
        dialog.Window.ShowDialog();
    }

    private (Border Host, TextBox Text) CreateSearchBox(string hint)
    {
        var text = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(12, 10, 12, 10),
            ToolTip = hint
        };
        var host = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(245, 247, 249)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(221, 226, 232)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = text
        };
        return (host, text);
    }

    private static ListBoxItem CreateContactItem(string primary, string? secondary, object tag)
    {
        var name = new TextBlock
        {
            Text = primary,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var meta = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(secondary) ? " " : secondary,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var stack = new StackPanel();
        stack.Children.Add(name);
        stack.Children.Add(meta);
        return new ListBoxItem
        {
            Content = stack,
            Tag = tag,
            Padding = new Thickness(12, 8, 12, 8),
            MinHeight = 54
        };
    }

    private (Window Window, Grid Body, Button Confirm) CreatePickerDialog(string title, string subtitle)
    {
        var window = new Window
        {
            Owner = this,
            Title = title,
            Width = 560,
            Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(243, 245, 247))
        };
        window.Resources = Resources;
        var root = new DockPanel { Margin = new Thickness(20) };
        var header = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 16)
        };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 26,
            FontWeight = FontWeights.Bold
        });
        header.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            Margin = new Thickness(0, 6, 0, 0)
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new Button { Content = "取消", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Button { Content = "确认选择", Style = (Style)FindResource("PrimaryButton") };
        cancel.Click += (_, _) => window.Close();
        footer.Children.Add(cancel);
        footer.Children.Add(confirm);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var card = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(16),
            BorderBrush = new SolidColorBrush(Color.FromRgb(230, 232, 235)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16)
        };
        var body = new Grid();
        card.Child = body;
        root.Children.Add(card);
        window.Content = root;
        return (window, body, confirm);
    }

    private static void JumpToInitial(ListBox list, string initial)
    {
        foreach (var item in list.Items.OfType<ListBoxItem>())
        {
            if (item.IsEnabled == false && string.Equals(item.Content?.ToString(), initial, StringComparison.OrdinalIgnoreCase))
            {
                list.ScrollIntoView(item);
                return;
            }
        }
    }

    private void CloseCurrentObjectClicked(object sender, RoutedEventArgs e)
    {
        if (WorkspaceObjectList.SelectedItem is ListBoxItem item)
        {
            RemoveWorkspaceItem(item);
        }
    }

    private void RemoveWorkspaceItem(ListBoxItem item)
    {
        var index = WorkspaceObjectList.Items.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        RemoveSummaryState(item.Tag);
        WorkspaceEntriesFor(item.Tag).RemoveAll(entry => Equals(entry.Tag, item.Tag));
        var wasSelected = ReferenceEquals(WorkspaceObjectList.SelectedItem, item);
        WorkspaceObjectList.Items.Remove(item);
        RefreshObjectSelector();
        if (!wasSelected)
        {
            return;
        }

        if (WorkspaceObjectList.Items.Count == 0)
        {
            ClearWorkspaceSummaryState(item.Tag);
            return;
        }

        var nextIndex = Math.Min(index, WorkspaceObjectList.Items.Count - 1);
        WorkspaceObjectList.SelectedIndex = nextIndex;
    }

    private void WorkspaceObjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RestoreWorkspaceSummaryState();
    }

    private void RestoreWorkspaceSummaryState()
    {
        switch (WorkspaceObjectList.SelectedItem)
        {
            case ListBoxItem { Tag: ChatRoom room }:
            {
                var state = _groupSummaryStates.GetValueOrDefault(room.Id);
                _groupSummaryMarkdown = state?.Markdown ?? $"已选择「{room.DisplayName}」。";
                _groupEmptyStateVisible = state?.IsEmptyState ?? false;
                RefreshGroupEvidence(state?.Messages ?? [], state?.Markdown);
                GroupMemoSuggestionPanel.Visibility = Visibility.Collapsed;
                ApplyGroupSummaryView();
                break;
            }
            case ListBoxItem { Tag: FriendContact friend }:
            {
                var state = _personSummaryStates.GetValueOrDefault(friend.Id);
                _personSummaryMarkdown = state?.Markdown ?? $"已选择「{friend.DisplayName}」。";
                _personEmptyStateVisible = state?.IsEmptyState ?? false;
                RefreshPersonEvidence(state?.Messages ?? [], state?.Markdown);
                PersonMemoSuggestionPanel.Visibility = Visibility.Collapsed;
                ApplyPersonSummaryView();
                break;
            }
        }
    }

    private void ClearWorkspaceSummaryState(object? removedTag)
    {
        if (removedTag is FriendContact)
        {
            _personSummaryMarkdown = "选择好友后生成总结。";
            _personEmptyStateVisible = false;
            PersonEvidenceSubtitle.Text = "生成后，这里显示当前筛选范围内的原始聊天消息。";
            _personSpeakerFilter.Clear();
            PersonEvidenceList.Items.Clear();
            PersonEvidenceList.Items.Add(CreateEvidenceItem("提示", "生成后显示相关消息。"));
            PersonMemoSuggestionPanel.Visibility = Visibility.Collapsed;
            ApplyPersonSummaryView();
            return;
        }

        _groupSummaryMarkdown = "选择群聊后生成总结。";
        _groupEmptyStateVisible = false;
        GroupEvidenceSubtitle.Text = "生成后，这里显示当前筛选范围内的原始聊天消息。";
        RefreshGroupSpeakerFilter([]);
        GroupEvidenceList.Items.Clear();
        GroupEvidenceList.Items.Add(CreateEvidenceItem("提示", "生成后显示相关消息。"));
        GroupMemoSuggestionPanel.Visibility = Visibility.Collapsed;
        ApplyGroupSummaryView();
    }

    private void RemoveSummaryState(object? tag)
    {
        switch (tag)
        {
            case ChatRoom room:
                _groupSummaryStates.Remove(room.Id);
                break;
            case FriendContact friend:
                _personSummaryStates.Remove(friend.Id);
                break;
        }
    }

    private async void GenerateGroupSummaryClicked(object sender, RoutedEventArgs e)
    {
        if (_chatRooms.Count == 0)
        {
            _groupSummaryMarkdown = "请先初始化微信数据，再选择群聊。";
            ApplyGroupSummaryView();
            return;
        }

        var room = CurrentSelectedChatRoom() ?? _chatRooms[0];
        var templateName = GroupTemplateCombo.SelectedItem?.ToString() ?? _config.CurrentGroupTemplate;
        var template = _templateService.Find("群聊分析", templateName);
        GenerateGroupButton.IsEnabled = false;
        _groupEmptyStateVisible = false;
        _groupSummaryMarkdown = "";
        GroupMemoSuggestionPanel.Visibility = Visibility.Collapsed;
        ApplyGroupSummaryView();
        SetGeneratingStatus(GroupGeneratingStatus, true);
        try
        {
            var (start, end) = ReadDateRange(GroupStartCalendar, GroupStartTimeBox, GroupEndCalendar, GroupEndTimeBox);
            UpdateDateTimeButtons();
            var allMessages = await GetGroupMessagesAsync(room, start, end, GroupKeywordBox.Text);
            RefreshGroupSpeakerFilter(allMessages);
            if (allMessages.Count == 0)
            {
                _groupSummaryMarkdown = "";
                _groupEmptyStateVisible = true;
                _groupSummaryStates[room.Id] = new SummaryState(_groupSummaryMarkdown, allMessages, true);
                GroupEvidenceSubtitle.Text = "当前日期范围和关键词下没有可展示的原始消息。";
                RefreshGroupSpeakerFilter([]);
                GroupEvidenceList.Items.Clear();
                GroupEvidenceList.Items.Add(CreateEvidenceItem("本机暂无聊天记录", "这台电脑里没有读取到这个群聊的相关聊天记录。可能是刚在新电脑登录微信，或历史消息没有迁移到本机。"));
                ApplyGroupSummaryView();
                return;
            }

            var messages = ApplySpeakerFilter(allMessages, _groupSpeakerFilter);
            if (messages.Count == 0)
            {
                _groupSummaryMarkdown = "";
                _groupEmptyStateVisible = true;
                _groupSummaryStates[room.Id] = new SummaryState(_groupSummaryMarkdown, allMessages, true);
                RefreshGroupEvidence(allMessages);
                GroupEvidenceList.Items.Clear();
                GroupEvidenceList.Items.Add(CreateEvidenceItem("当前筛选下没有消息", "请在发言人筛选里至少保留一位有消息的发言人。"));
                ApplyGroupSummaryView();
                return;
            }

            _groupEmptyStateVisible = false;
            TrySyncMessagesToLocalDb(room.Id, "group", room.DisplayName, allMessages);
            RefreshGroupEvidence(allMessages);
            var resolvedTemplate = await ResolveGenerationTemplateAsync("群聊分析", GroupGenerationModeCombo, GroupNaturalInstructionBox.Text, template);
            if (resolvedTemplate is null)
            {
                return;
            }

            template = resolvedTemplate;
            _groupSummaryMarkdown = await _summaryService.SummarizeAsync(messages, template, CurrentConfigFromUi());
            if (IsAiFailureResponse(_groupSummaryMarkdown))
            {
                _groupSummaryMarkdown = BuildAiFailureText(_groupSummaryMarkdown);
            }
            _groupSummaryStates[room.Id] = new SummaryState(_groupSummaryMarkdown, allMessages, false);
            RefreshGroupEvidence(allMessages, _groupSummaryMarkdown);
            GroupMemoSuggestionPanel.Visibility = Visibility.Collapsed;
            ApplyGroupSummaryView();
            RefreshAnalysisOverviewIfVisible();
        }
        catch (Exception ex)
        {
            _groupSummaryMarkdown = BuildGenerateErrorText(ex);
            _groupEmptyStateVisible = false;
            _groupSummaryStates[room.Id] = new SummaryState(_groupSummaryMarkdown, [], false);
            ApplyGroupSummaryView();
            RefreshAnalysisOverviewIfVisible();
        }
        finally
        {
            GenerateGroupButton.IsEnabled = true;
            SetGeneratingStatus(GroupGeneratingStatus, false);
            RefreshAnalysisOverviewIfVisible();
        }
    }

    private async void GeneratePersonSummaryClicked(object sender, RoutedEventArgs e)
    {
        if (_friends.Count == 0)
        {
            _personSummaryMarkdown = "请先初始化微信数据，再选择好友。";
            ApplyPersonSummaryView();
            return;
        }

        var friend = CurrentSelectedFriend() ?? _friends[0];
        var templateName = PersonTemplateCombo.SelectedItem?.ToString() ?? _config.CurrentPersonTemplate;
        var template = _templateService.Find("好友分析", templateName);
        GeneratePersonButton.IsEnabled = false;
        _personEmptyStateVisible = false;
        _personSummaryMarkdown = "";
        PersonMemoSuggestionPanel.Visibility = Visibility.Collapsed;
        ApplyPersonSummaryView();
        SetGeneratingStatus(PersonGeneratingStatus, true);
        PersonEvidenceList.Items.Clear();
        try
        {
            var (start, end) = ReadDateRange(PersonStartCalendar, PersonStartTimeBox, PersonEndCalendar, PersonEndTimeBox);
            UpdateDateTimeButtons();
            var allMessages = await _dataService.GetMessagesAsync(friend.Id, start, end, PersonKeywordBox.Text);
            _personSpeakerFilter.Clear();
            if (allMessages.Count == 0)
            {
                _personSummaryMarkdown = "";
                _personEmptyStateVisible = true;
                _personSummaryStates[friend.Id] = new SummaryState(_personSummaryMarkdown, allMessages, true);
                PersonEvidenceSubtitle.Text = "当前日期范围和关键词下没有可展示的原始消息。";
                PersonEvidenceList.Items.Clear();
                PersonEvidenceList.Items.Add(CreateEvidenceItem("本机暂无聊天记录", "这台电脑里没有读取到你和这位好友的相关聊天记录。可能是刚在新电脑登录微信，或历史消息没有迁移到本机。"));
                ApplyPersonSummaryView();
                return;
            }

            var messages = allMessages;

            _personEmptyStateVisible = false;
            TrySyncMessagesToLocalDb(friend.Id, "private", friend.DisplayName, allMessages);
            RefreshPersonEvidence(allMessages);
            var resolvedTemplate = await ResolveGenerationTemplateAsync("好友分析", PersonGenerationModeCombo, PersonNaturalInstructionBox.Text, template);
            if (resolvedTemplate is null)
            {
                return;
            }

            template = resolvedTemplate;
            _personSummaryMarkdown = await _summaryService.SummarizeAsync(messages, template, CurrentConfigFromUi());
            if (IsAiFailureResponse(_personSummaryMarkdown))
            {
                _personSummaryMarkdown = BuildAiFailureText(_personSummaryMarkdown);
            }
            _personSummaryStates[friend.Id] = new SummaryState(_personSummaryMarkdown, allMessages, false);
            RefreshPersonEvidence(allMessages, _personSummaryMarkdown);
            PersonMemoSuggestionPanel.Visibility = Visibility.Collapsed;
            ApplyPersonSummaryView();
            RefreshAnalysisOverviewIfVisible();
        }
        catch (Exception ex)
        {
            _personSummaryMarkdown = BuildGenerateErrorText(ex);
            _personEmptyStateVisible = false;
            _personSummaryStates[friend.Id] = new SummaryState(_personSummaryMarkdown, [], false);
            ApplyPersonSummaryView();
            RefreshAnalysisOverviewIfVisible();
        }
        finally
        {
            GeneratePersonButton.IsEnabled = true;
            SetGeneratingStatus(PersonGeneratingStatus, false);
            RefreshAnalysisOverviewIfVisible();
        }
    }

    private static void SetGeneratingStatus(UIElement status, bool isVisible)
    {
        status.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TryEnsureLocalDb()
    {
        try
        {
            _chatBriefDb.EnsureSchema();
        }
        catch
        {
            // 本机索引失败不阻断主程序启动，后续生成流程仍可直接读取微信数据。
        }
    }

    private bool TrySyncCatalogToLocalDb()
    {
        try
        {
            _chatBriefDb.UpsertCatalog(_chatRooms, _friends);
            return true;
        }
        catch (Exception ex)
        {
            SettingsDataStatusText.Text = $"已读取 {_chatRooms.Count} 个群聊、{_friends.Count} 位好友；本地缓存暂未同步：{ex.Message}";
            return false;
        }
    }

    private void TrySyncMessagesToLocalDb(string conversationId, string type, string displayName, IReadOnlyList<ChatMessage> messages)
    {
        try
        {
            _chatBriefDb.SyncConversationMessages(conversationId, type, displayName, messages);
        }
        catch (Exception ex)
        {
            SettingsDataStatusText.Text = $"本次总结可以继续生成，但本地缓存同步失败：{ex.Message}";
        }
    }

    private void RefreshGroupEvidence(IReadOnlyList<ChatMessage> messages, string? summaryMarkdown = null)
    {
        RefreshGroupSpeakerFilter(messages);
        var filteredMessages = ApplySpeakerFilter(messages, _groupSpeakerFilter);
        FillEvidenceList(GroupEvidenceList, filteredMessages, GroupEvidenceSubtitle, summaryMarkdown);
        UpdateSpeakerFilterButton(GroupSpeakerFilterButton, _groupSpeakerFilter, messages);
    }

    private void RefreshPersonEvidence(IReadOnlyList<ChatMessage> messages, string? summaryMarkdown = null)
    {
        FillEvidenceList(PersonEvidenceList, messages, PersonEvidenceSubtitle, summaryMarkdown);
    }

    private void RefreshGroupSpeakerFilter(IReadOnlyList<ChatMessage> messages)
    {
        RefreshSpeakerFilter(GroupSpeakerFilterList, GroupSpeakerFilterButton, GroupSpeakerSearchBox, messages, _groupSpeakerFilter);
    }

    private void RefreshPersonSpeakerFilter(IReadOnlyList<ChatMessage> messages)
    {
        RefreshSpeakerFilter(PersonSpeakerFilterList, PersonSpeakerFilterButton, PersonSpeakerSearchBox, messages, _personSpeakerFilter);
    }

    private void RefreshSpeakerFilter(StackPanel host, Button button, TextBox searchBox, IReadOnlyList<ChatMessage> messages, HashSet<string> filter)
    {
        _isRefreshingSpeakerFilter = true;
        try
        {
            var speakers = messages
                .Where(message => !string.IsNullOrWhiteSpace(message.SenderDisplayName))
                .GroupBy(message => message.SenderDisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(group => new SpeakerOption(group.Key, group.Count()))
                .OrderByDescending(option => option.Count)
                .ThenBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            filter.RemoveWhere(name => speakers.All(option => !string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase)));
            host.Children.Clear();
            var needsSearch = speakers.Count > SpeakerSearchThreshold;
            searchBox.Visibility = needsSearch ? Visibility.Visible : Visibility.Collapsed;
            if (!needsSearch && !string.IsNullOrWhiteSpace(searchBox.Text))
            {
                searchBox.Text = "";
            }

            if (speakers.Count == 0)
            {
                host.Children.Add(new TextBlock
                {
                    Text = "生成后可按发言人筛选。",
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                });
                UpdateSpeakerFilterButton(button, filter, messages);
                return;
            }

            var query = needsSearch ? searchBox.Text.Trim() : "";
            var visibleSpeakers = string.IsNullOrWhiteSpace(query)
                ? speakers.Take(needsSearch ? SpeakerVisibleLimit : speakers.Count).ToList()
                : speakers
                    .Where(option => option.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Take(SpeakerVisibleLimit)
                    .ToList();

            if (needsSearch && string.IsNullOrWhiteSpace(query) && speakers.Count > SpeakerVisibleLimit)
            {
                host.Children.Add(new TextBlock
                {
                    Text = $"共 {speakers.Count} 位发言人，先显示发言最多的 {SpeakerVisibleLimit} 位。",
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 0, 2, 8)
                });
            }

            if (visibleSpeakers.Count == 0)
            {
                host.Children.Add(new TextBlock
                {
                    Text = "没有匹配的发言人。",
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 4, 2, 4)
                });
                UpdateSpeakerFilterButton(button, filter, messages);
                return;
            }

            var panel = new WrapPanel
            {
                Margin = new Thickness(0, 0, 0, 2)
            };
            foreach (var speaker in visibleSpeakers)
            {
                panel.Children.Add(CreateSpeakerChip(speaker, filter.Count == 0 || filter.Contains(speaker.Name)));
            }

            host.Children.Add(panel);

            UpdateSpeakerFilterButton(button, filter, messages);
        }
        finally
        {
            _isRefreshingSpeakerFilter = false;
        }
    }

    private Button CreateSpeakerChip(SpeakerOption speaker, bool isSelected)
    {
        var button = new Button
        {
            Tag = speaker.Name,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 8, 8),
            MinHeight = 32,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(isSelected ? Color.FromRgb(7, 193, 96) : Color.FromRgb(226, 232, 240)),
            Background = new SolidColorBrush(isSelected ? Color.FromRgb(231, 248, 239) : Color.FromRgb(248, 250, 252)),
            Foreground = new SolidColorBrush(isSelected ? Color.FromRgb(6, 173, 86) : Color.FromRgb(51, 65, 85)),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock
                    {
                        Text = speaker.Name,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(isSelected ? Color.FromRgb(6, 173, 86) : Color.FromRgb(51, 65, 85)),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 120
                    },
                    new TextBlock
                    {
                        Text = $"  {speaker.Count}",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(isSelected ? Color.FromRgb(6, 173, 86) : Color.FromRgb(100, 116, 139))
                    }
                }
            }
        };
        button.Click += SpeakerChipClicked;
        return button;
    }

    private void ToggleSpeakerFilterClicked(object sender, RoutedEventArgs e)
    {
        RefreshGroupSpeakerFilter(CurrentGroupMessages());
        GroupSpeakerFilterPopup.IsOpen = !GroupSpeakerFilterPopup.IsOpen;
    }

    private void ClearSpeakerFilterClicked(object sender, RoutedEventArgs e)
    {
        _groupSpeakerFilter.Clear();
        RefreshGroupSpeakerFilter(CurrentGroupMessages());
        RefreshGroupEvidenceOnly();
    }

    private void ApplySpeakerFilterAndRegenerateClicked(object sender, RoutedEventArgs e)
    {
        GroupSpeakerFilterPopup.IsOpen = false;
        PersonSpeakerFilterPopup.IsOpen = false;

        GenerateGroupSummaryClicked(sender, e);
    }

    private void SpeakerFilterSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_isRefreshingSpeakerFilter)
        {
            return;
        }

        RefreshGroupSpeakerFilter(CurrentGroupMessages());
    }

    private void SpeakerChipClicked(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingSpeakerFilter || sender is not Button { Tag: string speakerName })
        {
            return;
        }

        var messages = CurrentGroupMessages();
        var filter = _groupSpeakerFilter;
        var allNames = SpeakerNames(messages);
        if (allNames.Count == 0)
        {
            return;
        }

        if (filter.Count == 0)
        {
            foreach (var name in allNames)
            {
                filter.Add(name);
            }
        }

        if (filter.Contains(speakerName))
        {
            if (filter.Count == 1)
            {
                return;
            }

            filter.Remove(speakerName);
        }
        else
        {
            filter.Add(speakerName);
        }

        if (filter.Count == allNames.Count)
        {
            filter.Clear();
        }

        RefreshGroupSpeakerFilter(messages);
        RefreshGroupEvidenceOnly();
    }

    private void SpeakerFilterCheckboxChanged(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingSpeakerFilter)
        {
            return;
        }

        var usePerson = IsChildOf(sender as DependencyObject, PersonSpeakerFilterList);
        var host = usePerson ? PersonSpeakerFilterList : GroupSpeakerFilterList;
        var filter = usePerson ? _personSpeakerFilter : _groupSpeakerFilter;
        var allNames = host.Children
            .OfType<CheckBox>()
            .Select(box => box.Tag as string)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToList();
        var selectedNames = host.Children
            .OfType<CheckBox>()
            .Where(box => box.IsChecked == true)
            .Select(box => box.Tag as string)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToList();

        if (selectedNames.Count == 0)
        {
            _isRefreshingSpeakerFilter = true;
            if (sender is CheckBox checkBox)
            {
                checkBox.IsChecked = true;
            }
            _isRefreshingSpeakerFilter = false;
            return;
        }

        filter.Clear();
        if (selectedNames.Count != allNames.Count)
        {
            foreach (var name in selectedNames)
            {
                filter.Add(name);
            }
        }

        if (usePerson)
        {
            UpdateSpeakerFilterButton(PersonSpeakerFilterButton, _personSpeakerFilter, CurrentPersonMessages());
            RefreshPersonEvidenceOnly();
            return;
        }

        UpdateSpeakerFilterButton(GroupSpeakerFilterButton, _groupSpeakerFilter, CurrentGroupMessages());
        RefreshGroupEvidenceOnly();
    }

    private void RefreshGroupEvidenceOnly()
    {
        FillEvidenceList(GroupEvidenceList, ApplySpeakerFilter(CurrentGroupMessages(), _groupSpeakerFilter), GroupEvidenceSubtitle, _groupSummaryMarkdown);
    }

    private void RefreshPersonEvidenceOnly()
    {
        FillEvidenceList(PersonEvidenceList, CurrentPersonMessages(), PersonEvidenceSubtitle, _personSummaryMarkdown);
    }

    private static IReadOnlyList<ChatMessage> ApplySpeakerFilter(IReadOnlyList<ChatMessage> messages, HashSet<string> filter)
    {
        if (filter.Count == 0)
        {
            return messages;
        }

        return messages
            .Where(message => filter.Contains(message.SenderDisplayName))
            .ToList();
    }

    private static void UpdateSpeakerFilterButton(Button button, HashSet<string> filter, IReadOnlyList<ChatMessage> messages)
    {
        var speakerCount = SpeakerNames(messages).Count;
        button.Content = filter.Count == 0 || filter.Count >= speakerCount ? "发言人" : $"{filter.Count} 位";
    }

    private static List<string> SpeakerNames(IReadOnlyList<ChatMessage> messages)
    {
        return messages
            .Select(message => message.SenderDisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
    }

    private bool ShouldUsePersonSpeakerFilter()
    {
        return false;
    }

    private static bool IsChildOf(DependencyObject? child, DependencyObject parent)
    {
        while (child is not null)
        {
            if (ReferenceEquals(child, parent))
            {
                return true;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return false;
    }

    private static void FillEvidenceList(ListBox list, IReadOnlyList<ChatMessage> messages, TextBlock? subtitle = null, string? summaryMarkdown = null)
    {
        list.Items.Clear();
        if (messages.Count == 0)
        {
            if (subtitle is not null)
            {
                subtitle.Text = "当前日期范围和关键词下没有可展示的原始消息。";
            }

            list.Items.Add(CreateEvidenceItem("提示", "当前日期范围和关键词下没有消息。"));
            return;
        }

        var citedNumbers = ExtractEvidenceNumbers(summaryMarkdown);
        var segments = BuildEvidenceSegments(messages, citedNumbers);
        if (subtitle is not null)
        {
            var citedSegmentCount = segments.Count(segment => segment.CitedCount > 0);
            subtitle.Text = citedNumbers.Count == 0
                ? $"共 {messages.Count} 条，按时间段整理为 {segments.Count} 个片段。生成文档后，可点击 [#001] 编号核对原文。"
                : $"共 {messages.Count} 条，{segments.Count} 个片段；AI 文档引用了 {citedNumbers.Count} 条原文，分布在 {citedSegmentCount} 个片段。";
        }

        foreach (var segment in segments)
        {
            list.Items.Add(CreateEvidenceSegmentHeader(segment));
            foreach (var evidence in segment.Items)
            {
                var message = evidence.Message;
                list.Items.Add(CreateEvidenceItem($"#{evidence.Number:000}  {message.Time:MM-dd HH:mm}  {message.SenderDisplayName}", message.Content, evidence.Number, citedNumbers.Contains(evidence.Number)));
            }
        }
    }

    private static ListBoxItem CreateEvidenceSegmentHeader(EvidenceSegment segment)
    {
        var isCited = segment.CitedCount > 0;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = $"片段 {segment.Index} · {segment.Start:MM-dd HH:mm} - {segment.End:HH:mm}",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = isCited ? new SolidColorBrush(Color.FromRgb(6, 173, 86)) : new SolidColorBrush(Color.FromRgb(71, 85, 105)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var badge = new Border
        {
            Background = isCited ? new SolidColorBrush(Color.FromRgb(220, 252, 231)) : new SolidColorBrush(Color.FromRgb(241, 245, 249)),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 3, 8, 3),
            Child = new TextBlock
            {
                Text = isCited ? $"引用 {segment.CitedCount}" : $"{segment.Items.Count} 条",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = isCited ? new SolidColorBrush(Color.FromRgb(6, 173, 86)) : new SolidColorBrush(Color.FromRgb(100, 116, 139))
            }
        };
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        return new ListBoxItem
        {
            IsHitTestVisible = false,
            Focusable = false,
            Content = new Border
            {
                Background = isCited ? new SolidColorBrush(Color.FromRgb(247, 253, 249)) : new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 8, 10, 8),
                Child = grid
            },
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, segment.Index == 1 ? 0 : 8, 0, 6)
        };
    }

    private static ListBoxItem CreateEvidenceItem(string meta, string content, int? evidenceNumber = null, bool isCited = false)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var metaText = new TextBlock
        {
            Text = meta,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        stack.Children.Add(metaText);
        var bodyText = new TextBlock
        {
            Text = content,
            FontSize = 13,
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        stack.Children.Add(bodyText);

        var card = new Border
        {
            Background = isCited ? new SolidColorBrush(Color.FromRgb(247, 253, 249)) : Brushes.Transparent,
            BorderBrush = isCited ? new SolidColorBrush(Color.FromRgb(187, 247, 208)) : Brushes.Transparent,
            BorderThickness = isCited ? new Thickness(1) : new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 9, 10, 9),
            Child = stack,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var isEvidence = evidenceNumber.HasValue;
        return new ListBoxItem
        {
            Tag = evidenceNumber,
            IsHitTestVisible = isEvidence,
            Focusable = isEvidence,
            Content = card,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 0, 0, 6)
        };
    }

    private static HashSet<int> ExtractEvidenceNumbers(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        return Regex.Matches(markdown, @"\[#(\d{1,4})\]")
            .Select(match => int.TryParse(match.Groups[1].Value, out var number) ? number : 0)
            .Where(number => number > 0)
            .ToHashSet();
    }

    private static List<EvidenceSegment> BuildEvidenceSegments(IReadOnlyList<ChatMessage> messages, HashSet<int> citedNumbers)
    {
        var segments = new List<EvidenceSegment>();
        var current = new List<EvidenceItem>();
        DateTime? previous = null;

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (previous is not null && message.Time - previous.Value > TimeSpan.FromMinutes(30))
            {
                AddEvidenceSegment(segments, current, citedNumbers);
                current = [];
            }

            current.Add(new EvidenceItem(index + 1, message));
            previous = message.Time;
        }

        AddEvidenceSegment(segments, current, citedNumbers);
        return segments;
    }

    private static void AddEvidenceSegment(List<EvidenceSegment> segments, List<EvidenceItem> items, HashSet<int> citedNumbers)
    {
        if (items.Count == 0)
        {
            return;
        }

        var citedCount = items.Count(item => citedNumbers.Contains(item.Number));
        segments.Add(new EvidenceSegment(
            segments.Count + 1,
            items[0].Message.Time,
            items[^1].Message.Time,
            citedCount,
            items));
    }

    private static void FocusEvidenceItem(ListBox list, int evidenceNumber)
    {
        var item = list.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(candidate => candidate.Tag is int number && number == evidenceNumber);
        if (item is null)
        {
            return;
        }

        list.SelectedItem = item;
        item.IsSelected = true;

        list.Dispatcher.BeginInvoke(new Action(() =>
        {
            list.ScrollIntoView(item);
            list.UpdateLayout();
            item.BringIntoView();

            var target = item.Content as Border;
            var original = target?.Background ?? item.Background;
            var originalBorder = target?.BorderBrush;
            var originalThickness = target?.BorderThickness ?? new Thickness(0);
            var highlight = new SolidColorBrush(Color.FromRgb(231, 248, 239));
            if (target is not null)
            {
                target.Background = highlight;
                target.BorderBrush = new SolidColorBrush(Color.FromRgb(7, 193, 96));
                target.BorderThickness = new Thickness(1);
            }
            else
            {
                item.Background = highlight;
            }
            highlight.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            {
                From = Color.FromRgb(209, 250, 229),
                To = Color.FromRgb(255, 255, 255),
                Duration = new Duration(TimeSpan.FromMilliseconds(620)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(680)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (target is not null)
                {
                    target.Background = original;
                    target.BorderBrush = originalBorder;
                    target.BorderThickness = originalThickness;
                }
                else
                {
                    item.Background = original;
                }
            };
            timer.Start();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private async Task<PromptTemplate?> ResolveGenerationTemplateAsync(string category, ComboBox modeCombo, string naturalInstruction, PromptTemplate fallback)
    {
        if (IsTemplateMode(modeCombo))
        {
            return fallback;
        }

        if (string.IsNullOrWhiteSpace(naturalInstruction))
        {
            ShowAppNotice("生成总结", "请先填写 AI 自定义要求，或切换为使用模板。", "warning");
            return null;
        }

        return await ResolveTemplateForRunAsync(category, naturalInstruction, fallback);
    }

    private static bool IsTemplateMode(ComboBox modeCombo)
    {
        return modeCombo.SelectedItem is ComboBoxItem item &&
               item.Content?.ToString()?.Contains("模板", StringComparison.Ordinal) == true;
    }

    private void GenerationModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupCustomInstructionPanel is not null)
        {
            var groupUsesTemplate = IsTemplateMode(GroupGenerationModeCombo);
            GroupCustomInstructionPanel.Visibility = groupUsesTemplate ? Visibility.Collapsed : Visibility.Visible;
            GroupTemplatePanel.Visibility = groupUsesTemplate ? Visibility.Visible : Visibility.Collapsed;
        }

        if (PersonCustomInstructionPanel is not null)
        {
            var personUsesTemplate = IsTemplateMode(PersonGenerationModeCombo);
            PersonCustomInstructionPanel.Visibility = personUsesTemplate ? Visibility.Collapsed : Visibility.Visible;
            PersonTemplatePanel.Visibility = personUsesTemplate ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async Task<PromptTemplate> ResolveTemplateForRunAsync(string category, string naturalInstruction, PromptTemplate fallback)
    {
        if (string.IsNullOrWhiteSpace(naturalInstruction))
        {
            return fallback;
        }

        var generated = await _summaryService.GenerateTemplateAsync(category, naturalInstruction.Trim(), CurrentConfigFromUi());
        if (string.IsNullOrWhiteSpace(generated) || generated.StartsWith("AI 模板生成失败", StringComparison.Ordinal))
        {
            return new PromptTemplate("临时自然语言总结", category, BuildNaturalInstructionTemplate(naturalInstruction), false);
        }

        if (!generated.Contains("{messages}", StringComparison.Ordinal))
        {
            generated += "\n\n共 {count} 条消息：\n{messages}";
        }

        return new PromptTemplate("临时自然语言总结", category, generated, false);
    }

    private static string BuildNaturalInstructionTemplate(string naturalInstruction)
    {
        return $$"""
        请根据用户的自然语言要求整理下面的聊天记录。

        用户要求：
        {{naturalInstruction.Trim()}}

        输出规则：
        - 只根据聊天记录总结，不要编造。
        - 不确定的人、时间、状态写“待确认”。
        - “我/本机”以及用户在设置中填写的称呼代表软件使用者本人。
        - 输出 Markdown，层级清晰。
        - 如涉及待办，请尽量列出事项、负责人、状态、时间和证据消息。

        共 {count} 条消息：
        {messages}
        """;
    }

    private void SummaryViewModeChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyGroupSummaryView();
        ApplyPersonSummaryView();
    }

    private void ApplyGroupSummaryView()
    {
        if (GroupSummaryBox is null)
        {
            return;
        }

        var hasAiFailure = TryParseAiFailure(_groupSummaryMarkdown, out var failureTitle, out var failureBody, out var failureHint);
        GroupAiFailureState.Visibility = hasAiFailure ? Visibility.Visible : Visibility.Collapsed;
        GroupEmptyState.Visibility = !hasAiFailure && _groupEmptyStateVisible ? Visibility.Visible : Visibility.Collapsed;
        if (hasAiFailure)
        {
            GroupAiFailureTitle.Text = failureTitle;
            GroupAiFailureBody.Text = failureBody;
            GroupAiFailureHint.Text = failureHint;
        }
        SetSummaryViewDocument(GroupSummaryBox, _groupSummaryMarkdown, "文档视图", PersonNamesForHighlight(CurrentGroupMessages()), number => FocusEvidenceItem(GroupEvidenceList, number));
    }

    private void ApplyPersonSummaryView()
    {
        if (PersonSummaryBox is null)
        {
            return;
        }

        var hasAiFailure = TryParseAiFailure(_personSummaryMarkdown, out var failureTitle, out var failureBody, out var failureHint);
        PersonAiFailureState.Visibility = hasAiFailure ? Visibility.Visible : Visibility.Collapsed;
        PersonEmptyState.Visibility = !hasAiFailure && _personEmptyStateVisible ? Visibility.Visible : Visibility.Collapsed;
        if (hasAiFailure)
        {
            PersonAiFailureTitle.Text = failureTitle;
            PersonAiFailureBody.Text = failureBody;
            PersonAiFailureHint.Text = failureHint;
        }
        SetSummaryViewDocument(PersonSummaryBox, _personSummaryMarkdown, "文档视图", PersonNamesForHighlight(CurrentPersonMessages()), number => FocusEvidenceItem(PersonEvidenceList, number));
    }

    private static string SelectedViewMode(ComboBox? comboBox)
    {
        if (comboBox?.SelectedItem is ComboBoxItem item && item.Content is string text)
        {
            return text;
        }

        return "文档视图";
    }

    private IReadOnlyList<ChatMessage> CurrentGroupMessages()
    {
        var room = CurrentSelectedChatRoom();
        return room is not null && _groupSummaryStates.TryGetValue(room.Id, out var state) ? state.Messages : [];
    }

    private Task<IReadOnlyList<ChatMessage>> GetGroupMessagesAsync(ChatRoom room, DateTime start, DateTime end, string? keyword)
    {
        if (_importedGroupMessages.TryGetValue(room.Id, out var importedMessages))
        {
            var filtered = importedMessages
                .Where(message => message.Time >= start && message.Time <= end)
                .Where(message => string.IsNullOrWhiteSpace(keyword) ||
                                  message.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                                  message.SenderDisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return Task.FromResult<IReadOnlyList<ChatMessage>>(filtered);
        }

        return _dataService.GetMessagesAsync(room.Id, start, end, keyword);
    }

    private IReadOnlyList<ChatMessage> CurrentPersonMessages()
    {
        var friend = CurrentSelectedFriend();
        return friend is not null && _personSummaryStates.TryGetValue(friend.Id, out var state) ? state.Messages : [];
    }

    private static IReadOnlyList<string> PersonNamesForHighlight(IReadOnlyList<ChatMessage> messages)
    {
        return messages
            .Select(message => message.SenderDisplayName)
            .Where(name => IsHighlightablePersonName(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(name => name.Length)
            .Take(4)
            .ToList();
    }

    private static bool IsHighlightablePersonName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        if (trimmed == "我/本机") return false;
        if (trimmed.StartsWith("wxid_", StringComparison.OrdinalIgnoreCase)) return false;
        if (trimmed.StartsWith("未知成员", StringComparison.OrdinalIgnoreCase)) return false;
        return trimmed.Length >= 2;
    }

    private static void SetSummaryViewDocument(RichTextBox viewer, string markdown, string viewMode, IReadOnlyList<string>? highlightNames = null, Action<int>? evidenceClick = null)
    {
        if (viewMode == "文档视图")
        {
            if (TryParseAiFailure(markdown, out var failureTitle, out var failureBody, out var failureHint))
            {
                SetAiFailureDocument(viewer, failureTitle, failureBody, failureHint);
                AnimateSummaryViewer(viewer);
                return;
            }

            if (IsSummaryPlaceholder(markdown))
            {
                SetEmptySummaryDocument(viewer, markdown);
                AnimateSummaryViewer(viewer);
                return;
            }

            SetMarkdownDocument(viewer, markdown, highlightNames, evidenceClick);
            AnimateSummaryViewer(viewer);
            return;
        }

        var document = CreateBaseFlowDocument();
        switch (viewMode)
        {
            case "待办视图":
                AddVisualHeader(document, "待办视图", "按待确认、进行中、已完成组织当前分析中的行动项。");
                AddTodoBoard(document, ExtractTodoLines(markdown));
                break;
            case "思维导图":
                AddVisualHeader(document, "思维导图", "把文档标题和要点转成层级结构，便于快速理解讨论脉络。");
                AddMindMapTree(document, ExtractMindMapNodes(markdown));
                break;
            default:
                SetMarkdownDocument(viewer, markdown, highlightNames, evidenceClick);
                return;
        }

        viewer.Document = document;
        AnimateSummaryViewer(viewer);
    }

    private static bool IsSummaryPlaceholder(string markdown)
    {
        var text = (markdown ?? string.Empty).Trim();
        return text == "选择群聊后生成总结。"
            || text == "选择好友后生成总结。"
            || (text.StartsWith("已选择「", StringComparison.Ordinal) &&
                (text.Contains("生成后，这里会显示", StringComparison.Ordinal) || text.Contains("生成后将呈现", StringComparison.Ordinal)));
    }

    private static bool TryParseAiFailure(string markdown, out string title, out string body, out string hint)
    {
        title = "";
        body = "";
        hint = "";
        var text = (markdown ?? string.Empty).Trim();
        if (!text.StartsWith("[[CHATBRIEF_AI_FAILURE]]", StringComparison.Ordinal))
        {
            return false;
        }

        var lines = text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        title = lines.FirstOrDefault(line => line.StartsWith("title=", StringComparison.Ordinal))?.Replace("title=", "") ?? "AI 生成失败";
        body = lines.FirstOrDefault(line => line.StartsWith("body=", StringComparison.Ordinal))?.Replace("body=", "") ?? "当前无法完成生成。";
        hint = lines.FirstOrDefault(line => line.StartsWith("hint=", StringComparison.Ordinal))?.Replace("hint=", "") ?? "请检查 API Key 或稍后重试。";
        return true;
    }

    private static void SetAiFailureDocument(RichTextBox viewer, string title, string body, string hint)
    {
        var document = CreateBaseFlowDocument();
        var stack = new StackPanel
        {
            Width = 430,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var lottie = CreateSafeLottieAnimation("pack://application:,,,/Assets/invalid-state.json", 180, 150);
        if (lottie is not null)
        {
            lottie.Margin = new Thickness(0, 0, 0, 10);
            stack.Children.Add(lottie);
        }

        stack.Children.Add(new TextBlock
        {
            Text = title,
            TextAlignment = TextAlignment.Center,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
            Margin = new Thickness(0, 0, 0, 8)
        });
        stack.Children.Add(new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(75, 85, 99)),
            FontSize = 14,
            LineHeight = 22,
            Margin = new Thickness(0, 0, 0, 12)
        });
        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(247, 249, 252)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 14, 10),
            Child = new TextBlock
            {
                Text = hint,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                FontSize = 13,
                LineHeight = 20
            }
        });

        AddCardBlock(document, new Grid
        {
            MinHeight = 500,
            Children =
            {
                stack
            }
        });
        viewer.Document = document;
    }

    private static void SetEmptySummaryDocument(RichTextBox viewer, string text)
    {
        var document = CreateBaseFlowDocument();
        var stack = new StackPanel
        {
            Width = 380,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        stack.Children.Add(CreateCatBoxEmptyVisual());

        stack.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            FontSize = 14,
            LineHeight = 22
        });

        AddCardBlock(document, new Grid
        {
            MinHeight = 500,
            Children =
            {
                stack
            }
        });
        viewer.Document = document;
    }

    private static FrameworkElement CreateCatBoxEmptyVisual()
    {
        var image = new Image
        {
            Width = 160,
            Height = 132,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
            Source = new BitmapImage(new Uri("pack://application:,,,/WeChatSummary.Desktop;component/Assets/cat-popping-box.gif"))
        };
        BeginGifAnimation(image);
        return image;
    }

    private void EmptyGifLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
        {
            BeginGifAnimation(image);
        }
    }

    private static void BeginGifAnimation(Image image)
    {
        if (image.Tag is DispatcherTimer existingTimer)
        {
            existingTimer.Stop();
        }

        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/WeChatSummary.Desktop;component/Assets/cat-popping-box.gif"));
        if (resource?.Stream is null)
        {
            return;
        }

        using var stream = resource.Stream;
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frames = decoder.Frames.ToList();
        if (frames.Count == 0)
        {
            return;
        }

        foreach (var frame in frames)
        {
            if (frame.CanFreeze)
            {
                frame.Freeze();
            }
        }

        var delays = frames.Select(ReadGifFrameDelay).ToList();
        var index = 0;
        image.Source = frames[0];

        var timer = new DispatcherTimer { Interval = delays[0] };
        timer.Tick += (_, _) =>
        {
            index = (index + 1) % frames.Count;
            image.Source = frames[index];
            timer.Interval = delays[index];
        };
        image.Tag = timer;
        timer.Start();
    }

    private static TimeSpan ReadGifFrameDelay(BitmapFrame frame)
    {
        const int defaultDelayMs = 90;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata &&
                metadata.GetQuery("/grctlext/Delay") is ushort delay)
            {
                return TimeSpan.FromMilliseconds(Math.Max(20, delay * 10));
            }
        }
        catch
        {
            return TimeSpan.FromMilliseconds(defaultDelayMs);
        }

        return TimeSpan.FromMilliseconds(defaultDelayMs);
    }

    private static LottieSharp.WPF.LottieAnimationView? CreateSafeLottieAnimation(string resourcePath, double width, double height)
    {
        try
        {
            return new LottieSharp.WPF.LottieAnimationView
            {
                ResourcePath = resourcePath,
                Width = width,
                Height = height,
                AutoPlay = true,
                RepeatCount = -1,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }
        catch
        {
            return null;
        }
    }

    private static void AnimateSummaryViewer(RichTextBox viewer)
    {
        var translate = new TranslateTransform(0, 8);
        viewer.RenderTransform = translate;
        viewer.Opacity = 0.72;

        viewer.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0.72,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = 8,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static List<string> ExtractTodoLines(string markdown)
    {
        return ExtractMeaningfulLines(markdown)
            .Where(line => Regex.IsMatch(line, "任务|待办|负责|截止|完成|跟进|提交|安排|todo", RegexOptions.IgnoreCase))
            .Take(24)
            .ToList();
    }

    private static List<MindMapNode> ExtractMindMapNodes(string markdown)
    {
        var roots = new List<MindMapNode>();
        MindMapNode? currentRoot = null;
        MindMapNode? currentBranch = null;

        foreach (var rawLine in (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || IsMarkdownFence(line) || IsMarkdownDivider(line))
            {
                continue;
            }

            var heading = ParseHeading(line);
            if (heading is not null)
            {
                var node = new MindMapNode(heading.Value.Text);
                if (heading.Value.Level <= 2 || currentRoot is null)
                {
                    roots.Add(node);
                    currentRoot = node;
                    currentBranch = null;
                }
                else
                {
                    currentRoot.Children.Add(node);
                    currentBranch = node;
                }
                continue;
            }

            if (!IsListLine(line) && !LooksLikeTableRow(line))
            {
                continue;
            }

            var text = CleanMarkdownInlineKeepBold(StripListMarker(line.Trim('|', ' ')));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (currentRoot is null)
            {
                currentRoot = new MindMapNode("分析结果");
                roots.Add(currentRoot);
            }

            if (currentBranch is null || currentBranch.Children.Count >= 6)
            {
                currentBranch = new MindMapNode(text.Length > 28 ? text[..28] + "..." : text);
                currentRoot.Children.Add(currentBranch);
            }
            else
            {
                currentBranch.Children.Add(new MindMapNode(text));
            }
        }

        if (roots.Count == 0)
        {
            var fallback = ExtractMeaningfulLines(markdown ?? string.Empty).Take(8).ToList();
            if (fallback.Count > 0)
            {
                roots.Add(new MindMapNode("分析结果", fallback.Select(line => new MindMapNode(CleanMarkdownInlineKeepBold(StripListMarker(line)))).ToList()));
            }
        }

        return roots;
    }

    private static List<string> ExtractMeaningfulLines(string markdown)
    {
        return (markdown ?? string.Empty)
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !IsMarkdownFence(line) && !IsMarkdownDivider(line))
            .Where(line => !Regex.IsMatch(line, @"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?$"))
            .Select(line => line.Trim('|', ' '))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct()
            .ToList();
    }

    private static FlowDocument CreateBaseFlowDocument()
    {
        return new FlowDocument
        {
            FontFamily = new FontFamily("PingFang SC"),
            FontSize = 14,
            PagePadding = new Thickness(0),
            LineHeight = 22,
            Foreground = new SolidColorBrush(Color.FromRgb(31, 35, 41))
        };
    }

    private static void AddVisualHeader(FlowDocument document, string title, string description)
    {
        document.Blocks.Add(CreateParagraph(title, 24, FontWeights.SemiBold, new Thickness(0, 2, 0, 6)));
        document.Blocks.Add(CreateParagraph(description, 13, FontWeights.Normal, new Thickness(0, 0, 0, 16), new SolidColorBrush(Color.FromRgb(107, 114, 128))));
    }

    private static void AddTodoBoard(FlowDocument document, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            AddEmptyVisualState(document, "当前总结里没有明显的待办事项。");
            return;
        }

        var board = new StackPanel();

        var todo = lines.Where(line => !Regex.IsMatch(line, "进行|处理中|完成|已", RegexOptions.IgnoreCase)).ToList();
        var doing = lines.Where(line => Regex.IsMatch(line, "进行|处理中|推进|待确认|确认", RegexOptions.IgnoreCase)).ToList();
        var done = lines.Where(line => Regex.IsMatch(line, "完成|已|done", RegexOptions.IgnoreCase)).ToList();

        AddKanbanLane(board, "待确认", todo.Count > 0 ? todo : lines.Take(Math.Max(1, lines.Count / 2)).ToList(), Color.FromRgb(245, 158, 11));
        AddKanbanLane(board, "进行中", doing.Count > 0 ? doing : lines.Skip(Math.Max(1, lines.Count / 2)).Take(Math.Max(1, lines.Count / 3)).ToList(), Color.FromRgb(59, 130, 246));
        AddKanbanLane(board, "已完成", done, Color.FromRgb(7, 193, 96));

        AddCardBlock(document, board);
    }

    private static void AddKanbanLane(StackPanel board, string title, IReadOnlyList<string> lines, Color accent)
    {
        var lane = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        lane.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(24, accent.R, accent.G, accent.B)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock
            {
                Text = $"{title} · {lines.Count}",
                Foreground = new SolidColorBrush(accent),
                FontWeight = FontWeights.SemiBold
            }
        });

        var cards = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 210,
            MinWidth = 440
        };

        if (lines.Count == 0)
        {
            cards.Children.Add(CreateMiniCard("暂无事项", "当前没有归入这一状态的行动项。", accent));
        }
        else
        {
            for (var i = 0; i < lines.Count; i++)
            {
                cards.Children.Add(CreateMiniCard($"#{i + 1:00}", CleanMarkdownInlineKeepBold(StripListMarker(lines[i])), accent));
            }
        }

        lane.Children.Add(cards);
        board.Children.Add(lane);
    }

    private static void AddMindMapTree(FlowDocument document, IReadOnlyList<MindMapNode> nodes)
    {
        if (nodes.Count == 0)
        {
            AddEmptyVisualState(document, "当前文档还没有形成可展示的层级结构。");
            return;
        }

        var map = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };

        var rootTitle = nodes.Count == 1 ? nodes[0].Title : "分析结构";
        var rootCard = CreateMindMapBubble(rootTitle, 0, Color.FromRgb(7, 193, 96));
        rootCard.HorizontalAlignment = HorizontalAlignment.Center;
        rootCard.Width = 260;
        rootCard.Margin = new Thickness(0, 0, 0, 16);
        map.Children.Add(rootCard);

        var branchSource = nodes.Count == 1 && nodes[0].Children.Count > 0 ? nodes[0].Children : nodes;
        foreach (var node in branchSource.Take(10))
        {
            map.Children.Add(CreateMindMapBranch(node));
        }

        AddCardBlock(document, map);
    }

    private static Border CreateMindMapBranch(MindMapNode node)
    {
        var stack = new StackPanel();
        stack.Children.Add(CreateMindMapBubble(node.Title, 1, Color.FromRgb(59, 130, 246)));

        foreach (var child in node.Children.Take(6))
        {
            var row = new Grid { Margin = new Thickness(18, 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Border
            {
                Width = 16,
                Height = 2,
                Background = new SolidColorBrush(Color.FromRgb(221, 226, 232)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            var childBubble = CreateMindMapBubble(child.Title, 2, Color.FromRgb(129, 129, 165));
            Grid.SetColumn(childBubble, 1);
            row.Children.Add(childBubble);
            stack.Children.Add(row);
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(249, 250, 251)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(230, 232, 235)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = stack
        };
    }

    private static Border CreateMindMapBubble(string text, int depth, Color accent)
    {
        return new Border
        {
            Background = depth == 0
                ? new SolidColorBrush(Color.FromRgb(231, 248, 239))
                : depth == 1
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(depth == 0 ? (byte)120 : (byte)70, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(depth == 0 ? 22 : 14),
            Padding = new Thickness(depth == 0 ? 18 : 12, depth == 0 ? 14 : 9, depth == 0 ? 18 : 12, depth == 0 ? 14 : 9),
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = depth == 0 ? 16 : 13,
                FontWeight = depth == 2 ? FontWeights.Normal : FontWeights.SemiBold,
                Foreground = depth == 0
                    ? new SolidColorBrush(Color.FromRgb(7, 136, 70))
                    : new SolidColorBrush(Color.FromRgb(31, 35, 41)),
                TextAlignment = depth == 0 ? TextAlignment.Center : TextAlignment.Left
            }
        };
    }

    private static Border CreateInfoCard(string title, string body, string tag, Color accent)
    {
        var stack = new StackPanel();
        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39))
        });

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(26, accent.R, accent.G, accent.B)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            Child = new TextBlock
            {
                Text = tag,
                FontSize = 12,
                Foreground = new SolidColorBrush(accent),
                FontWeight = FontWeights.SemiBold
            }
        };
        Grid.SetColumn(badge, 1);
        header.Children.Add(badge);
        stack.Children.Add(header);
        stack.Children.Add(new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20,
            Foreground = new SolidColorBrush(Color.FromRgb(31, 35, 41))
        });

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(230, 232, 235)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 12),
            Child = stack
        };
    }

    private static Border CreateMiniCard(string title, string body, Color accent)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(accent),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        });
        stack.Children.Add(new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 19,
            Foreground = new SolidColorBrush(Color.FromRgb(31, 35, 41))
        });

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(230, 232, 235)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 10, 10),
            Child = stack
        };
    }

    private static void AddEmptyVisualState(FlowDocument document, string text)
    {
        AddCardBlock(document, new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 250)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                TextWrapping = TextWrapping.Wrap
            }
        });
    }

    private static void AddCardBlock(FlowDocument document, UIElement element)
    {
        document.Blocks.Add(new BlockUIContainer(element) { Margin = new Thickness(0) });
    }

    private static string BuildGenerateErrorText(Exception ex)
    {
        return BuildAiFailureText(ex.Message);
    }

    private static bool IsAiFailureResponse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("AI 服务调用失败", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Authentication Fails", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("insufficient", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("balance", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAiFailureText(string rawMessage)
    {
        var message = rawMessage ?? "";
        if (message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Authentication", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("API Key", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return "[[CHATBRIEF_AI_FAILURE]]\n" +
                   "title=AI 服务暂时不可用\n" +
                   "body=当前 API Key 缺失或无法通过验证，所以这次没有生成成功。\n" +
                   "hint=请到设置页填写可用的 AI 服务 API Key，再回到工作区重新生成。";
        }

        if (message.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("balance", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("insufficient", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("额度", StringComparison.OrdinalIgnoreCase))
        {
            return "[[CHATBRIEF_AI_FAILURE]]\n" +
                   "title=当前额度不足\n" +
                   "body=AI 服务返回额度不足，这次没有生成成功。\n" +
                   "hint=请在设置页检测当前可用额度，或更换可用的 API Key。";
        }

        return "[[CHATBRIEF_AI_FAILURE]]\n" +
               "title=AI 生成失败\n" +
               "body=当前请求没有完成，可能是网络、服务状态或 API 配置异常。\n" +
               "hint=请稍后重试；如果连续失败，可以先检查设置页里的 API Key 与额度。";
    }

    private static void SetMarkdownDocument(RichTextBox viewer, string markdown, IReadOnlyList<string>? highlightNames = null, Action<int>? evidenceClick = null)
    {
        highlightNames ??= [];
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("PingFang SC"),
            FontSize = 14,
            PagePadding = new Thickness(0),
            LineHeight = 22,
            Foreground = new SolidColorBrush(Color.FromRgb(31, 35, 41))
        };

        var lines = (markdown ?? "").Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                document.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 8) });
                continue;
            }

            if (IsMarkdownFence(line) || IsMarkdownDivider(line))
            {
                continue;
            }

            var heading = ParseHeading(line);
            if (heading is not null)
            {
                var (level, text) = heading.Value;
                var fontSize = level == 1 ? 24 : level == 2 ? 20 : 17;
                var margin = level == 1
                    ? new Thickness(0, 4, 0, 14)
                    : level == 2
                        ? new Thickness(0, 18, 0, 10)
                        : new Thickness(0, 16, 0, 8);
                document.Blocks.Add(CreateParagraph(text, fontSize, FontWeights.SemiBold, margin, highlightNames, evidenceClick));
            }
            else if (IsListLine(line))
            {
                document.Blocks.Add(CreateParagraph("• " + StripListMarker(line), 14, FontWeights.Normal, new Thickness(0, 3, 0, 3), highlightNames, evidenceClick));
            }
            else if (line.StartsWith(">", StringComparison.Ordinal))
            {
                document.Blocks.Add(CreateParagraph(line.TrimStart('>', ' '), 14, FontWeights.Normal, new Thickness(12, 6, 0, 6), new SolidColorBrush(Color.FromRgb(107, 114, 128)), highlightNames, evidenceClick));
            }
            else if (LooksLikeTableRow(line))
            {
                var tableLines = new List<string>();
                while (i < lines.Length && LooksLikeAnyTableLine(lines[i].Trim()))
                {
                    tableLines.Add(lines[i].Trim());
                    i++;
                }
                i--;

                var table = CreateTable(tableLines, highlightNames, evidenceClick);
                if (table is not null)
                {
                    document.Blocks.Add(table);
                }
            }
            else
            {
                document.Blocks.Add(CreateParagraph(line, 14, FontWeights.Normal, new Thickness(0, 5, 0, 5), highlightNames, evidenceClick));
            }
        }

        viewer.Document = document;
    }

    private static Paragraph CreateParagraph(string text, double fontSize, FontWeight weight, Thickness margin, IReadOnlyList<string>? highlightNames = null, Action<int>? evidenceClick = null)
    {
        var paragraph = new Paragraph
        {
            FontSize = fontSize,
            FontWeight = weight,
            Margin = margin
        };
        AddInlineRuns(paragraph, text, highlightNames, evidenceClick);
        return paragraph;
    }

    private static Paragraph CreateParagraph(string text, double fontSize, FontWeight weight, Thickness margin, Brush foreground, IReadOnlyList<string>? highlightNames = null, Action<int>? evidenceClick = null)
    {
        var paragraph = CreateParagraph(text, fontSize, weight, margin, highlightNames, evidenceClick);
        paragraph.Foreground = foreground;
        return paragraph;
    }

    private static (int Level, string Text)? ParseHeading(string line)
    {
        var match = Regex.Match(line, @"^(#{1,6})\s*(.+)$");
        if (!match.Success)
        {
            return null;
        }

        return (match.Groups[1].Value.Length, CleanMarkdownInline(match.Groups[2].Value));
    }

    private static bool IsMarkdownFence(string line)
    {
        return line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal);
    }

    private static bool IsMarkdownDivider(string line)
    {
        return Regex.IsMatch(line, @"^[-*_]{3,}$");
    }

    private static bool IsListLine(string line)
    {
        if (Regex.IsMatch(line, @"^[-*+]\s+"))
        {
            return true;
        }
        return Regex.IsMatch(line, @"^\d{1,3}[\.\)]\s+");
    }

    private static string StripListMarker(string line)
    {
        return Regex.Replace(line, @"^([-*+]|\d{1,3}[\.\)])\s+", "").Trim();
    }

    private static bool LooksLikeTableRow(string line)
    {
        return line.Contains('|', StringComparison.Ordinal)
            && !Regex.IsMatch(line, @"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?$");
    }

    private static bool LooksLikeAnyTableLine(string line)
    {
        return line.Contains('|', StringComparison.Ordinal);
    }

    private static bool IsTableSeparator(string line)
    {
        return Regex.IsMatch(line, @"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?$");
    }

    private static Table? CreateTable(IEnumerable<string> rawLines, IReadOnlyList<string>? highlightNames = null, Action<int>? evidenceClick = null)
    {
        var rows = rawLines
            .Where(line => !IsTableSeparator(line))
            .Select(ParseTableCells)
            .Where(cells => cells.Count > 0)
            .ToList();

        if (rows.Count == 0)
        {
            return null;
        }

        var maxColumns = rows.Max(row => row.Count);
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 8, 0, 14)
        };

        for (var column = 0; column < maxColumns; column++)
        {
            table.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = new TableRow();
            group.Rows.Add(row);
            for (var column = 0; column < maxColumns; column++)
            {
                var text = column < rows[rowIndex].Count ? rows[rowIndex][column] : "";
                var paragraph = CreateParagraph(text, 13, rowIndex == 0 ? FontWeights.SemiBold : FontWeights.Normal, new Thickness(0), highlightNames, evidenceClick);
                var cell = new TableCell(paragraph)
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(8, 7, 8, 7)
                };
                if (rowIndex == 0)
                {
                    cell.Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
                }
                row.Cells.Add(cell);
            }
        }

        return table;
    }

    private static List<string> ParseTableCells(string line)
    {
        return line.Trim().Trim('|')
            .Split('|')
            .Select(cell => CleanMarkdownInline(cell.Trim()))
            .Where(cell => cell.Length > 0)
            .ToList();
    }

    private static void AddInlineRuns(Paragraph paragraph, string text, IReadOnlyList<string>? highlightNames = null, Action<int>? evidenceClick = null)
    {
        var cleaned = CleanMarkdownInlineKeepBold(text);
        var matches = Regex.Matches(cleaned, @"(\*\*|__)(.+?)\1");
        var index = 0;
        foreach (Match match in matches)
        {
            if (match.Index > index)
            {
                AddHighlightedTextRuns(paragraph.Inlines, CleanMarkdownInline(cleaned[index..match.Index]), highlightNames, evidenceClick);
            }

            var bold = new Bold();
            AddHighlightedTextRuns(bold.Inlines, CleanMarkdownInline(match.Groups[2].Value), highlightNames, evidenceClick);
            paragraph.Inlines.Add(bold);
            index = match.Index + match.Length;
        }

        if (index < cleaned.Length)
        {
            AddHighlightedTextRuns(paragraph.Inlines, CleanMarkdownInline(cleaned[index..]), highlightNames, evidenceClick);
        }
    }

    private static void AddHighlightedTextRuns(InlineCollection target, string text, IReadOnlyList<string>? highlightNames, Action<int>? evidenceClick = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (evidenceClick is not null)
        {
            var citationMatches = Regex.Matches(text, @"\[#(\d{1,4})\]");
            if (citationMatches.Count > 0)
            {
                var citationIndex = 0;
                foreach (Match match in citationMatches)
                {
                    if (match.Index > citationIndex)
                    {
                        AddHighlightedTextRuns(target, text[citationIndex..match.Index], highlightNames);
                    }

                    if (int.TryParse(match.Groups[1].Value, out var number))
                    {
                        var hyperlink = new Hyperlink(new Run(match.Value))
                        {
                            Foreground = new SolidColorBrush(Color.FromRgb(6, 173, 86)),
                            FontWeight = FontWeights.SemiBold,
                            TextDecorations = null,
                            ToolTip = $"查看原始消息 #{number:000}"
                        };
                        hyperlink.Click += (_, _) => evidenceClick(number);
                        hyperlink.MouseLeftButtonUp += (_, e) =>
                        {
                            evidenceClick(number);
                            e.Handled = true;
                        };
                        target.Add(hyperlink);
                    }
                    else
                    {
                        target.Add(new Run(match.Value));
                    }

                    citationIndex = match.Index + match.Length;
                }

                if (citationIndex < text.Length)
                {
                    AddHighlightedTextRuns(target, text[citationIndex..], highlightNames);
                }

                return;
            }
        }

        var names = highlightNames?.Where(name => !string.IsNullOrWhiteSpace(name)).Take(4).ToList() ?? [];
        if (names.Count == 0)
        {
            target.Add(new Run(text));
            return;
        }

        var index = 0;
        while (index < text.Length)
        {
            var nextName = "";
            var nextIndex = -1;
            foreach (var name in names)
            {
                var found = text.IndexOf(name, index, StringComparison.OrdinalIgnoreCase);
                if (found >= 0 && (nextIndex < 0 || found < nextIndex || (found == nextIndex && name.Length > nextName.Length)))
                {
                    nextIndex = found;
                    nextName = name;
                }
            }

            if (nextIndex < 0)
            {
                target.Add(new Run(text[index..]));
                break;
            }

            if (nextIndex > index)
            {
                target.Add(new Run(text[index..nextIndex]));
            }

            var colorIndex = Math.Max(0, names.FindIndex(name => name.Equals(nextName, StringComparison.OrdinalIgnoreCase))) % PersonHighlightBrushes.Length;
            target.Add(new Run(text.Substring(nextIndex, nextName.Length))
            {
                Foreground = PersonHighlightBrushes[colorIndex],
                FontWeight = FontWeights.SemiBold
            });
            index = nextIndex + nextName.Length;
        }
    }

    private static string CleanMarkdownInline(string text)
    {
        return (text ?? "")
            .Replace("**", "")
            .Replace("__", "")
            .Replace("`", "")
            .Replace("\\-", "-")
            .Trim();
    }

    private static string CleanMarkdownInlineKeepBold(string text)
    {
        return (text ?? "")
            .Replace("`", "")
            .Replace("\\-", "-")
            .Trim();
    }

    private static (DateTime Start, DateTime End) ReadDateRange(
        System.Windows.Controls.Calendar startCalendar,
        TextBox startTimeBox,
        System.Windows.Controls.Calendar endCalendar,
        TextBox endTimeBox)
    {
        var startDate = (startCalendar.SelectedDate ?? DateTime.Today).Date;
        var endDate = (endCalendar.SelectedDate ?? DateTime.Today).Date;
        var start = startDate.Add(ParseTime(startTimeBox.Text, TimeSpan.Zero, "开始时间"));
        var end = endDate.Add(ParseTime(endTimeBox.Text, new TimeSpan(23, 59, 59), "结束时间"));
        if (end < start)
        {
            return (end, start);
        }
        return (start, end);
    }

    private static void SetDateTimeButtonContent(Button button, System.Windows.Controls.Calendar calendar, TextBox timeBox, string label, string fallbackTime)
    {
        if (button is null || calendar is null || timeBox is null)
        {
            return;
        }

        var date = (calendar.SelectedDate ?? DateTime.Today).ToString("yyyy/M/d", CultureInfo.InvariantCulture);
        var time = NormalizeTimeText(timeBox.Text, fallbackTime);
        button.Content = $"{date} {time}";
        button.ToolTip = $"{label}：{date} {time}";
    }

    private static TimeSpan ParseTime(string? text, TimeSpan fallback, string fieldName)
    {
        var value = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string[] formats = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss"];
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed.TimeOfDay;
        }

        throw new InvalidOperationException($"{fieldName}格式不正确，请使用 23:00 或 06:30 这样的格式。");
    }

    private static string NormalizeTimeText(string? text, string fallback)
    {
        try
        {
            var time = ParseTime(text, TimeSpan.Zero, "");
            return $"{(int)time.TotalHours:00}:{time.Minutes:00}";
        }
        catch
        {
            return fallback;
        }
    }

    private void ExportGroupMarkdownClicked(object sender, RoutedEventArgs e)
    {
        ExportMarkdown(_groupSummaryMarkdown, "群聊总结");
    }

    private void ExportPersonMarkdownClicked(object sender, RoutedEventArgs e)
    {
        ExportMarkdown(_personSummaryMarkdown, "好友总结");
    }

    private void ExportMarkdown(string content, string defaultName)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Contains("这里会显示", StringComparison.Ordinal))
        {
            ShowAppNotice("导出 Markdown", "当前还没有可导出的总结。");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出 Markdown",
            Filter = "Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt",
            FileName = $"{defaultName}_{DateTime.Now:yyyyMMdd_HHmm}.md"
        };
        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, CleanMarkdownForExport(content));
            ShowAppNotice("导出 Markdown", "已导出。");
        }
    }

    private static string CleanMarkdownForExport(string markdown)
    {
        var cleaned = Regex.Replace(markdown ?? string.Empty, @"\s*(?:\[#\d{1,4}\])+", "");
        cleaned = Regex.Replace(cleaned, @"[ \t]+\r?\n", Environment.NewLine);
        return cleaned.TrimEnd() + Environment.NewLine;
    }

    private void ShowGroupTemplatesClicked(object sender, RoutedEventArgs e) => SelectTemplateCategory("群聊分析");

    private void ShowPersonTemplatesClicked(object sender, RoutedEventArgs e) => SelectTemplateCategory("好友分析");

    private AppConfig CurrentConfigFromUi()
    {
        _config.ApiKey = ApiKeyBox.Password;
        _config.SelfAliases = SelfAliasesBox.Text.Trim();
        _config.WeChatDataDirectory = WeChatDataDirectoryBox.Text == "自动检测微信数据目录" ? "" : WeChatDataDirectoryBox.Text.Trim();
        _config.CurrentGroupTemplate = SelectedTemplateName(GroupTemplateCombo, _config.CurrentGroupTemplate);
        _config.CurrentPersonTemplate = SelectedTemplateName(PersonTemplateCombo, _config.CurrentPersonTemplate);
        _configService.Save(_config);
        return _config;
    }

    private void LoadTemplates()
    {
        GroupTemplateCombo.Items.Clear();
        foreach (var template in _templateService.GetByCategory("群聊分析"))
        {
            GroupTemplateCombo.Items.Add(template.Name);
        }
        GroupTemplateCombo.Items.Add(CreateTemplateAction);
        GroupTemplateCombo.SelectedItem = _config.CurrentGroupTemplate;
        if (GroupTemplateCombo.SelectedIndex < 0)
        {
            GroupTemplateCombo.SelectedIndex = 0;
        }

        PersonTemplateCombo.Items.Clear();
        foreach (var template in _templateService.GetByCategory("好友分析"))
        {
            PersonTemplateCombo.Items.Add(template.Name);
        }
        PersonTemplateCombo.Items.Add(CreateTemplateAction);
        PersonTemplateCombo.SelectedItem = _config.CurrentPersonTemplate;
        if (PersonTemplateCombo.SelectedIndex < 0)
        {
            PersonTemplateCombo.SelectedIndex = 0;
        }

        SelectTemplateCategory("群聊分析");
    }

    private static string SelectedTemplateName(ComboBox comboBox, string fallback)
    {
        var selected = comboBox.SelectedItem?.ToString();
        return string.IsNullOrWhiteSpace(selected) || selected == CreateTemplateAction ? fallback : selected;
    }

    private void TemplateComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.SelectedItem?.ToString() != CreateTemplateAction)
        {
            return;
        }

        var category = comboBox == PersonTemplateCombo ? "好友分析" : "群聊分析";
        comboBox.SelectedItem = category == "好友分析" ? _config.CurrentPersonTemplate : _config.CurrentGroupTemplate;
        OpenCreateTemplateDialog(category, selectAfterCreate: true);
    }

    private void CreateTemplateFromCenterClicked(object sender, RoutedEventArgs e)
    {
        var category = (TemplateCategoryList.SelectedItem as ListBoxItem)?.Content?.ToString() ?? "群聊分析";
        OpenCreateTemplateDialog(category, selectAfterCreate: false);
    }

    private void OpenCreateTemplateDialog(string category, bool selectAfterCreate)
    {
        var dialog = new Window
        {
            Title = "新建模板",
            Owner = this,
            Width = 760,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            Background = Brushes.Transparent
        };

        var shell = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(243, 245, 247)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 233, 238)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(15, 23, 42),
                BlurRadius = 34,
                ShadowDepth = 8,
                Opacity = 0.14
            }
        };

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(2, 0, 2, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                dialog.DragMove();
            }
        };

        var headerText = new StackPanel();
        var title = new TextBlock
        {
            Text = $"新建{category}模板",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(9, 22, 43)),
            Margin = new Thickness(0, 0, 0, 8)
        };
        headerText.Children.Add(title);

        var description = new TextBlock
        {
            Text = "用固定模板或 AI 生成规则，把选定聊天记录整理成可阅读的分析文档。",
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            TextWrapping = TextWrapping.Wrap
        };
        headerText.Children.Add(description);
        header.Children.Add(headerText);

        var closeButton = new Button
        {
            Content = "×",
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            FontSize = 18,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 233, 238)),
            BorderThickness = new Thickness(1),
            ToolTip = "关闭"
        };
        closeButton.Click += (_, _) => dialog.Close();
        Grid.SetColumn(closeButton, 1);
        header.Children.Add(closeButton);
        root.Children.Add(header);

        var card = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 233, 238)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(22)
        };
        Grid.SetRow(card, 1);

        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var form = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nameBox = CreateDialogTextBox("例如：日报整理、客户需求总结");
        var directionBox = CreateDialogTextBox("例如：按客户需求、风险和下一步行动整理");
        form.Children.Add(WrapField("模板名称", nameBox));
        var directionField = WrapField("总结方向", directionBox);
        Grid.SetColumn(directionField, 2);
        form.Children.Add(directionField);
        cardGrid.Children.Add(form);

        var tips = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 16)
        };
        var tipsStack = new StackPanel();
        tipsStack.Children.Add(new TextBlock
        {
            Text = "模板规则",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            Margin = new Thickness(0, 0, 0, 4)
        });
        tipsStack.Children.Add(new TextBlock
        {
            Text = "必须包含 {messages}。可用占位符：{count} 消息数量，{date} 生成日期，{member_notes} 成员备注。",
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            TextWrapping = TextWrapping.Wrap
        });
        tips.Child = tipsStack;
        Grid.SetRow(tips, 1);
        cardGrid.Children.Add(tips);

        var editorBlock = new Grid();
        editorBlock.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editorBlock.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        editorBlock.Children.Add(new TextBlock
        {
            Text = "提示词内容",
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            Margin = new Thickness(0, 0, 0, 8)
        });
        var contentBox = CreateDialogTextBox("这里填写模板提示词。也可以先输入总结方向，再点击“AI 生成模板”。", multiline: true);
        contentBox.Text = BuildStarterTemplate(category);
        Grid.SetRow(contentBox, 1);
        editorBlock.Children.Add(contentBox);
        Grid.SetRow(editorBlock, 2);
        cardGrid.Children.Add(editorBlock);
        card.Child = cardGrid;
        root.Children.Add(card);

        var actions = new Grid
        {
            Margin = new Thickness(0, 16, 0, 0)
        };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var footerHint = new TextBlock
        {
            Text = "保存后会出现在对应工作台和模板中心，可直接用于下一次分析。",
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 16, 0)
        };
        actions.Children.Add(footerHint);

        var actionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var generateButton = new Button { Style = (Style)FindResource("SecondaryButton"), Content = "AI 生成模板", Margin = new Thickness(0, 0, 10, 0), MinWidth = 116 };
        var cancelButton = new Button { Style = (Style)FindResource("SecondaryButton"), Content = "取消", Margin = new Thickness(0, 0, 10, 0), MinWidth = 72 };
        var saveButton = new Button { Style = (Style)FindResource("PrimaryButton"), Content = "保存模板", MinWidth = 96 };
        actionButtons.Children.Add(generateButton);
        actionButtons.Children.Add(cancelButton);
        actionButtons.Children.Add(saveButton);
        Grid.SetColumn(actionButtons, 1);
        actions.Children.Add(actionButtons);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        shell.Child = root;

        cancelButton.Click += (_, _) => dialog.Close();
        generateButton.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(directionBox.Text))
            {
                ShowAppNotice("新建模板", "请先输入想要的总结方向。", "warning", dialog);
                return;
            }

            generateButton.IsEnabled = false;
            generateButton.Content = "生成中...";
            try
            {
                var generated = await _summaryService.GenerateTemplateAsync(category, directionBox.Text, CurrentConfigFromUi());
                if (!generated.Contains("{messages}", StringComparison.Ordinal))
                {
                    generated += "\n\n共 {count} 条消息：\n{messages}";
                }
                contentBox.Text = generated;
            }
            catch (Exception ex)
            {
                ShowAppNotice("新建模板", $"AI 生成模板失败：{ex.Message}", "warning", dialog);
            }
            finally
            {
                generateButton.Content = "AI 生成模板";
                generateButton.IsEnabled = true;
            }
        };

        saveButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(contentBox.Text))
            {
                ShowAppNotice("新建模板", "模板名称和提示词内容不能为空。", "warning", dialog);
                return;
            }

            if (!contentBox.Text.Contains("{messages}", StringComparison.Ordinal))
            {
                ShowAppNotice("新建模板", "模板内容必须包含 {messages}，否则 AI 不知道要总结哪段聊天。", "warning", dialog);
                return;
            }

            var template = _templateService.Add(nameBox.Text, category, contentBox.Text);
            if (category == "群聊分析")
            {
                _config.CurrentGroupTemplate = template.Name;
            }
            else
            {
                _config.CurrentPersonTemplate = template.Name;
            }
            _configService.Save(_config);
            LoadTemplates();
            SelectTemplateCategory(category);
            if (selectAfterCreate)
            {
                if (category == "群聊分析")
                {
                    GroupTemplateCombo.SelectedItem = template.Name;
                }
                else
                {
                    PersonTemplateCombo.SelectedItem = template.Name;
                }
            }
            dialog.Close();
        };

        dialog.Content = shell;
        dialog.ShowDialog();
    }

    private void ShowAppNotice(string title, string message, string tone = "info", Window? owner = null)
    {
        owner ??= this;
        var accent = tone == "error"
            ? Color.FromRgb(239, 68, 68)
            : tone == "warning"
                ? Color.FromRgb(245, 158, 11)
                : Color.FromRgb(7, 193, 96);
        var accentBrush = new SolidColorBrush(accent);

        var dialog = new Window
        {
            Title = title,
            Owner = owner,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            Background = Brushes.Transparent
        };

        var shell = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 233, 238)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(22)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                dialog.DragMove();
            }
        };

        var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(28, accent.R, accent.G, accent.B)),
            Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = tone == "error" ? "!" : tone == "warning" ? "!" : "✓",
                Foreground = accentBrush,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        header.Children.Add(icon);

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 1);
        header.Children.Add(titleText);
        root.Children.Add(header);

        var body = new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(0, 0, 0, 22)
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var okButton = new Button
        {
            Style = (Style)FindResource("PrimaryButton"),
            Content = "知道了",
            MinWidth = 92,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        okButton.Click += (_, _) => dialog.Close();
        Grid.SetRow(okButton, 2);
        root.Children.Add(okButton);

        shell.Child = root;
        dialog.Content = shell;
        dialog.ShowDialog();
    }

    private Window CreateInputDialog(string title, UIElement content, string primaryText)
    {
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            Background = Brushes.Transparent
        };

        var shell = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 233, 238)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(22)
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39)),
            Margin = new Thickness(0, 0, 0, 16)
        });
        stack.Children.Add(content);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancelButton = new Button
        {
            Style = (Style)FindResource("SecondaryButton"),
            Content = "取消",
            MinWidth = 88,
            Margin = new Thickness(0, 0, 10, 0)
        };
        cancelButton.Click += (_, _) =>
        {
            dialog.DialogResult = false;
            dialog.Close();
        };
        var primaryButton = new Button
        {
            Style = (Style)FindResource("PrimaryButton"),
            Content = primaryText,
            MinWidth = 96
        };
        primaryButton.Click += (_, _) =>
        {
            dialog.DialogResult = true;
            dialog.Close();
        };
        actions.Children.Add(cancelButton);
        actions.Children.Add(primaryButton);
        stack.Children.Add(actions);

        shell.Child = stack;
        dialog.Content = shell;
        return dialog;
    }

    private TextBox CreateDialogTextBox(string placeholder, bool multiline = false)
    {
        return new TextBox
        {
            Style = (Style)FindResource(typeof(TextBox)),
            Tag = placeholder,
            MinHeight = multiline ? 292 : 44,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
        };
    }

    private static FrameworkElement WrapField(string label, Control control)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            Margin = new Thickness(0, 0, 0, 6)
        });
        stack.Children.Add(control);
        return stack;
    }

    private static string BuildStarterTemplate(string category)
    {
        return $$"""
        请根据下面的{{category}}聊天记录，按照指定方向生成一份结构清晰的中文总结。

        输出要求：
        # 总结标题
        ## 1. 核心结论
        ## 2. 重点内容
        ## 3. 待办事项
        用表格列出：事项、负责人、状态、时间、证据消息。
        ## 4. 待确认问题

        规则：
        - 只根据聊天记录总结，不要编造。
        - 不确定的人、时间、状态写“待确认”。
        - “我/本机”以及用户在设置中填写的称呼代表软件使用者本人。
        - 输出 Markdown，层级清晰。

        共 {count} 条消息：
        {messages}
        """;
    }

    private void FillTemplateList(string category)
    {
        if (TemplateList is null)
        {
            return;
        }
        TemplateList.Items.Clear();
        foreach (var template in _templateService.GetByCategory(category))
        {
            var title = new TextBlock
            {
                Text = template.Name,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var scene = new TextBlock
            {
                Text = template.Category,
                Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
                FontSize = 12,
                Margin = new Thickness(0, 5, 0, 0)
            };
            var stack = new StackPanel();
            stack.Children.Add(title);
            stack.Children.Add(scene);
            TemplateList.Items.Add(new ListBoxItem
            {
                Content = stack,
                Tag = template
            });
        }

        if (TemplateList.Items.Count > 0)
        {
            TemplateList.SelectedIndex = 0;
        }
    }

    private void SelectTemplateCategory(string category)
    {
        foreach (var item in TemplateCategoryList.Items.OfType<ListBoxItem>())
        {
            item.IsSelected = string.Equals(item.Content?.ToString(), category, StringComparison.Ordinal);
        }
        FillTemplateList(category);
    }

    private void TemplateCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplateList is null)
        {
            return;
        }
        if (TemplateCategoryList.SelectedItem is ListBoxItem item && item.Content is not null)
        {
            FillTemplateList(item.Content.ToString() ?? "群聊分析");
        }
    }

    private void TemplateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplateList.SelectedItem is ListBoxItem { Tag: PromptTemplate template })
        {
            LoadTemplateDetail(template);
        }
    }

    private void LoadTemplateDetail(PromptTemplate template)
    {
        _selectedTemplate = template;
        TemplateNameBox.Text = template.Name;
        TemplateContentBox.Text = template.Content;
        foreach (var item in TemplateCategoryBox.Items.OfType<ComboBoxItem>())
        {
            item.IsSelected = string.Equals(item.Content?.ToString(), template.Category, StringComparison.Ordinal);
        }
    }

    private void ResetTemplateDetailClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTemplate is not null)
        {
            LoadTemplateDetail(_selectedTemplate);
        }
    }

    private void SaveTemplateDetailClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTemplate is null)
        {
            return;
        }
        var category = (TemplateCategoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? _selectedTemplate.Category;
        if (string.IsNullOrWhiteSpace(TemplateNameBox.Text) || string.IsNullOrWhiteSpace(TemplateContentBox.Text))
        {
            ShowAppNotice("模板管理", "模板名称和提示词内容不能为空。", "warning");
            return;
        }
        if (!TemplateContentBox.Text.Contains("{messages}", StringComparison.Ordinal))
        {
            ShowAppNotice("模板管理", "模板内容需要包含 {messages}，否则 AI 不知道要总结哪段聊天。", "warning");
            return;
        }

        _templateService.Save(_selectedTemplate, TemplateNameBox.Text, category, TemplateContentBox.Text);
        LoadTemplates();
        SelectTemplateCategory(category);
        ShowAppNotice("模板管理", "模板已保存到当前运行会话。下一步会接入本地持久化。");
    }

    private void RefreshObjectSelector()
    {
        if (_currentMode == "好友分析")
        {
            ChooseObjectButton.Content = _personWorkspaceEntries.Count == 0 ? "选择好友" : "新增好友";
        }
        else
        {
            ChooseObjectButton.Content = _groupWorkspaceEntries.Count == 0 ? "选择群聊" : "新增群聊";
        }
    }

    private static string GetInitial(string text)
    {
        var first = (text ?? "").Trim().FirstOrDefault(ch => !char.IsWhiteSpace(ch));
        if (first == default)
        {
            return "#";
        }
        if (first >= 'a' && first <= 'z')
        {
            return char.ToUpperInvariant(first).ToString();
        }
        if (first >= 'A' && first <= 'Z')
        {
            return first.ToString();
        }
        if (ChineseInitials.TryGetValue(first, out var initial))
        {
            return initial;
        }
        return "#";
    }

    private ChatRoom? CurrentSelectedChatRoom()
    {
        if (WorkspaceObjectList.SelectedItem is ListBoxItem { Tag: ChatRoom room })
        {
            return room;
        }
        if (WorkspaceObjectList.SelectedItem is string label)
        {
            return _chatRooms.FirstOrDefault(room => label.StartsWith(room.DisplayName, StringComparison.Ordinal));
        }
        return null;
    }

    private FriendContact? CurrentSelectedFriend()
    {
        if (WorkspaceObjectList.SelectedItem is ListBoxItem { Tag: FriendContact selectedFriend })
        {
            return selectedFriend;
        }
        if (WorkspaceObjectList.SelectedItem is string label)
        {
            return _friends.FirstOrDefault(friend => label.StartsWith(friend.DisplayName, StringComparison.Ordinal));
        }
        return null;
    }

    private void ShowPage(UIElement page, Button selectedButton)
    {
        if (_isSummaryFullscreen)
        {
            _isSummaryFullscreen = false;
            RefreshSummaryFullscreenState();
        }

        _currentPage = page;
        GuidePage.Visibility = Visibility.Collapsed;
        AnalysisPage.Visibility = Visibility.Collapsed;
        GlobalInsightPage.Visibility = Visibility.Collapsed;
        GroupPage.Visibility = Visibility.Collapsed;
        PersonPage.Visibility = Visibility.Collapsed;
        MemoPage.Visibility = Visibility.Collapsed;
        TemplatePage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        _currentMode = page == PersonPage ? "好友分析" : "群聊分析";
        ContextTitle.Text = page == TemplatePage ? "模板中心" :
            page == SettingsPage ? "系统设置" :
            page == MemoPage ? "记录" :
            page == AnalysisPage ? "分析" :
            page == GlobalInsightPage ? "洞察" :
            page == PersonPage ? "好友分析" : "分析对象";
        ContextSubtitle.Text = page == PersonPage ? "好友工作区" :
            page == MemoPage ? "按对象查看摘录" :
            page == TemplatePage ? "按场景维护提示词" :
            page == SettingsPage ? "服务与数据" :
            page == GlobalInsightPage ? "全局扫描" :
            page == AnalysisPage ? "数据概览" : "群聊工作区";
        RefreshContextColumnForCurrentPage();
        RefreshSummaryFullscreenState();
        RefreshObjectSelector();
        RenderWorkspaceObjects();

        foreach (var button in new[] { GuideButton, GroupButton, GroupSubButton, PersonButton, InsightButton, MemoButton, TemplateButton, SettingsButton })
        {
            button.Background = _transparent;
            button.Foreground = new SolidColorBrush(Color.FromRgb(3, 2, 41));
            button.Opacity = 0.5;
            button.FontWeight = FontWeights.Medium;
        }

        selectedButton.Background = _transparent;
        selectedButton.Foreground = new SolidColorBrush(Color.FromRgb(7, 193, 96));
        selectedButton.Opacity = 1;
        selectedButton.FontWeight = FontWeights.Medium;

        if (page == SettingsPage)
        {
            _ = RefreshAiBalanceAsync();
        }
        else if (page == MemoPage)
        {
            RefreshMemoPage();
        }
        else if (page == AnalysisPage)
        {
            RefreshAnalysisOverview();
        }
        else if (page == GlobalInsightPage)
        {
            ApplyGlobalInsightRangeButtons();
            RefreshGlobalInsightPage();
        }
    }

    private void RefreshAnalysisOverview()
    {
        var messages = CurrentInsightMessages();
        InsightGroupCountText.Text = _groupSummaryStates.Count(pair => IsInsightState(pair.Value)).ToString(CultureInfo.InvariantCulture);
        InsightFriendCountText.Text = _personSummaryStates.Count(pair => IsInsightState(pair.Value)).ToString(CultureInfo.InvariantCulture);
        InsightMemoCountText.Text = _memos.Count.ToString(CultureInfo.InvariantCulture);
        InsightMessageCountText.Text = messages.Count.ToString(CultureInfo.InvariantCulture);
        InsightRangeText.Text = messages.Count == 0
            ? "暂无样本"
            : $"{messages.Min(message => message.Time):MM/dd} - {messages.Max(message => message.Time):MM/dd}";

        RenderInsightTrend(messages);
        RenderInsightSpeakerRank(messages);
        RenderInsightObjectRank();
        RenderInsightRelations(messages);
        RefreshCompareSelectors();
        RefreshAnalysisModuleView();
    }

    private void RefreshAnalysisModuleView()
    {
        var isCompare = _analysisModuleMode == "对照";
        SingleTrendSection.Visibility = isCompare ? Visibility.Collapsed : Visibility.Visible;
        SingleObjectSection.Visibility = isCompare ? Visibility.Collapsed : Visibility.Visible;
        CompareAnalysisSection.Visibility = isCompare ? Visibility.Visible : Visibility.Collapsed;
        ApplySegmentButtonState(SingleAnalysisModuleButton, !isCompare);
        ApplySegmentButtonState(CompareAnalysisModuleButton, isCompare);
        if (isCompare)
        {
            RefreshCompareSelectors();
        }
    }

    private List<ChatMessage> CurrentInsightMessages()
    {
        var messages = new List<ChatMessage>();
        messages.AddRange(_groupSummaryStates.Values.Where(IsInsightState).SelectMany(state => state.Messages));
        messages.AddRange(_personSummaryStates.Values.Where(IsInsightState).SelectMany(state => state.Messages));
        return messages
            .OrderBy(message => message.Time)
            .ToList();
    }

    private static bool IsInsightState(SummaryState state) => !state.IsEmptyState && state.Messages.Count > 0;

    private void RefreshAnalysisOverviewIfVisible()
    {
        if (_currentPage == AnalysisPage)
        {
            RefreshAnalysisOverview();
        }
    }

    private void RenderInsightTrend(IReadOnlyList<ChatMessage> messages)
    {
        InsightTrendPanel.Items.Clear();
        if (messages.Count == 0)
        {
            InsightTrendPanel.Items.Add(CreateInsightEmpty("生成一次群聊或好友总结后，这里会显示消息趋势。"));
            return;
        }

        var end = messages.Max(message => message.Time).Date;
        var days = Enumerable.Range(0, 7)
            .Select(offset => end.AddDays(offset - 6))
            .ToList();
        var counts = days
            .Select(day => new
            {
                Day = day,
                Count = messages.Count(message => message.Time.Date == day)
            })
            .ToList();
        var max = Math.Max(1, counts.Max(item => item.Count));

        foreach (var item in counts)
        {
            InsightTrendPanel.Items.Add(CreateInsightBarRow(
                item.Day.ToString("MM/dd", CultureInfo.InvariantCulture),
                item.Count,
                max,
                new SolidColorBrush(Color.FromRgb(7, 193, 96))));
        }
    }

    private void RenderInsightSpeakerRank(IReadOnlyList<ChatMessage> messages)
    {
        InsightSpeakerRankPanel.Items.Clear();
        var ranks = messages
            .Where(message => !string.IsNullOrWhiteSpace(message.SenderDisplayName))
            .GroupBy(message => message.SenderDisplayName)
            .Select(group => new { Name = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .Take(6)
            .ToList();
        if (ranks.Count == 0)
        {
            InsightSpeakerRankPanel.Items.Add(CreateInsightEmpty("暂无可统计的发言人。"));
            return;
        }

        var max = Math.Max(1, ranks.Max(item => item.Count));
        foreach (var item in ranks)
        {
            InsightSpeakerRankPanel.Items.Add(CreateInsightBarRow(item.Name, item.Count, max, new SolidColorBrush(Color.FromRgb(37, 99, 235))));
        }
    }

    private void RenderInsightObjectRank()
    {
        InsightObjectRankPanel.Items.Clear();
        var showGroups = _insightObjectMode == "群聊";
        ApplyInsightObjectToggleState(showGroups);
        var groupRanks = _chatRooms
            .Where(room => _groupSummaryStates.TryGetValue(room.Id, out var state) && IsInsightState(state))
            .Select(room => new
            {
                Name = room.DisplayName,
                Count = _groupSummaryStates[room.Id].Messages.Count
            });
        var friendRanks = _friends
            .Where(friend => _personSummaryStates.TryGetValue(friend.Id, out var state) && IsInsightState(state))
            .Select(friend => new
            {
                Name = friend.DisplayName,
                Count = _personSummaryStates[friend.Id].Messages.Count
            });
        var ranks = (showGroups ? groupRanks : friendRanks)
            .Where(item => item.Count > 0)
            .OrderByDescending(item => item.Count)
            .Take(6)
            .ToList();
        if (ranks.Count == 0)
        {
            InsightObjectRankPanel.Items.Add(CreateInsightEmpty(showGroups ? "分析过群聊后，这里会显示群聊沉淀排行。" : "分析过好友后，这里会显示好友沉淀排行。"));
            return;
        }

        var max = Math.Max(1, ranks.Max(item => item.Count));
        foreach (var item in ranks)
        {
            InsightObjectRankPanel.Items.Add(CreateInsightBarRow(item.Name, item.Count, max, new SolidColorBrush(Color.FromRgb(217, 119, 6))));
        }
    }

    private void ApplyInsightObjectToggleState(bool showGroups)
    {
        if (InsightObjectGroupButton is null || InsightObjectPersonButton is null)
        {
            return;
        }

        var activeBackground = (Brush)FindResource("WechatGreen");
        var inactiveBackground = new SolidColorBrush(Color.FromRgb(251, 252, 254));
        InsightObjectGroupButton.Background = showGroups ? activeBackground : inactiveBackground;
        InsightObjectGroupButton.Foreground = showGroups ? Brushes.White : (Brush)FindResource("TextPrimary");
        InsightObjectPersonButton.Background = showGroups ? inactiveBackground : activeBackground;
        InsightObjectPersonButton.Foreground = showGroups ? (Brush)FindResource("TextPrimary") : Brushes.White;
    }

    private void RenderInsightRelations(IReadOnlyList<ChatMessage> messages)
    {
        InsightRelationPanel.Items.Clear();
        var pairs = messages
            .Zip(messages.Skip(1), (previous, next) => new { Previous = previous, Next = next })
            .Where(pair => !string.Equals(pair.Previous.SenderDisplayName, pair.Next.SenderDisplayName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(pair =>
            {
                var names = new[] { NormalizeRelationName(pair.Previous.SenderDisplayName), NormalizeRelationName(pair.Next.SenderDisplayName) }
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return names.Length == 2 ? $"{names[0]} ↔ {names[1]}" : "";
            })
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => new { Name = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .Take(6)
            .ToList();
        if (pairs.Count == 0)
        {
            InsightRelationPanel.Items.Add(CreateInsightEmpty("多人对话生成后，这里会显示相邻互动关系。"));
            return;
        }

        var max = Math.Max(1, pairs.Max(item => item.Count));
        foreach (var item in pairs)
        {
            InsightRelationPanel.Items.Add(CreateInsightBarRow(item.Name, item.Count, max, new SolidColorBrush(Color.FromRgb(147, 51, 234))));
        }
    }

    private void RefreshCompareSelectors()
    {
        _isRefreshingCompareSelectors = true;
        try
        {
            ApplySegmentButtonState(CompareGroupButton, _compareMode == "群聊");
            ApplySegmentButtonState(ComparePersonButton, _compareMode == "好友");
            var currentFirst = (CompareFirstCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var currentSecond = (CompareSecondCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            var currentThird = (CompareThirdCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            CompareFirstCombo.Items.Clear();
            CompareSecondCombo.Items.Clear();
            CompareThirdCombo.Items.Clear();

            var entries = CurrentCompareEntries().ToList();
            CompareThirdCombo.Items.Add(new ComboBoxItem { Content = "不选择第三项", Tag = "" });
            foreach (var entry in entries)
            {
                CompareFirstCombo.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry.Id });
                CompareSecondCombo.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry.Id });
                CompareThirdCombo.Items.Add(new ComboBoxItem { Content = entry.Name, Tag = entry.Id });
            }

            SelectComboByTagOrIndex(CompareFirstCombo, currentFirst, 0);
            SelectComboByTagOrIndex(CompareSecondCombo, currentSecond, entries.Count > 1 ? 1 : 0);
            SelectComboByTagOrIndex(CompareThirdCombo, currentThird, 0);
        }
        finally
        {
            _isRefreshingCompareSelectors = false;
        }

        RefreshCompareResult();
    }

    private IEnumerable<CompareEntry> CurrentCompareEntries()
    {
        if (_compareMode == "群聊")
        {
            foreach (var room in _chatRooms.Where(room => _groupSummaryStates.TryGetValue(room.Id, out var state) && IsInsightState(state)))
            {
                yield return new CompareEntry(room.Id, room.DisplayName, _groupSummaryStates[room.Id].Messages);
            }
            yield break;
        }

        foreach (var friend in _friends.Where(friend => _personSummaryStates.TryGetValue(friend.Id, out var state) && IsInsightState(state)))
        {
            yield return new CompareEntry(friend.Id, friend.DisplayName, _personSummaryStates[friend.Id].Messages);
        }
    }

    private static void SelectComboByTagOrIndex(ComboBox combo, string? tag, int index)
    {
        var matched = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        combo.SelectedItem = matched ?? (combo.Items.Count > index ? combo.Items[index] : null);
    }

    private void RefreshCompareResult()
    {
        CompareLeftPanel.Children.Clear();
        CompareRightPanel.Children.Clear();
        CompareThirdPanel.Children.Clear();
        var firstId = (CompareFirstCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var secondId = (CompareSecondCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var thirdId = (CompareThirdCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var entries = CurrentCompareEntries().ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(firstId) || string.IsNullOrWhiteSpace(secondId) ||
            !entries.TryGetValue(firstId, out var first) ||
            !entries.TryGetValue(secondId, out var second))
        {
            CompareLeftPanel.Children.Add(CreateInsightEmpty("至少需要两个已分析对象。"));
            return;
        }

        var selected = new List<CompareEntry> { first, second };
        if (!string.IsNullOrWhiteSpace(thirdId) && entries.TryGetValue(thirdId, out var third))
        {
            selected.Add(third);
        }

        if (selected.Select(entry => entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Count)
        {
            CompareLeftPanel.Children.Add(CreateInsightEmpty("请选择不同对象进行对照。"));
            return;
        }

        RenderComparePanel(CompareLeftPanel, selected[0]);
        RenderComparePanel(CompareRightPanel, selected[1]);
        if (selected.Count > 2)
        {
            RenderComparePanel(CompareThirdPanel, selected[2]);
        }
    }

    private void RenderComparePanel(StackPanel host, CompareEntry entry)
    {
        var messages = entry.Messages;
        var speakerCount = messages.Select(message => message.SenderDisplayName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var peakHour = messages.GroupBy(message => message.Time.Hour).OrderByDescending(group => group.Count()).FirstOrDefault();
        var topType = messages.GroupBy(message => string.IsNullOrWhiteSpace(message.MessageType) ? "文本" : message.MessageType).OrderByDescending(group => group.Count()).FirstOrDefault();
        host.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 12)
        });
        host.Children.Add(CreateCompareMetric("消息量", messages.Count.ToString(CultureInfo.InvariantCulture)));
        host.Children.Add(CreateCompareMetric(_compareMode == "群聊" ? "发言人" : "互动人", speakerCount.ToString(CultureInfo.InvariantCulture)));
        host.Children.Add(CreateCompareMetric("高峰时段", peakHour is null ? "-" : $"{peakHour.Key:00}:00"));
        host.Children.Add(CreateCompareMetric("主要类型", topType?.Key ?? "-"));
    }

    private FrameworkElement CreateCompareMetric(string label, string value)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("TextMuted"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        var valueText = new TextBlock
        {
            Text = value,
            Foreground = (Brush)FindResource("TextPrimary"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        border.Child = grid;
        return border;
    }

    private FrameworkElement CreateInsightBarRow(string label, int count, int max, Brush accent)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        var labelText = new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("TextPrimary"),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(labelText, 0);
        grid.Children.Add(labelText);

        var track = new Border
        {
            Height = 10,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var fill = new Border
        {
            Width = Math.Max(8, 180.0 * count / Math.Max(1, max)),
            CornerRadius = new CornerRadius(6),
            Background = accent,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        track.Child = fill;
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);

        var countText = new TextBlock
        {
            Text = count.ToString(CultureInfo.InvariantCulture),
            Foreground = (Brush)FindResource("TextMuted"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(countText, 2);
        grid.Children.Add(countText);

        return grid;
    }

    private FrameworkElement CreateInsightStackedDistribution(IReadOnlyList<(string Name, int Count)> items, IReadOnlyList<Color> colors)
    {
        var total = Math.Max(1, items.Sum(item => item.Count));
        var host = new StackPanel();
        var outer = new Border
        {
            Height = 18,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
            Margin = new Thickness(0, 4, 0, 18)
        };
        outer.SizeChanged += (_, _) =>
        {
            outer.Clip = new RectangleGeometry(
                new Rect(0, 0, outer.ActualWidth, outer.ActualHeight),
                6,
                6);
        };
        var stack = new Grid();
        for (var i = 0; i < items.Count; i++)
        {
            stack.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(0.2, items[i].Count), GridUnitType.Star)
            });
            var segment = new Border
            {
                Background = new SolidColorBrush(colors[i % colors.Count]),
                ToolTip = $"{items[i].Name}：{items[i].Count} 条",
                Margin = new Thickness(0)
            };
            Grid.SetColumn(segment, i);
            stack.Children.Add(segment);
        }
        outer.Child = stack;
        host.Children.Add(outer);

        var chips = new WrapPanel();
        for (var i = 0; i < items.Count; i++)
        {
            var percent = Math.Round(items[i].Count * 100.0 / total);
            var chip = new Border
            {
                MinHeight = 32,
                Background = new SolidColorBrush(Color.FromRgb(248, 251, 252)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 10, 10),
                BorderBrush = new SolidColorBrush(Color.FromRgb(229, 236, 242)),
                BorderThickness = new Thickness(1)
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(colors[i % colors.Count]),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{items[i].Name} {percent}%",
                Foreground = (Brush)FindResource("TextPrimary"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
            chip.Child = row;
            chips.Children.Add(chip);
        }
        host.Children.Add(chips);
        return host;
    }

    private FrameworkElement CreateInsightRelationCard(string label, int count, int index)
    {
        var accent = new SolidColorBrush(InsightChartColors[(index + 1) % InsightChartColors.Length]);
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(249, 250, 251)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            BorderBrush = new SolidColorBrush(Color.FromRgb(237, 242, 247)),
            BorderThickness = new Thickness(1)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        var badge = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(10),
            Background = accent,
            Child = new TextBlock
            {
                Text = (index + 1).ToString(CultureInfo.InvariantCulture),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        grid.Children.Add(badge);

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("TextPrimary"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = "关系热度",
            Foreground = (Brush)FindResource("TextMuted"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0)
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var countText = new TextBlock
        {
            Text = count.ToString(CultureInfo.InvariantCulture),
            Foreground = accent,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(countText, 2);
        grid.Children.Add(countText);
        card.Child = grid;
        return card;
    }

    private FrameworkElement CreateInsightColumnChart(IReadOnlyList<(string Name, int Count)> items, Brush accent)
    {
        var max = Math.Max(1, items.Max(item => item.Count));
        var grid = new Grid { Height = 190, Margin = new Thickness(0, 4, 0, 0) };
        for (var i = 0; i < items.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var column = new Grid { Margin = new Thickness(4, 0, 4, 0), ToolTip = $"{item.Name}：{item.Count} 条" };
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var barWrap = new Grid { Height = 126, VerticalAlignment = VerticalAlignment.Bottom };
            var bar = new Border
            {
                Height = Math.Max(12, 120.0 * item.Count / max),
                CornerRadius = new CornerRadius(10, 10, 4, 4),
                Background = new SolidColorBrush(InsightChartColors[(i + 2) % InsightChartColors.Length]),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            barWrap.Children.Add(bar);
            column.Children.Add(barWrap);

            var label = new TextBlock
            {
                Text = ShortInsightLabel(item.Name),
                Foreground = (Brush)FindResource("TextMuted"),
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 42,
                Margin = new Thickness(0, 8, 0, 0)
            };
            Grid.SetRow(label, 1);
            column.Children.Add(label);
            Grid.SetColumn(column, i);
            grid.Children.Add(column);
        }

        return grid;
    }

    private static string ShortInsightLabel(string value)
    {
        var text = value.Replace("群聊 · ", "").Replace("好友 · ", "").Trim();
        return text.Length <= 6 ? text : text[..6] + "...";
    }

    private FrameworkElement CreateInsightEmpty(string text)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Child = new TextBlock
            {
                Text = text,
                Foreground = (Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            }
        };
    }

    private void RefreshGlobalInsightPage()
    {
        RefreshGlobalInsightScopeOptions();
        var messages = TryGetDatabaseMessages(out var dbMessages)
            ? dbMessages
            : CurrentGlobalInsightMessages().OrderBy(message => message.Time).ToList();
        var (start, end) = CurrentGlobalInsightRange();
        InsightOverviewStats? dbStats = null;
        try
        {
            dbStats = _chatBriefDb.GetOverviewStats(start, end, _globalInsightScopeKey, SelfIdentityNames());
        }
        catch
        {
        }
        var topSpeaker = messages
            .Where(message => !IsSelfSpeaker(message.SenderDisplayName))
            .GroupBy(message => message.SenderDisplayName)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();
        var peakHour = messages
            .GroupBy(message => message.Time.Hour)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();

        GlobalInsightMessageText.Text = (dbStats?.MessageCount ?? messages.Count).ToString(CultureInfo.InvariantCulture);
        GlobalInsightObjectText.Text = (dbStats?.ObjectCount ?? CurrentGlobalInsightObjectCount()).ToString(CultureInfo.InvariantCulture);
        GlobalInsightTopSpeakerText.Text = dbStats is { TopSpeaker: not "-" } ? dbStats.TopSpeaker : topSpeaker is null ? "-" : topSpeaker.Key;
        GlobalInsightPeakHourText.Text = dbStats is { PeakHour: >= 0 } ? $"{dbStats.PeakHour:00}" : peakHour is null ? "-" : $"{peakHour.Key:00}";

        ApplyGlobalInsightViewState();
        RenderGlobalInsightNarrative(messages);
        RenderGlobalInsightHeatmap(messages);
        RenderGlobalInsightTypes(messages);
        RenderGlobalInsightRelations(messages);
        RenderGlobalInsightObjectRanking();
        RenderGlobalInsightHours(messages);
    }

    private void RefreshGlobalInsightScopeOptions()
    {
        _isRefreshingGlobalInsightScope = true;
        try
        {
            var current = _globalInsightScopeKey;
            GlobalInsightScopeCombo.Items.Clear();
            AddGlobalInsightScopeOption("全部对象", "all");
            AddGlobalInsightScopeOption("全部群聊", "groups");
            AddGlobalInsightScopeOption("全部好友", "friends");
            foreach (var item in _globalInsightGroupMessages.OrderBy(item => item.Value.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                AddGlobalInsightScopeOption($"群聊 · {item.Value.Name}", $"group:{item.Key}");
            }
            foreach (var item in _globalInsightFriendMessages.OrderBy(item => item.Value.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                AddGlobalInsightScopeOption($"好友 · {item.Value.Name}", $"friend:{item.Key}");
            }

            var selected = GlobalInsightScopeCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), current, StringComparison.OrdinalIgnoreCase))
                ?? GlobalInsightScopeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
            GlobalInsightScopeCombo.SelectedItem = selected;
            _globalInsightScopeKey = selected?.Tag?.ToString() ?? "all";
        }
        finally
        {
            _isRefreshingGlobalInsightScope = false;
        }
    }

    private void AddGlobalInsightScopeOption(string label, string key)
    {
        GlobalInsightScopeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = key });
    }

    private List<ChatMessage> CurrentGlobalInsightMessages()
    {
        return _globalInsightScopeKey switch
        {
            "groups" => CurrentGlobalInsightGroupMessages().SelectMany(group => group.Messages).OrderBy(message => message.Time).ToList(),
            "friends" => CurrentGlobalInsightFriendMessages().SelectMany(friend => friend.Messages).OrderBy(message => message.Time).ToList(),
            var key when key.StartsWith("group:", StringComparison.OrdinalIgnoreCase) && _globalInsightGroupMessages.TryGetValue(key[6..], out var group) => group.Messages.OrderBy(message => message.Time).ToList(),
            var key when key.StartsWith("friend:", StringComparison.OrdinalIgnoreCase) && _globalInsightFriendMessages.TryGetValue(key[7..], out var friend) => friend.Messages.OrderBy(message => message.Time).ToList(),
            _ => _globalInsightMessages.OrderBy(message => message.Time).ToList()
        };
    }

    private (DateTime Start, DateTime End) CurrentGlobalInsightRange()
    {
        var end = _globalInsightMessages.Count > 0
            ? _globalInsightMessages.Max(message => message.Time)
            : DateTime.Now;
        return (end.AddDays(-_globalInsightDays), end);
    }

    private bool TryGetDatabaseMessages(out List<ChatMessage> messages)
    {
        try
        {
            var (start, end) = CurrentGlobalInsightRange();
            messages = _chatBriefDb.QueryScopedMessages(start, end, _globalInsightScopeKey).ToList();
            return messages.Count > 0;
        }
        catch
        {
            messages = [];
            return false;
        }
    }

    private IEnumerable<(string Name, List<ChatMessage> Messages)> CurrentGlobalInsightGroupMessages()
    {
        if (_globalInsightScopeKey.StartsWith("group:", StringComparison.OrdinalIgnoreCase) &&
            _globalInsightGroupMessages.TryGetValue(_globalInsightScopeKey[6..], out var selectedGroup))
        {
            yield return selectedGroup;
            yield break;
        }

        if (_globalInsightScopeKey.StartsWith("friend:", StringComparison.OrdinalIgnoreCase) || _globalInsightScopeKey == "friends")
        {
            yield break;
        }

        foreach (var groupItem in _globalInsightGroupMessages.Values)
        {
            yield return groupItem;
        }
    }

    private IEnumerable<(string Name, List<ChatMessage> Messages)> CurrentGlobalInsightFriendMessages()
    {
        if (_globalInsightScopeKey.StartsWith("friend:", StringComparison.OrdinalIgnoreCase) &&
            _globalInsightFriendMessages.TryGetValue(_globalInsightScopeKey[7..], out var selectedFriend))
        {
            yield return selectedFriend;
            yield break;
        }

        if (_globalInsightScopeKey.StartsWith("group:", StringComparison.OrdinalIgnoreCase) || _globalInsightScopeKey == "groups")
        {
            yield break;
        }

        foreach (var friendItem in _globalInsightFriendMessages.Values)
        {
            yield return friendItem;
        }
    }

    private int CurrentGlobalInsightObjectCount()
    {
        return CurrentGlobalInsightGroupMessages().Count() + CurrentGlobalInsightFriendMessages().Count();
    }

    private void ApplyGlobalInsightViewState()
    {
        var isOverview = _globalInsightView == "总览";
        var isAnnual = _globalInsightView == "年度总结";
        var isTime = _globalInsightView == "时间投入";
        var isRelationship = _globalInsightView == "关系变化";

        ApplySegmentButtonState(GlobalOverviewInsightButton, isOverview);
        ApplySegmentButtonState(GlobalAnnualInsightButton, _globalInsightView == "年度总结");
        ApplySegmentButtonState(GlobalTimeInvestmentButton, _globalInsightView == "时间投入");
        ApplySegmentButtonState(GlobalRelationshipInsightButton, _globalInsightView == "关系变化");

        GlobalInsightKpiGrid.Visibility = isOverview ? Visibility.Visible : Visibility.Collapsed;
        GlobalInsightNarrativeCard.Visibility = isOverview ? Visibility.Collapsed : Visibility.Visible;
        GlobalInsightHeatCard.Visibility = (isOverview || isAnnual || isTime) ? Visibility.Visible : Visibility.Collapsed;
        GlobalInsightTypeCard.Visibility = (isOverview || isAnnual) ? Visibility.Visible : Visibility.Collapsed;
        GlobalInsightRelationCard.Visibility = (isOverview || isRelationship) ? Visibility.Visible : Visibility.Collapsed;
        GlobalInsightDetailCard.Visibility = Visibility.Visible;

        GlobalInsightHeatTitle.Text = isTime ? "投入日历" : "活跃日历";
        GlobalInsightTypeTitle.Text = isAnnual ? "内容结构" : "消息类型";
        GlobalInsightRelationTitle.Text = isRelationship ? "关系变化" : "关系排行";
        GlobalInsightDetailTitle.Text = isTime ? "投入对象" : isRelationship ? "关系对象" : "对象排行";
        GlobalInsightHourTitle.Text = isRelationship ? "联系时段" : "时间节奏";

        Grid.SetRow(GlobalInsightNarrativeCard, 0);
        Grid.SetColumn(GlobalInsightNarrativeCard, 0);
        Grid.SetColumnSpan(GlobalInsightNarrativeCard, 2);

        if (isAnnual)
        {
            Grid.SetRow(GlobalInsightHeatCard, 1);
            Grid.SetColumn(GlobalInsightHeatCard, 0);
            Grid.SetColumnSpan(GlobalInsightHeatCard, 1);
            GlobalInsightHeatCard.Margin = new Thickness(0, 0, 8, 16);
            Grid.SetRow(GlobalInsightTypeCard, 1);
            Grid.SetColumn(GlobalInsightTypeCard, 1);
            GlobalInsightTypeCard.Margin = new Thickness(8, 0, 0, 16);
            Grid.SetRow(GlobalInsightDetailCard, 2);
            Grid.SetColumn(GlobalInsightDetailCard, 0);
            Grid.SetColumnSpan(GlobalInsightDetailCard, 2);
            GlobalInsightDetailCard.Margin = new Thickness(0);
        }
        else if (isTime)
        {
            Grid.SetRow(GlobalInsightHeatCard, 1);
            Grid.SetColumn(GlobalInsightHeatCard, 0);
            Grid.SetColumnSpan(GlobalInsightHeatCard, 2);
            GlobalInsightHeatCard.Margin = new Thickness(0, 0, 0, 16);
            Grid.SetRow(GlobalInsightDetailCard, 2);
            Grid.SetColumn(GlobalInsightDetailCard, 0);
            Grid.SetColumnSpan(GlobalInsightDetailCard, 2);
            GlobalInsightDetailCard.Margin = new Thickness(0);
        }
        else if (isRelationship)
        {
            Grid.SetColumn(GlobalInsightRelationCard, 0);
            Grid.SetColumnSpan(GlobalInsightRelationCard, 1);
            Grid.SetRow(GlobalInsightRelationCard, 1);
            GlobalInsightRelationCard.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(GlobalInsightDetailCard, 1);
            Grid.SetColumnSpan(GlobalInsightDetailCard, 1);
            Grid.SetRow(GlobalInsightDetailCard, 1);
            GlobalInsightDetailCard.Margin = new Thickness(8, 0, 0, 0);
        }
        else
        {
            Grid.SetRow(GlobalInsightHeatCard, 0);
            Grid.SetColumn(GlobalInsightHeatCard, 0);
            Grid.SetColumnSpan(GlobalInsightHeatCard, 1);
            GlobalInsightHeatCard.Margin = new Thickness(0, 0, 8, 16);
            Grid.SetRow(GlobalInsightTypeCard, 0);
            Grid.SetColumn(GlobalInsightTypeCard, 1);
            GlobalInsightTypeCard.Margin = new Thickness(8, 0, 0, 16);
            Grid.SetColumn(GlobalInsightRelationCard, 0);
            Grid.SetColumnSpan(GlobalInsightRelationCard, 1);
            Grid.SetRow(GlobalInsightRelationCard, 1);
            GlobalInsightRelationCard.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(GlobalInsightDetailCard, 1);
            Grid.SetColumnSpan(GlobalInsightDetailCard, 1);
            Grid.SetRow(GlobalInsightDetailCard, 1);
            GlobalInsightDetailCard.Margin = new Thickness(8, 0, 0, 0);
        }
    }

    private void RenderGlobalInsightNarrative(IReadOnlyList<ChatMessage> messages)
    {
        if (_globalInsightView == "总览")
        {
            return;
        }

        if (messages.Count == 0)
        {
            GlobalInsightViewTitle.Text = _globalInsightView;
            GlobalInsightViewSummary.Text = "扫描后，这里会显示当前范围的关键结论。";
            return;
        }

        var topObject = CurrentGlobalInsightGroupMessages()
            .Select(item => new { Name = $"群聊「{item.Name}」", Count = item.Messages.Count })
            .Concat(CurrentGlobalInsightFriendMessages().Select(item => new { Name = $"好友「{item.Name}」", Count = item.Messages.Count }))
            .OrderByDescending(item => item.Count)
            .FirstOrDefault();
        var topDay = messages
            .GroupBy(message => message.Time.Date)
            .Select(group => new { Day = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .FirstOrDefault();
        var topHour = messages
            .GroupBy(message => message.Time.Hour)
            .Select(group => new { Hour = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .FirstOrDefault();
        var topType = messages
            .GroupBy(message => string.IsNullOrWhiteSpace(message.MessageType) ? "文本" : message.MessageType)
            .Select(group => new { Name = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .FirstOrDefault();
        var topGroup = BuildGroupRelationRanks().FirstOrDefault();
        var topFriend = BuildFriendRelationRanks().FirstOrDefault();

        GlobalInsightViewTitle.Text = _globalInsightView;
        GlobalInsightViewSummary.Text = _globalInsightView switch
        {
            "年度总结" => $"当前范围共 {messages.Count} 条消息，最活跃对象是 {topObject?.Name ?? "暂无"}；内容以 {topType?.Name ?? "暂无"} 为主，高峰集中在 {topDay?.Day.ToString("MM/dd", CultureInfo.InvariantCulture) ?? "暂无"}。",
            "时间投入" => $"当前范围日均约 {Math.Round(messages.Count / (double)Math.Max(1, _globalInsightDays), 1)} 条消息；最集中的日期是 {topDay?.Day.ToString("MM/dd", CultureInfo.InvariantCulture) ?? "暂无"}，高峰时段为 {topHour?.Hour.ToString("00", CultureInfo.InvariantCulture) ?? "--"} 点。",
            "关系变化" => $"当前范围内，群聊热度最高的是 {topGroup.Name ?? "暂无"}，好友互动最高的是 {topFriend.Name ?? "暂无"}。可以切换范围查看单个对象的关系变化。",
            _ => ""
        };
    }

    private void ApplySegmentButtonState(Button button, bool active)
    {
        button.Background = active ? (Brush)FindResource("WechatGreen") : new SolidColorBrush(Color.FromRgb(251, 252, 254));
        button.Foreground = active ? Brushes.White : (Brush)FindResource("TextPrimary");
    }

    private async Task ScanGlobalInsightAsync()
    {
        if (_chatRooms.Count == 0 && _friends.Count == 0)
        {
            GlobalInsightStatusText.Text = "还没有可扫描的群聊或好友，请先初始化数据。";
            return;
        }

        GlobalInsightScanButton.IsEnabled = false;
        GlobalInsightStatusText.Text = $"正在扫描近 {_globalInsightDays} 天的群聊和好友...";
        _globalInsightMessages.Clear();
        _globalInsightGroupMessages.Clear();
        _globalInsightFriendMessages.Clear();
        _globalInsightObjectCount = 0;
        RefreshGlobalInsightPage();

        var end = DateTime.Now;
        var start = end.AddDays(-_globalInsightDays);
        var scannedObjects = 0;
        var messages = new List<ChatMessage>();

        try
        {
            foreach (var room in _chatRooms)
            {
                var roomMessages = await GetGroupMessagesAsync(room, start, end, null);
                if (roomMessages.Count > 0)
                {
                    scannedObjects++;
                    var normalized = roomMessages.Select(message => message with
                    {
                        SenderDisplayName = string.IsNullOrWhiteSpace(message.SenderDisplayName) ? room.DisplayName : message.SenderDisplayName
                    }).ToList();
                    _globalInsightGroupMessages[room.Id] = (room.DisplayName, normalized);
                    messages.AddRange(normalized);
                    TrySyncMessagesToLocalDb(room.Id, "group", room.DisplayName, normalized);
                }
            }

            foreach (var friend in _friends)
            {
                var friendMessages = await _dataService.GetMessagesAsync(friend.Id, start, end, null);
                if (friendMessages.Count > 0)
                {
                    scannedObjects++;
                    var normalized = friendMessages.ToList();
                    _globalInsightFriendMessages[friend.Id] = (friend.DisplayName, normalized);
                    messages.AddRange(normalized);
                    TrySyncMessagesToLocalDb(friend.Id, "private", friend.DisplayName, normalized);
                }
            }

            _globalInsightMessages.Clear();
            _globalInsightMessages.AddRange(messages.OrderBy(message => message.Time));
            _globalInsightObjectCount = scannedObjects;
            GlobalInsightStatusText.Text = $"已扫描近 {_globalInsightDays} 天，覆盖 {scannedObjects} 个对象、{messages.Count} 条消息。";
            RefreshGlobalInsightPage();
        }
        catch (Exception ex)
        {
            GlobalInsightStatusText.Text = $"扫描失败：{ex.Message}";
        }
        finally
        {
            GlobalInsightScanButton.IsEnabled = true;
        }
    }

    private void ApplyGlobalInsightRangeButtons()
    {
        var sevenActive = _globalInsightDays == 7;
        ApplySegmentButtonState(GlobalInsightSevenDayButton, sevenActive);
        ApplySegmentButtonState(GlobalInsightThirtyDayButton, !sevenActive);
    }

    private void RenderGlobalInsightHeatmap(IReadOnlyList<ChatMessage> messages)
    {
        GlobalInsightHeatmapPanel.Children.Clear();
        if (messages.Count == 0)
        {
            GlobalInsightHeatmapPanel.Children.Add(CreateInsightEmpty("扫描后显示每日活跃分布。"));
            return;
        }

        var end = messages.Max(message => message.Time).Date;
        var dayCount = _globalInsightDays == 7 ? 7 : 30;
        GlobalInsightHeatmapPanel.Columns = _globalInsightDays == 7 ? 7 : 10;
        var days = Enumerable.Range(0, dayCount).Select(offset => end.AddDays(offset - dayCount + 1)).ToList();
        Dictionary<DateTime, int> counts;
        try
        {
            var (start, rangeEnd) = CurrentGlobalInsightRange();
            counts = _chatBriefDb.CountByDay(start, rangeEnd, _globalInsightScopeKey)
                .ToDictionary(item => item.Day.Date, item => item.Count);
            counts = days.ToDictionary(day => day, day => counts.GetValueOrDefault(day));
        }
        catch
        {
            counts = days.ToDictionary(day => day, day => messages.Count(message => message.Time.Date == day));
        }
        var max = Math.Max(1, counts.Values.Max());
        foreach (var day in days)
        {
            var intensity = counts[day] / (double)max;
            var cell = new Border
            {
                Height = 34,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(
                    255,
                    (byte)(232 - 205 * intensity),
                    (byte)(248 - 55 * intensity),
                    (byte)(239 - 143 * intensity))),
                ToolTip = $"{day:MM/dd}：{counts[day]} 条"
            };
            GlobalInsightHeatmapPanel.Children.Add(cell);
        }
    }

    private void RenderGlobalInsightTypes(IReadOnlyList<ChatMessage> messages)
    {
        GlobalInsightTypePanel.Items.Clear();
        List<InsightRankItem> ranks;
        try
        {
            var (start, end) = CurrentGlobalInsightRange();
            ranks = _chatBriefDb.RankMessageTypes(start, end, _globalInsightScopeKey, 5).ToList();
        }
        catch
        {
            ranks = messages
                .GroupBy(message => string.IsNullOrWhiteSpace(message.MessageType) ? "文本" : message.MessageType)
                .Select(group => new InsightRankItem(group.Key, group.Count()))
                .OrderByDescending(item => item.Count)
                .Take(5)
                .ToList();
        }
        if (ranks.Count == 0)
        {
            GlobalInsightTypePanel.Items.Add(CreateInsightEmpty("暂无内容结构。"));
            return;
        }

        GlobalInsightTypePanel.Items.Add(CreateInsightStackedDistribution(
            ranks.Select(item => (item.Name, item.Count)).ToList(),
            InsightChartColors));
    }

    private void RenderGlobalInsightRelations(IReadOnlyList<ChatMessage> messages)
    {
        GlobalInsightRelationHeatPanel.Items.Clear();
        ApplyGlobalInsightRelationToggleState();
        List<InsightRankItem> pairs;
        try
        {
            var (start, end) = CurrentGlobalInsightRange();
            pairs = _chatBriefDb.RankRelations(start, end, _globalInsightScopeKey, _globalInsightRelationMode, 7).ToList();
        }
        catch
        {
            pairs = (_globalInsightRelationMode == "群聊"
                ? BuildGroupRelationRanks().Take(7).Select(item => new InsightRankItem(item.Name, item.Count))
                : BuildFriendRelationRanks().Take(7).Select(item => new InsightRankItem(item.Name, item.Count))).ToList();
        }
        if (pairs.Count == 0)
        {
            GlobalInsightRelationHeatPanel.Items.Add(CreateInsightEmpty(_globalInsightRelationMode == "群聊" ? "暂无可比较的群聊关系。" : "暂无可比较的好友关系。"));
            return;
        }

        for (var i = 0; i < pairs.Count; i++)
        {
            GlobalInsightRelationHeatPanel.Items.Add(CreateInsightRelationCard(pairs[i].Name, pairs[i].Count, i));
        }
    }

    private void ApplyGlobalInsightRelationToggleState()
    {
        var activeBackground = (Brush)FindResource("WechatGreen");
        var inactiveBackground = new SolidColorBrush(Color.FromRgb(251, 252, 254));
        var groupActive = _globalInsightRelationMode == "群聊";
        GlobalInsightGroupRelationButton.Background = groupActive ? activeBackground : inactiveBackground;
        GlobalInsightGroupRelationButton.Foreground = groupActive ? Brushes.White : (Brush)FindResource("TextPrimary");
        GlobalInsightFriendRelationButton.Background = groupActive ? inactiveBackground : activeBackground;
        GlobalInsightFriendRelationButton.Foreground = groupActive ? (Brush)FindResource("TextPrimary") : Brushes.White;
    }

    private void RenderGlobalInsightObjectRanking()
    {
        GlobalInsightWordCloudPanel.Children.Clear();
        List<InsightRankItem> ranks;
        try
        {
            var (start, end) = CurrentGlobalInsightRange();
            ranks = _chatBriefDb.RankObjects(start, end, _globalInsightScopeKey, 8).ToList();
        }
        catch
        {
            var groupItems = CurrentGlobalInsightGroupMessages()
                .Select(item => new InsightRankItem($"群聊 · {item.Name}", item.Messages.Count));
            var friendItems = CurrentGlobalInsightFriendMessages()
                .Select(item => new InsightRankItem($"好友 · {item.Name}", item.Messages.Count));
            ranks = groupItems.Concat(friendItems)
                .Where(item => item.Count > 0)
                .OrderByDescending(item => item.Count)
                .Take(8)
                .ToList();
        }
        if (ranks.Count == 0)
        {
            GlobalInsightWordCloudPanel.Children.Add(CreateInsightEmpty("暂无对象排行。"));
            return;
        }

        GlobalInsightWordCloudPanel.Children.Add(CreateInsightColumnChart(
            ranks.Select(item => (item.Name, item.Count)).ToList(),
            new SolidColorBrush(InsightChartColors[3])));
    }

    private void RenderGlobalInsightHours(IReadOnlyList<ChatMessage> messages)
    {
        GlobalInsightHourPanel.Children.Clear();
        List<InsightHourCount> counts;
        try
        {
            var (start, end) = CurrentGlobalInsightRange();
            var raw = _chatBriefDb.CountByHour(start, end, _globalInsightScopeKey).ToDictionary(item => item.Hour, item => item.Count);
            counts = Enumerable.Range(0, 24).Select(hour => new InsightHourCount(hour, raw.GetValueOrDefault(hour))).ToList();
        }
        catch
        {
            counts = Enumerable.Range(0, 24)
                .Select(hour => new InsightHourCount(hour, messages.Count(message => message.Time.Hour == hour)))
                .ToList();
        }
        var max = Math.Max(1, counts.Max(item => item.Count));
        foreach (var item in counts)
        {
            var intensity = item.Count / (double)max;
            GlobalInsightHourPanel.Children.Add(new Border
            {
                Height = 28,
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(255, (byte)(241 - 24 * intensity), (byte)(245 - 146 * intensity), (byte)(249 - 20 * intensity))),
                ToolTip = $"{item.Hour:00}:00：{item.Count} 条",
                Child = new TextBlock
                {
                    Text = item.Hour.ToString("00", CultureInfo.InvariantCulture),
                    FontSize = 10,
                    Foreground = (Brush)FindResource("TextMuted"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }
    }

    private IEnumerable<(string Name, int Count)> BuildGroupRelationRanks()
    {
        return CurrentGlobalInsightGroupMessages()
            .Select(group => (Name: group.Name, Count: group.Messages.Count))
            .Where(item => item.Count > 0)
            .OrderByDescending(item => item.Count);
    }

    private IEnumerable<(string Name, int Count)> BuildFriendRelationRanks()
    {
        return CurrentGlobalInsightFriendMessages()
            .Select(friend => ($"我/本机 ↔ {friend.Name}", friend.Messages.Count))
            .Where(item => item.Count > 0)
            .OrderByDescending(item => item.Count);
    }

    private bool IsSelfSpeaker(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        if (string.Equals(trimmed, "我/本机", StringComparison.Ordinal))
        {
            return true;
        }

        return SelfIdentityNames().Any(alias => string.Equals(alias, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<string> SelfIdentityNames()
    {
        var names = new List<string>(ParseAliases(_config.SelfAliases));
        if (!string.IsNullOrWhiteSpace(_dataService.CurrentWechatUserDir))
        {
            names.Add(new DirectoryInfo(_dataService.CurrentWechatUserDir).Name);
        }
        names.AddRange(_dataService.CurrentSelfDisplayNames);

        return names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ParseAliases(string? aliases)
    {
        return (aliases ?? "")
            .Split(['，', ',', '、', ';', '；', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsTextMessageForWordCloud(ChatMessage message)
    {
        var content = message.Content.Trim();
        var explicitlyNonText = !string.Equals(message.MessageType, "text", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(message.MessageType, "文本", StringComparison.OrdinalIgnoreCase) &&
                                content.StartsWith("[");
        if (explicitlyNonText)
        {
            return false;
        }

        return !content.StartsWith("[") &&
               !content.Contains("本地图片未定位", StringComparison.Ordinal) &&
               !content.Contains("OCR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemLikeWord(string word)
    {
        return word.Contains("消息", StringComparison.Ordinal) ||
               word.Contains("未定位", StringComparison.Ordinal) ||
               word.Contains("识别", StringComparison.Ordinal) ||
               word.Contains("系统", StringComparison.Ordinal) ||
               word.Contains("图片", StringComparison.Ordinal) ||
               word.Contains("语音", StringComparison.Ordinal);
    }

    private IEnumerable<(string Name, int Count)> BuildRelationPairs(IReadOnlyList<ChatMessage> messages)
    {
        return messages
            .Zip(messages.Skip(1), (previous, next) => new { Previous = previous, Next = next })
            .Where(pair => !string.Equals(pair.Previous.SenderDisplayName, pair.Next.SenderDisplayName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(pair =>
            {
                var names = new[] { NormalizeRelationName(pair.Previous.SenderDisplayName), NormalizeRelationName(pair.Next.SenderDisplayName) }
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return names.Length == 2 ? $"{names[0]} ↔ {names[1]}" : "";
            })
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => (group.Key, group.Count()))
            .OrderByDescending(item => item.Item2);
    }

    private string NormalizeRelationName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        return IsSelfSpeaker(name) ? "我/本机" : name.Trim();
    }

    private void SummarySelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBox viewer)
        {
            return;
        }

        var selectedText = viewer.Selection.Text.Trim();
        if (string.IsNullOrWhiteSpace(selectedText) || selectedText.Length < 2)
        {
            SelectionMemoPopup.IsOpen = false;
            _pendingMemoText = "";
        }
    }

    private void SummarySelectionMouseFinished(object sender, MouseButtonEventArgs e)
    {
        if (sender is not RichTextBox viewer)
        {
            return;
        }

        var selectedText = NormalizeMemoContent(viewer.Selection.Text);
        if (string.IsNullOrWhiteSpace(selectedText) || selectedText.Length < 2)
        {
            SelectionMemoPopup.IsOpen = false;
            _pendingMemoText = "";
            return;
        }

        _pendingMemoText = selectedText;
        SelectionMemoPopup.PlacementTarget = viewer;
        SelectionMemoPopup.IsOpen = false;
        SelectionMemoPopup.IsOpen = true;
    }

    private void SaveSelectedTextToMemoClicked(object sender, RoutedEventArgs e)
    {
        SelectionMemoPopup.IsOpen = false;
        var content = NormalizeMemoContent(_pendingMemoText);
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var source = CurrentMemoSource();
        if (source is null)
        {
            ShowAppNotice("保存记录", "请先在当前工作区选择一个群聊或好友。", "warning");
            return;
        }

        var (sourceType, sourceId, sourceName) = source.Value;
        var exists = _memos.Any(memo =>
            memo.SourceType == sourceType &&
            memo.SourceId == sourceId &&
            string.Equals(memo.Content, content, StringComparison.Ordinal));

        if (!exists)
        {
            var evidenceNumbers = ExtractEvidenceNumbers(content).OrderBy(number => number).ToList();
            var evidencePreview = BuildMemoEvidencePreview(evidenceNumbers, CurrentMemoMessages());
            var category = ClassifyMemoCategory(content);
            _memos.Insert(0, new MemoEntry(Guid.NewGuid().ToString("N"), sourceType, sourceId, sourceName, content, DateTime.Now, evidenceNumbers, evidencePreview, category, DefaultMemoStatus(category)));
            _memoService.Save(_memos);
        }

        _memoMode = sourceType;
        _pendingMemoText = "";
        RefreshMemoPage();
        FlashSavedMemoFeedback(sourceType, sourceName);
    }

    private void RenderMemoSuggestions(Border panel, StackPanel list, string sourceType, string sourceId, string sourceName, string markdown, IReadOnlyList<ChatMessage> messages)
    {
        list.Children.Clear();
        var suggestions = BuildMemoSuggestions(sourceType, sourceId, sourceName, markdown, messages);
        panel.Visibility = suggestions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var suggestion in suggestions)
        {
            list.Children.Add(CreateMemoSuggestionItem(suggestion));
        }
    }

    private void GenerateMemoSuggestionsClicked(object sender, RoutedEventArgs e)
    {
        if (_currentPage == GroupPage && CurrentSelectedChatRoom() is { } room)
        {
            var suggestions = BuildMemoSuggestions("群聊", room.Id, room.DisplayName, _groupSummaryMarkdown, CurrentGroupMessages());
            if (suggestions.Count == 0)
            {
                GroupMemoSuggestionPanel.Visibility = Visibility.Collapsed;
                ShowAppNotice("记录建议", "当前总结里没有识别到适合保存的记录。");
                return;
            }

            GroupMemoSuggestionList.Children.Clear();
            foreach (var suggestion in suggestions)
            {
                GroupMemoSuggestionList.Children.Add(CreateMemoSuggestionItem(suggestion));
            }

            GroupMemoSuggestionPanel.Visibility = Visibility.Visible;
            return;
        }

        if (_currentPage == PersonPage && CurrentSelectedFriend() is { } friend)
        {
            var suggestions = BuildMemoSuggestions("好友", friend.Id, friend.DisplayName, _personSummaryMarkdown, CurrentPersonMessages());
            if (suggestions.Count == 0)
            {
                PersonMemoSuggestionPanel.Visibility = Visibility.Collapsed;
                ShowAppNotice("记录建议", "当前总结里没有识别到适合保存的记录。");
                return;
            }

            PersonMemoSuggestionList.Children.Clear();
            foreach (var suggestion in suggestions)
            {
                PersonMemoSuggestionList.Children.Add(CreateMemoSuggestionItem(suggestion));
            }

            PersonMemoSuggestionPanel.Visibility = Visibility.Visible;
            return;
        }

        ShowAppNotice("记录建议", "请先选择群聊或好友并生成总结。");
    }

    private void CloseMemoSuggestionsClicked(object sender, RoutedEventArgs e)
    {
        GroupMemoSuggestionPanel.Visibility = Visibility.Collapsed;
        PersonMemoSuggestionPanel.Visibility = Visibility.Collapsed;
    }

    private List<MemoSuggestion> BuildMemoSuggestions(string sourceType, string sourceId, string sourceName, string markdown, IReadOnlyList<ChatMessage> messages)
    {
        if (string.IsNullOrWhiteSpace(markdown) || IsSummaryPlaceholder(markdown))
        {
            return [];
        }

        var candidates = ExtractMeaningfulLines(markdown)
            .Select(line => Regex.Replace(line, @"^\s*(#{1,6}|[-*+]|•|\d{1,3}[\.\)])\s*", "").Trim())
            .Where(line => line.Length is >= 12 and <= 180)
            .Where(line => !Regex.IsMatch(line, @"^(说明|摘要|总结|背景|聊天时间跨度|以下|本次|当前)\s*[:：]", RegexOptions.IgnoreCase))
            .Where(line => ExtractEvidenceNumbers(line).Count > 0)
            .Where(IsMemoSuggestionLine)
            .Distinct()
            .Take(5)
            .ToList();

        var suggestions = new List<MemoSuggestion>();
        foreach (var content in candidates)
        {
            if (_memos.Any(memo => memo.SourceType == sourceType && memo.SourceId == sourceId && string.Equals(memo.Content, content, StringComparison.Ordinal)))
            {
                continue;
            }

            var evidenceNumbers = ExtractEvidenceNumbers(content).OrderBy(number => number).ToList();
            var evidencePreview = BuildMemoEvidencePreview(evidenceNumbers, messages);
            var category = ClassifyMemoCategory(content);
            suggestions.Add(new MemoSuggestion(sourceType, sourceId, sourceName, content, evidenceNumbers, evidencePreview, category, DefaultMemoStatus(category)));
        }

        return suggestions;
    }

    private static bool IsMemoSuggestionLine(string line)
    {
        return Regex.IsMatch(
            line,
            "待办|待确认|确认|负责|责任|风险|问题|截止|时间|日期|安排|回复|跟进|推进|提交|完成|承诺|需要|建议|下次|会议|日程",
            RegexOptions.IgnoreCase);
    }

    private static string ClassifyMemoCategory(string content)
    {
        if (Regex.IsMatch(content, "风险|问题|阻塞|延期|不确定|注意|异常|失败", RegexOptions.IgnoreCase))
        {
            return "风险";
        }

        if (Regex.IsMatch(content, "截止|时间|日期|日程|会议|明天|今天|后天|下周|周一|周二|周三|周四|周五|周六|周日|\\d{1,2}[月/.-]\\d{1,2}", RegexOptions.IgnoreCase))
        {
            return "日程";
        }

        if (Regex.IsMatch(content, "待办|待确认|负责|回复|跟进|推进|提交|处理|需要|安排|确认|完成|承诺", RegexOptions.IgnoreCase))
        {
            return "待办";
        }

        return "结论";
    }

    private static string DefaultMemoStatus(string category)
    {
        return category switch
        {
            "待办" => "待处理",
            "风险" => "待确认",
            "日程" => "待安排",
            _ => "已记录"
        };
    }

    private UIElement CreateMemoSuggestionItem(MemoSuggestion suggestion)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var contentStack = new StackPanel();
        var meta = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6)
        };
        meta.Children.Add(CreateMemoDotLabel(suggestion.Category, MemoCategoryBrush(suggestion.Category)));
        meta.Children.Add(CreateMemoMetaText(suggestion.Status));
        contentStack.Children.Add(meta);

        var text = new TextBlock
        {
            Text = suggestion.Content,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(31, 35, 41)),
            Margin = new Thickness(0, 0, 10, 0)
        };
        contentStack.Children.Add(text);
        Grid.SetColumn(contentStack, 0);
        grid.Children.Add(contentStack);

        var saveButton = new Button
        {
            Content = "保存",
            Tag = suggestion,
            MinWidth = 56,
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Background = new SolidColorBrush(Color.FromRgb(7, 193, 96)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        saveButton.Click += SaveMemoSuggestionClicked;
        Grid.SetColumn(saveButton, 1);
        grid.Children.Add(saveButton);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };
    }

    private void SaveMemoSuggestionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MemoSuggestion suggestion })
        {
            return;
        }

        var exists = _memos.Any(memo =>
            memo.SourceType == suggestion.SourceType &&
            memo.SourceId == suggestion.SourceId &&
            string.Equals(memo.Content, suggestion.Content, StringComparison.Ordinal));
        if (!exists)
        {
            _memos.Insert(0, new MemoEntry(
                Guid.NewGuid().ToString("N"),
                suggestion.SourceType,
                suggestion.SourceId,
                suggestion.SourceName,
                suggestion.Content,
                DateTime.Now,
                suggestion.EvidenceNumbers,
                suggestion.EvidencePreview,
                suggestion.Category,
                suggestion.Status));
            _memoService.Save(_memos);
        }

        _memoMode = suggestion.SourceType;
        RefreshMemoPage();
        FlashSavedMemoFeedback(suggestion.SourceType, suggestion.SourceName);
        if (_currentPage == GroupPage && CurrentSelectedChatRoom() is { } room)
        {
            RenderMemoSuggestions(GroupMemoSuggestionPanel, GroupMemoSuggestionList, "群聊", room.Id, room.DisplayName, _groupSummaryMarkdown, CurrentGroupMessages());
        }
        else if (_currentPage == PersonPage && CurrentSelectedFriend() is { } friend)
        {
            RenderMemoSuggestions(PersonMemoSuggestionPanel, PersonMemoSuggestionList, "好友", friend.Id, friend.DisplayName, _personSummaryMarkdown, CurrentPersonMessages());
        }
    }

    private (string SourceType, string SourceId, string SourceName)? CurrentMemoSource()
    {
        if (_currentPage == GroupPage && CurrentSelectedChatRoom() is { } room)
        {
            return ("群聊", room.Id, room.DisplayName);
        }

        if (_currentPage == PersonPage && CurrentSelectedFriend() is { } friend)
        {
            return ("好友", friend.Id, friend.DisplayName);
        }

        return null;
    }

    private IReadOnlyList<ChatMessage> CurrentMemoMessages()
    {
        if (_currentPage == GroupPage)
        {
            return CurrentGroupMessages();
        }

        if (_currentPage == PersonPage)
        {
            return CurrentPersonMessages();
        }

        return [];
    }

    private static string BuildMemoEvidencePreview(IReadOnlyList<int> evidenceNumbers, IReadOnlyList<ChatMessage> messages)
    {
        if (evidenceNumbers.Count == 0 || messages.Count == 0)
        {
            return "";
        }

        return string.Join(Environment.NewLine, evidenceNumbers
            .Where(number => number > 0 && number <= messages.Count)
            .Take(5)
            .Select(number =>
            {
                var message = messages[number - 1];
                var content = message.Content.Length > 80 ? message.Content[..80] + "..." : message.Content;
                return $"#{number:000} {message.Time:MM-dd HH:mm} {message.SenderDisplayName}：{content}";
            }));
    }

    private static Border CreateMemoBadge(string text, Brush background, Brush foreground)
    {
        return new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private static UIElement CreateMemoDotLabel(string text, Brush accent)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        stack.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        stack.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        return stack;
    }

    private static TextBlock CreateMemoMetaText(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
    }

    private static Brush MemoCategoryBrush(string? category)
    {
        return category switch
        {
            "待办" => new SolidColorBrush(Color.FromRgb(7, 193, 96)),
            "风险" => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
            "日程" => new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            _ => new SolidColorBrush(Color.FromRgb(100, 116, 139))
        };
    }

    private static string NormalizeMemoContent(string text)
    {
        return Regex.Replace(text.Trim(), @"\s+", " ");
    }

    private void FlashSavedMemoFeedback(string sourceType, string sourceName)
    {
        ShowAppNotice("已保存", $"这段内容已放入记录：{sourceType}「{sourceName}」。", "success");
    }

    private void MemoGroupTabClicked(object sender, RoutedEventArgs e)
    {
        _memoMode = "群聊";
        RefreshMemoPage();
    }

    private void MemoPersonTabClicked(object sender, RoutedEventArgs e)
    {
        _memoMode = "好友";
        RefreshMemoPage();
    }

    private void RefreshMemoPage()
    {
        if (MemoObjectList is null)
        {
            return;
        }

        EnsureMemoModeHasVisibleContent();
        var isGroupMode = _memoMode == "群聊";
        MemoGroupTabButton.Background = isGroupMode ? new SolidColorBrush(Color.FromRgb(231, 248, 239)) : Brushes.Transparent;
        MemoGroupTabButton.Foreground = isGroupMode ? new SolidColorBrush(Color.FromRgb(6, 173, 86)) : new SolidColorBrush(Color.FromRgb(107, 114, 128));
        MemoPersonTabButton.Background = isGroupMode ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(231, 248, 239));
        MemoPersonTabButton.Foreground = isGroupMode ? new SolidColorBrush(Color.FromRgb(107, 114, 128)) : new SolidColorBrush(Color.FromRgb(6, 173, 86));
        MemoObjectTitle.Text = isGroupMode ? "群聊" : "好友";
        MemoObjectSubtitle.Text = "已保存记录";

        var previousId = (MemoObjectList.SelectedItem as ListBoxItem)?.Tag as string;
        MemoObjectList.Items.Clear();
        var groups = _memos
            .Where(memo => memo.SourceType == _memoMode)
            .GroupBy(memo => new { memo.SourceId, memo.SourceName })
            .OrderByDescending(group => group.Max(memo => memo.CreatedAt))
            .ToList();

        foreach (var group in groups)
        {
            MemoObjectList.Items.Add(CreateMemoObjectItem(group.Key.SourceId, group.Key.SourceName, group.Count()));
        }

        if (MemoObjectList.Items.Count == 0)
        {
            RenderMemoEntries(null);
            return;
        }

        var selected = MemoObjectList.Items.OfType<ListBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, previousId, StringComparison.Ordinal))
            ?? MemoObjectList.Items.OfType<ListBoxItem>().First();
        MemoObjectList.SelectedItem = selected;
        RenderMemoEntries(selected.Tag as string);
    }

    private void EnsureMemoModeHasVisibleContent()
    {
        if (_memos.Any(memo => memo.SourceType == _memoMode))
        {
            return;
        }

        var latest = _memos
            .OrderByDescending(memo => memo.CreatedAt)
            .FirstOrDefault();
        if (latest is not null)
        {
            _memoMode = latest.SourceType;
        }
    }

    private static ListBoxItem CreateMemoObjectItem(string sourceId, string sourceName, int count)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = sourceName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = sourceName
        });
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(231, 248, 239)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(10, 0, 0, 0),
            Child = new TextBlock
            {
                Text = count.ToString(CultureInfo.InvariantCulture),
                Foreground = new SolidColorBrush(Color.FromRgb(6, 173, 86)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            }
        };
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        return new ListBoxItem
        {
            Tag = sourceId,
            Content = grid,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 10, 12, 10)
        };
    }

    private void MemoObjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RenderMemoEntries((MemoObjectList.SelectedItem as ListBoxItem)?.Tag as string);
    }

    private void RenderMemoEntries(string? sourceId)
    {
        if (MemoEntryList is null)
        {
            return;
        }

        MemoEntryList.Items.Clear();
        var entries = string.IsNullOrWhiteSpace(sourceId)
            ? []
            : _memos
                .Where(memo => memo.SourceType == _memoMode && memo.SourceId == sourceId)
                .OrderByDescending(memo => memo.CreatedAt)
                .ToList();

        MemoEmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteMemoButton.IsEnabled = entries.Count > 0;
        if (entries.Count == 0)
        {
            MemoDetailTitle.Text = "最近保存";
            MemoDetailSubtitle.Text = "暂无选择";
            return;
        }

        MemoDetailTitle.Text = entries[0].SourceName;
        MemoDetailSubtitle.Text = $"{entries.Count} 条记录";
        foreach (var memo in entries)
        {
            MemoEntryList.Items.Add(CreateMemoEntryItem(memo));
        }
    }

    private static ListBoxItem CreateMemoEntryItem(MemoEntry memo)
    {
        var stack = new StackPanel();
        var metaGrid = new Grid();
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var typeStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        typeStack.Children.Add(CreateMemoDotLabel(string.IsNullOrWhiteSpace(memo.Category) ? "结论" : memo.Category, MemoCategoryBrush(memo.Category)));
        typeStack.Children.Add(CreateMemoMetaText(string.IsNullOrWhiteSpace(memo.Status) ? "已记录" : memo.Status));
        metaGrid.Children.Add(typeStack);

        var rightMeta = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var evidenceNumbers = memo.EvidenceNumbers?.Where(number => number > 0).Take(4).ToList() ?? [];
        rightMeta.Children.Add(new TextBlock
        {
            Text = memo.CreatedAt.ToString("yyyy/M/d HH:mm", CultureInfo.InvariantCulture),
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, evidenceNumbers.Count > 0 ? 12 : 0, 0)
        });
        if (evidenceNumbers.Count > 0)
        {
            rightMeta.Children.Add(CreateMemoMetaText($"依据 {string.Join(" ", evidenceNumbers.Select(number => $"#{number:000}"))}"));
        }

        Grid.SetColumn(rightMeta, 1);
        metaGrid.Children.Add(rightMeta);
        stack.Children.Add(metaGrid);
        stack.Children.Add(new TextBlock
        {
            Text = memo.Content,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(31, 35, 41)),
            Margin = new Thickness(0, 8, 0, 0)
        });
        if (!string.IsNullOrWhiteSpace(memo.EvidencePreview))
        {
            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 10, 0, 0),
                Child = new TextBlock
                {
                    Text = memo.EvidencePreview,
                    Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                    FontSize = 12,
                    LineHeight = 18,
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        return new ListBoxItem
        {
            Tag = memo.Id,
            Content = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(232, 237, 243)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14),
                Child = stack
            },
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 0, 0, 10)
        };
    }

    private void DeleteSelectedMemoClicked(object sender, RoutedEventArgs e)
    {
        if (MemoEntryList.SelectedItem is not ListBoxItem { Tag: string memoId })
        {
            ShowAppNotice("删除记录", "请先选择一条要删除的摘录。", "warning");
            return;
        }

        _memos.RemoveAll(memo => memo.Id == memoId);
        _memoService.Save(_memos);
        RefreshMemoPage();
    }

    private async Task RefreshAiBalanceAsync()
    {
        if (_isRefreshingBalance)
        {
            return;
        }

        _isRefreshingBalance = true;
        BalanceTotalText.Text = "同步中";
        BalanceDetailText.Text = "正在同步当前 API Key 的服务状态。";

        try
        {
            var balance = await _deepSeekService.GetBalanceAsync(CurrentConfigFromUi());
            if (balance is null || balance.Items.Count == 0)
            {
                BalanceTotalText.Text = "暂未同步额度";
                BalanceDetailText.Text = "请确认 API Key 可用后重试。";
                return;
            }

            var item = balance.Items.FirstOrDefault(info => info.Currency == "CNY") ?? balance.Items[0];
            var total = Math.Max(item.TotalBalance, 0m);
            var currency = string.IsNullOrWhiteSpace(item.Currency) ? "CNY" : item.Currency;
            BalanceTotalText.Text = balance.IsAvailable && total > 0
                ? $"{currency} {total:0.##}"
                : "额度不可用";
            BalanceDetailText.Text = balance.IsAvailable
                ? "已同步当前 API Key 返回的可用额度。"
                : "当前 API Key 暂不可用于生成。";
        }
        catch
        {
            BalanceTotalText.Text = "暂未同步额度";
            BalanceDetailText.Text = "请确认 API Key 可用后重试。";
        }
        finally
        {
            _isRefreshingBalance = false;
        }
    }

    private static string BalanceStatusLabel(decimal total, bool isAvailable)
    {
        if (!isAvailable || total <= 0)
        {
            return "额度已用尽";
        }

        return total switch
        {
            >= 20m => "额度充足",
            >= 5m => "额度正常",
            >= 1m => "额度偏低",
            _ => "即将用尽"
        };
    }

    private static double BalanceRatio(decimal total, bool isAvailable)
    {
        if (!isAvailable || total <= 0)
        {
            return 0;
        }

        return total switch
        {
            >= 20m => 1,
            >= 10m => 0.92,
            >= 5m => 0.82,
            >= 1m => 0.42,
            _ => 0.16
        };
    }

    private void RefreshContextColumnForCurrentPage()
    {
        var usesAnalysisContext = _currentPage == GroupPage || _currentPage == PersonPage;
        var canShowContext = usesAnalysisContext && !_isSummaryFullscreen;
        var shouldShowContext = canShowContext && !_isContextDrawerCollapsed;
        ContextPanel.Visibility = shouldShowContext ? Visibility.Visible : Visibility.Collapsed;
        ContextColumn.Width = shouldShowContext ? new GridLength(260) : new GridLength(0);
        ContextDrawerHandle.Visibility = canShowContext && _isContextDrawerCollapsed ? Visibility.Visible : Visibility.Collapsed;
        MainContentPanel.Margin = new Thickness(34, 30, 34, 32);
    }

    private void ToggleContextDrawerClicked(object sender, RoutedEventArgs e)
    {
        _isContextDrawerCollapsed = !_isContextDrawerCollapsed;
        RefreshContextColumnForCurrentPage();
    }

    private void ToggleSummaryFullscreenClicked(object sender, RoutedEventArgs e)
    {
        _isSummaryFullscreen = !_isSummaryFullscreen;
        RefreshSummaryFullscreenState();
        RefreshContextColumnForCurrentPage();
    }

    private void RefreshSummaryFullscreenState()
    {
        var isGroupPage = _currentPage == GroupPage;
        var isPersonPage = _currentPage == PersonPage;
        var active = _isSummaryFullscreen && (isGroupPage || isPersonPage);
        var groupActive = active && isGroupPage;
        var personActive = active && isPersonPage;

        GroupHeaderPanel.Visibility = groupActive ? Visibility.Collapsed : Visibility.Visible;
        GroupFilterPanel.Visibility = groupActive ? Visibility.Collapsed : Visibility.Visible;
        GroupAnalysisGrid.SetValue(Grid.RowProperty, groupActive ? 0 : 2);
        GroupAnalysisGrid.SetValue(Grid.RowSpanProperty, groupActive ? 3 : 1);
        GroupSummaryPanel.Margin = groupActive ? new Thickness(0) : new Thickness(0, 0, 16, 0);
        GroupEvidencePanel.Visibility = groupActive ? Visibility.Collapsed : Visibility.Visible;
        GroupGridSplitter.Visibility = groupActive ? Visibility.Collapsed : Visibility.Visible;
        GroupSplitterColumn.Width = groupActive ? new GridLength(0) : new GridLength(16);
        GroupEvidenceColumn.Width = groupActive ? new GridLength(0) : new GridLength(320);
        GroupFullscreenButton.ToolTip = groupActive ? "还原文档视图" : "放大文档视图";

        PersonHeaderPanel.Visibility = personActive ? Visibility.Collapsed : Visibility.Visible;
        PersonAnalysisGrid.Margin = new Thickness(0);
        PersonAnalysisGrid.SetValue(Grid.RowProperty, personActive ? 0 : 2);
        PersonAnalysisGrid.SetValue(Grid.RowSpanProperty, personActive ? 3 : 1);
        PersonFilterPanel.Visibility = personActive ? Visibility.Collapsed : Visibility.Visible;
        PersonSummaryPanel.Margin = personActive ? new Thickness(0) : new Thickness(0, 0, 16, 0);
        PersonEvidencePanel.Visibility = personActive ? Visibility.Collapsed : Visibility.Visible;
        PersonGridSplitter.Visibility = personActive ? Visibility.Collapsed : Visibility.Visible;
        PersonSplitterColumn.Width = personActive ? new GridLength(0) : new GridLength(16);
        PersonEvidenceColumn.Width = personActive ? new GridLength(0) : new GridLength(320);
        PersonFullscreenButton.ToolTip = personActive ? "还原文档视图" : "放大文档视图";
    }

    private static readonly Dictionary<char, string> ChineseInitials = new()
    {
        ['陈'] = "C", ['成'] = "C", ['程'] = "C", ['蔡'] = "C", ['曹'] = "C", ['常'] = "C", ['崔'] = "C",
        ['张'] = "Z", ['章'] = "Z", ['赵'] = "Z", ['周'] = "Z", ['郑'] = "Z", ['朱'] = "Z", ['钟'] = "Z", ['邹'] = "Z",
        ['王'] = "W", ['吴'] = "W", ['魏'] = "W", ['汪'] = "W",
        ['李'] = "L", ['刘'] = "L", ['林'] = "L", ['罗'] = "L", ['梁'] = "L", ['卢'] = "L", ['陆'] = "L",
        ['黄'] = "H", ['何'] = "H", ['胡'] = "H", ['韩'] = "H", ['侯'] = "H",
        ['杨'] = "Y", ['叶'] = "Y", ['姚'] = "Y", ['袁'] = "Y", ['余'] = "Y",
        ['徐'] = "X", ['谢'] = "X", ['肖'] = "X", ['许'] = "X", ['夏'] = "X", ['熊'] = "X",
        ['孙'] = "S", ['宋'] = "S", ['沈'] = "S", ['苏'] = "S", ['石'] = "S", ['史'] = "S",
        ['马'] = "M", ['毛'] = "M", ['孟'] = "M", ['莫'] = "M",
        ['郭'] = "G", ['高'] = "G", ['顾'] = "G", ['龚'] = "G",
        ['方'] = "F", ['范'] = "F", ['冯'] = "F", ['傅'] = "F",
        ['杜'] = "D", ['董'] = "D", ['丁'] = "D", ['邓'] = "D", ['戴'] = "D",
        ['唐'] = "T", ['田'] = "T", ['谭'] = "T", ['陶'] = "T",
        ['潘'] = "P", ['彭'] = "P",
        ['蒋'] = "J", ['江'] = "J", ['姜'] = "J", ['金'] = "J", ['贾'] = "J",
        ['秦'] = "Q", ['钱'] = "Q", ['邱'] = "Q",
        ['白'] = "B", ['包'] = "B",
        ['任'] = "R", ['饶'] = "R",
        ['欧'] = "O",
        ['孔'] = "K", ['康'] = "K"
    };

    private sealed record MindMapNode(string Title, List<MindMapNode>? Children = null)
    {
        public List<MindMapNode> Children { get; } = Children ?? [];
    }

    private sealed record WorkspaceEntry(string Label, object Tag, bool IsPinned = false);

    private sealed record SummaryState(string Markdown, IReadOnlyList<ChatMessage> Messages, bool IsEmptyState = false);

    private sealed record SpeakerOption(string Name, int Count);

    private sealed record EvidenceItem(int Number, ChatMessage Message);

    private sealed record EvidenceSegment(int Index, DateTime Start, DateTime End, int CitedCount, IReadOnlyList<EvidenceItem> Items);

    private sealed record CompareEntry(string Id, string Name, IReadOnlyList<ChatMessage> Messages);

    private sealed record MemoSuggestion(
        string SourceType,
        string SourceId,
        string SourceName,
        string Content,
        IReadOnlyList<int> EvidenceNumbers,
        string EvidencePreview,
        string Category,
        string Status);
}

public sealed class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register(nameof(From), typeof(GridLength), typeof(GridLengthAnimation));

    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register(nameof(To), typeof(GridLength), typeof(GridLengthAnimation));

    public GridLength From
    {
        get => (GridLength)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public GridLength To
    {
        get => (GridLength)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction { get; set; }

    public override Type TargetPropertyType => typeof(GridLength);

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        var progress = animationClock.CurrentProgress ?? 0;
        var easedProgress = EasingFunction?.Ease(progress) ?? progress;
        var from = From.Value;
        var to = To.Value;
        return new GridLength(from + ((to - from) * easedProgress));
    }
}
