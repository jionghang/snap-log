using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Text.Json;
using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Storage;
using SnapLog.Summarization;

namespace SnapLog.Ui;

/// <summary>
/// 设置页只放三类东西：
///   1. 大模型接入（要填的只有地址、模型名、密钥）
///   2. 飞书接入（要填的只有四个标识）
///   3. 进阶——折叠着，里面只剩三件"确实有人要改"的事：不记录的软件、附加要求、工作项目清单与字段映射。
///
/// 其余参数刻意不提供。抓取节奏、识别引擎、保留策略、重试与超时、图片格式……
/// 都有经过验证的默认值，放在界面里只会让人以为"必须调"，实际只是增加负担。
/// 真要改仍然可以编辑 appsettings.json（格式和 1.x 一样）。
/// </summary>
internal sealed class SettingsPage : UserControl, IRefreshable
{
    private readonly AppServices _services;

    private readonly TextBlock _llmStatus;
    private readonly ToggleSwitch _summaryEnabled;
    private readonly StackPanel _providers;

    private readonly TextBlock _feishuStatus;
    private readonly ToggleSwitch _feishuEnabled;
    private readonly TextBox _appId;
    private readonly TextBox _appSecret;
    private readonly TextBox _appToken;
    private readonly TextBox _tableId;

    private readonly StackPanel _excluded;
    private readonly TextBox _extraInstructions;
    private readonly StackPanel _projects;
    private readonly StackPanel _mappings;
    private readonly TextBlock _advancedStatus;

    private readonly TextBlock _status;

    /// <summary>上次保存时的配置快照。跟当前值不一样就说明有未保存的改动（比逐个控件挂事件稳）。</summary>
    private string _savedSnapshot = string.Empty;
    private DispatcherTimer? _dirtyWatch;
    private readonly TextBlock _llmNote = Ui.Caption(string.Empty);
    private readonly TextBlock _feishuNote = Ui.Caption(string.Empty);

    public SettingsPage(AppServices services)
    {
        _services = services;

        var summarization = services.Options.Summarization;
        EnsurePrimaryProvider(summarization);

        _llmStatus = Ui.Caption(string.Empty);
        _summaryEnabled = Ui.Switch(summarization.Enabled, value => summarization.Enabled = value);
        _providers = new StackPanel { Spacing = 14 };

        var feishu = services.Options.Feishu;
        _feishuStatus = Ui.Caption(string.Empty);
        _feishuEnabled = Ui.Switch(feishu.Enabled, value => feishu.Enabled = value);
        _appId = Ui.Input(feishu.AppId, value => feishu.AppId = value, "cli_xxxxxxxxxxxxx", 380);
        _appSecret = Ui.Secret(feishu.AppSecret, value => feishu.AppSecret = value, 380);
        _appToken = Ui.Input(feishu.AppToken, value => feishu.AppToken = value, "多维表格 URL 里 /base/ 之后那一串", 380);
        _tableId = Ui.Input(feishu.TableId, value => feishu.TableId = value, "tblxxxxxxxxxxxxxx", 380);

        // 勾选面板：内容在 Refresh 里按"库里出现过的程序"填（手打进程名不现实）。
        _excluded = new StackPanel { Spacing = 6 };
        _extraInstructions = BuildMultiline(services.Options.Summarization.ExtraInstructions, 3,
            value => services.Options.Summarization.ExtraInstructions = value);
        _projects = new StackPanel { Spacing = 8 };
        _mappings = new StackPanel { Spacing = 8 };
        _advancedStatus = Ui.Caption(string.Empty);

        _status = Ui.Caption(string.Empty);

        Content = Ui.Scroller(Ui.Page(
            Ui.PageTitle("设置"),
            _status,
            BuildLlmCard(),
            BuildFeishuCard(),
            BuildAdvancedCard(),
            BuildAboutCard()));

        _savedSnapshot = Snapshot();
        _dirtyWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dirtyWatch.Tick += (_, _) => ShowDirtyState();

        // 页面被复用：切回来要重新开始盯着，否则之后再改配置都不会提示"未保存"。
        AttachedToVisualTree += (_, _) =>
        {
            _dirtyWatch?.Start();
            ShowDirtyState();
        };

        DetachedFromVisualTree += (_, _) => _dirtyWatch?.Stop();
        _dirtyWatch.Start();

        Refresh();
    }

    private string Snapshot() => JsonSerializer.Serialize(_services.Options);

    /// <summary>最近一次保存的回执；改回原样时用它把提示恢复过来。</summary>
    private string _lastSaveMessage = string.Empty;

    /// <summary>每秒比一次：配置被改过就提示"有未保存的更改"，改回原样或保存后自动恢复。</summary>
    private void ShowDirtyState()
    {
        var text = string.Equals(Snapshot(), _savedSnapshot, StringComparison.Ordinal)
            ? _lastSaveMessage
            : "有未保存的更改，记得点保存";

        _llmNote.Text = text;
        _feishuNote.Text = text;
        _advancedStatus.Text = text;
        _status.Text = text;
    }

    public void Refresh()
    {
        var summarization = _services.Options.Summarization;
        EnsurePrimaryProvider(summarization);

        RenderProviders();

        // 总开关关掉时把模型区一起置灰：不然子开关还亮着，看不出到底关干净没有。
        _providers.IsEnabled = summarization.Enabled;

        var llmReady = Ui.IsLlmReady(_services.Options);
        _llmStatus.Text = llmReady ? "已配置，可以生成总结" : "还没配好：接口地址、模型名称、API Key 三项都要有";
        _llmStatus.Foreground = new SolidColorBrush(Color.Parse(llmReady ? "#15803D" : "#B45309"));

        var feishuReady = Ui.IsFeishuReady(_services.Options);
        _feishuStatus.Text = feishuReady ? "已配置，可以写入多维表格" : "还没配好：下面四个标识都要填";
        _feishuStatus.Foreground = new SolidColorBrush(Color.Parse(feishuReady ? "#15803D" : "#B45309"));

        RenderProjects();
        RenderMappings();
        _ = RenderExcludedAsync();
    }

    // ---------------------------------------------------------------- 大模型

    private Control BuildLlmCard()
    {
        var head = Ui.FieldRow("启用大模型总结", _summaryEnabled);

        var hint = Ui.Hint("按顺序调用：前一个失败就换下一个。接口地址填任意 OpenAI 兼容服务的地址，一般以 /v1 结尾。");
        hint.Margin = new Avalonia.Thickness(0, 10, 0, 0);

        // 保存行放在卡片顶部：模型多的时候卡片会很长，按钮留在底部就会被挤出屏幕。
        var buttons = Ui.ButtonRow(
            Ui.Primary("保存大模型设置", () => SaveAsync(_llmNote)),
            Ui.Secondary("测试连接", TestLlmAsync),
            _llmNote);

        return Ui.CardWithHeader("大模型", _llmStatus, head, buttons, hint, _providers);
    }

    /// <summary>把进程名说成人话：勾选清单里出现 et / msedge 这种名字，没人知道是什么。</summary>
    private static string FriendlyName(string processName)
    {
        var known = processName.ToLowerInvariant() switch
        {
            "et" or "wps" or "wpp" or "wpspdf" => "WPS Office",
            "msedge" => "Microsoft Edge",
            "chrome" => "Google Chrome",
            "firefox" => "Firefox",
            "wechat" or "weixin" => "微信",
            "dingtalk" => "钉钉",
            "feishu" or "lark" => "飞书",
            "explorer" => "文件资源管理器",
            "notepad" => "记事本",
            "code" => "VS Code",
            "devenv" => "Visual Studio",
            "explorer.exe" => "文件资源管理器",
            "snaplog" => "SnapLog 自己",
            "windowsterminal" or "conhost" or "cmd" or "powershell" or "pwsh" => "终端",
            "python" or "pythonw" => "Python",
            "outlook" => "Outlook",
            "teams" => "Microsoft Teams",
            _ => string.Empty,
        };

        return known.Length > 0 ? $"{known}（{processName}）" : processName;
    }

    /// <summary>删模型是不可撤销的（密钥跟着一起没了），所以确认一下。</summary>
    private async Task RemoveProviderAsync(LlmProviderOptions provider)
    {
        if (OwnerWindow() is not { } owner)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(provider.Name) ? provider.Model : provider.Name;

        if (!await Ui.Confirm(owner, "删除这个模型？", $"模型 {name} 会从列表里去掉，它填的密钥也会一起消失。", "删除", danger: true))
        {
            return;
        }

        _services.Options.Summarization.Providers.Remove(provider);
        RenderProviders();
    }

    /// <summary>模型列表：可以加多个，按顺序回退；界面上直接维护，不用改配置文件。</summary>
    private void RenderProviders()
    {
        _providers.Children.Clear();

        var list = _services.Options.Summarization.Providers;

        for (var index = 0; index < list.Count; index++)
        {
            var provider = list[index];
            var caption = new TextBlock
            {
                Text = index == 0 ? "主模型" : $"备用 {index}",
                Classes = { "field" },
                FontWeight = FontWeight.SemiBold,
            };

            var enabled = Ui.Switch(provider.Enabled, value => provider.Enabled = value);
            var endpoint = Ui.Input(provider.Endpoint, value => provider.Endpoint = value, "接口地址，如 https://api.deepseek.com/v1", 360);
            var model = Ui.Input(provider.Model, value => provider.Model = value, "模型名，如 deepseek-chat", 360);
            var key = Ui.Secret(provider.ApiKey, value => provider.ApiKey = value, 360);

            var rows = Ui.RowStack(
                Ui.FieldRow("启用", enabled),
                Ui.FieldRow("接口地址", endpoint),
                Ui.FieldRow("模型名称", model),
                Ui.FieldRow("API Key", key));

            var block = new StackPanel { Spacing = 12 };
            block.Children.Add(caption);
            block.Children.Add(rows);

            if (list.Count > 1)
            {
                block.Children.Add(Ui.ButtonRow(Ui.Link("删除这个模型", () => _ = RemoveProviderAsync(provider))));
            }

            _providers.Children.Add(block);

            if (index < list.Count - 1)
            {
                _providers.Children.Add(Ui.Divider());
            }
        }

        _providers.Children.Add(Ui.ButtonRow(Ui.Secondary("添加模型", () =>
        {
            list.Add(new LlmProviderOptions { Name = "备用模型" });
            RenderProviders();
            return Task.CompletedTask;
        })));
    }

    // ---------------------------------------------------------------- 飞书

    private Control BuildFeishuCard()
    {
        var rows = Ui.RowStack(
            Ui.FieldRow("启用飞书推送", _feishuEnabled, "关掉后每日流程里没有写入飞书这一步。"),
            Ui.FieldRow("App ID", _appId, "自建应用的 App ID。"),
            Ui.FieldRow("App Secret", _appSecret, "留空则读环境变量 SNAPLOG_FEISHU_APP_SECRET。"),
            Ui.FieldRow("多维表格 token", _appToken, "表格链接里 /base/ 之后那串。"),
            Ui.FieldRow("数据表 ID", _tableId, "形如 tblxxxxxxxxxxxxxx。"));

        var buttons = Ui.ButtonRow(Ui.Primary("保存飞书设置", () => SaveAsync(_feishuNote)), _feishuNote);

        return Ui.CardWithHeader("飞书", _feishuStatus, rows, buttons);
    }

    // ---------------------------------------------------------------- 进阶（折叠）

    private Control BuildAdvancedCard()
    {
        var panel = new StackPanel { Spacing = 14 };

        panel.Children.Add(Ui.FieldRow(
            "不记录的软件",
            new ScrollViewer { Content = _excluded, MaxHeight = 240, MinWidth = 420 },
            "勾上的不再记录。密码管理器这类请勾上。"));

        panel.Children.Add(Ui.FieldRow(
            "附加要求",
            _extraInstructions,
            "追加到提示词末尾，例如：只写完成的事和结论。"));

        var projectsBlock = new StackPanel { Spacing = 10 };
        projectsBlock.Children.Add(Ui.Section("工作项目清单"));
        projectsBlock.Children.Add(Ui.Hint("填了以后，总结按这些项目分组写。"));
        projectsBlock.Children.Add(_projects);
        panel.Children.Add(projectsBlock);

        var mappingsBlock = new StackPanel { Spacing = 10 };
        mappingsBlock.Children.Add(Ui.Section("飞书字段映射"));
        mappingsBlock.Children.Add(Ui.Hint("右边填飞书表里的列名，必须完全一致；也可以点下面自动匹配。"));
        mappingsBlock.Children.Add(_mappings);
        panel.Children.Add(mappingsBlock);

        panel.Children.Add(Ui.ButtonRow(Ui.Primary("保存这些设置", SaveAdvancedAsync), _advancedStatus));

        var expander = new Expander
        {
            Header = "进阶设置（多数情况不用改）",
            Content = panel,
            IsExpanded = false,
        };

        return Ui.Card(null, null, expander);
    }

    private Control BuildAboutCard()
    {
        var buttons = Ui.ButtonRow(
            Ui.Secondary("关于", () => { Ui.ShowDialog(this, new AboutWindow(_services)); return Task.CompletedTask; }),
            Ui.Secondary("退出 SnapLog", ExitAsync));

        return Ui.Card("其他", "关闭窗口只是收进托盘、仍在记录；要停就用托盘菜单里的退出。换机器时把数据目录整个拷过去即可。", buttons);
    }

    private Window? OwnerWindow() => TopLevel.GetTopLevel(this) as Window;

    // ---------------------------------------------------------------- 状态判断

    private bool IsLlmReady()
    {
        var provider = _services.Options.Summarization.Providers.FirstOrDefault();
        return provider is not null
               && !string.IsNullOrWhiteSpace(provider.Endpoint)
               && !string.IsNullOrWhiteSpace(provider.Model)
               && (!string.IsNullOrWhiteSpace(provider.ApiKey)
                   || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                       string.IsNullOrWhiteSpace(provider.ApiKeyEnvironmentVariable)
                           ? "SNAPLOG_OPENAI_API_KEY"
                           : provider.ApiKeyEnvironmentVariable)));
    }

    private bool IsFeishuReady()
    {
        var feishu = _services.Options.Feishu;
        return !string.IsNullOrWhiteSpace(feishu.AppId)
               && (!string.IsNullOrWhiteSpace(feishu.AppSecret)
                   || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                       string.IsNullOrWhiteSpace(feishu.AppSecretEnvironmentVariable)
                           ? "SNAPLOG_FEISHU_APP_SECRET"
                           : feishu.AppSecretEnvironmentVariable)))
               && !string.IsNullOrWhiteSpace(feishu.AppToken)
               && !string.IsNullOrWhiteSpace(feishu.TableId);
    }

    // ---------------------------------------------------------------- 动作

    /// <summary>反馈写在触发它的那个按钮旁边，顺手也写一份到页顶。</summary>
    private void SetSaveNotes(string message)
    {
        _llmNote.Text = message;
        _feishuNote.Text = message;
        _advancedStatus.Text = message;
        _status.Text = message;
    }

    private void SetNote(TextBlock note, string message)
    {
        note.Text = message;
        _status.Text = message;
    }

    private async Task SaveAsync(TextBlock? note = null)
    {
        if (_services.SaveOptions())
        {
            Refresh();

            var message = "已保存　" + DateTime.Now.ToString("HH:mm:ss");
            _savedSnapshot = Snapshot();
            _lastSaveMessage = message;

            if (note is null)
            {
                SetSaveNotes(message);
            }
            else
            {
                SetNote(note, message);
            }

            return;
        }

        if (OwnerWindow() is { } owner)
        {
            await Ui.Info(owner, "配置没能写入磁盘", "请检查数据目录的写权限。改动已经在内存里生效，但重启后会丢失。");
        }
    }

    private Task SaveAdvancedAsync()
    {
        if (_services.SaveOptions())
        {
            var message = "已保存　" + DateTime.Now.ToString("HH:mm:ss");
            _savedSnapshot = Snapshot();
            _lastSaveMessage = message;
            SetSaveNotes(message);
        }
        else
        {
            SetSaveNotes("保存失败：数据目录可能没有写权限。");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 逐个模型做一次真实的小请求：列表里每个开启的模型都要能对上地址、密钥、模型名。
    /// 只测第一个是不够的——备用模型配错了，真要回退时才发现，那就晚了。
    /// </summary>
    private async Task TestLlmAsync()
    {
        if (OwnerWindow() is not { } owner)
        {
            return;
        }

        if (_services.SaveOptions())
        {
            _savedSnapshot = Snapshot();
            _lastSaveMessage = "已保存　" + DateTime.Now.ToString("HH:mm:ss");
        }

        var all = _services.Options.Summarization;
        var targets = all.Providers.Where(provider => provider.Enabled).ToList();

        if (targets.Count == 0)
        {
            await Ui.Info(owner, "没有可测的模型", "把要测的模型打开「启用」再测；关着的模型不会参与每日流程。");
            return;
        }

        SetNote(_llmNote, "正在测试 " + targets.Count + " 个模型…");

        var lines = new List<string>();
        var passed = 0;

        foreach (var skipped in all.Providers.Where(provider => !provider.Enabled))
        {
            lines.Add((string.IsNullOrWhiteSpace(skipped.Name) ? skipped.Model : skipped.Name) + "：未启用，已跳过");
        }

        foreach (var provider in targets)
        {
            var name = string.IsNullOrWhiteSpace(provider.Name) ? provider.Model : provider.Name;

            // 只带这一个模型、且不重试：这样每条结果就对应一个模型的真实情况。
            var single = new SummarizationOptions
            {
                Enabled = true,
                Providers = [provider],
                PayloadMode = LlmPayloadMode.TextOnly,
                RetryCount = 0,
                RequestTimeoutSeconds = 30,
            };

            if (!OpenAiCompatibleSummarizer.TryCreate(single, _services.Log, out var summarizer, out var createError))
            {
                lines.Add(name + "：还不能测（" + (createError ?? "配置不完整") + "）");
                continue;
            }

            try
            {
                var request = new SummaryRequest("这是一次连通性测试。", "只回复两个字：正常", [], LlmImageDetail.Auto);
                var completion = await summarizer!.SummarizeAsync(request, CancellationToken.None);
                passed++;
                lines.Add(name + "：通过（返回 " + Ui.Shorten(completion.Text, 20) + "）");
            }
            catch (Exception ex)
            {
                _services.Log.Warn("模型 " + name + " 连通性测试失败：" + ex.Message);
                lines.Add(name + "：失败（" + Ui.Shorten(ex.Message, 60) + "）");
            }
        }

        SetNote(_llmNote, "测试完成：" + passed + " / " + targets.Count + " 个模型可用　" + DateTime.Now.ToString("HH:mm:ss"));

        await Ui.Info(owner,
            passed == targets.Count ? "已启用的模型都能用" : "有模型不能用",
            string.Join(Environment.NewLine, lines)
            + Environment.NewLine + Environment.NewLine
            + "地址、密钥、模型名三者任一不对都会失败；接口地址通常要以 /v1 结尾。");
    }

    private async Task PushAsync()
    {
        if (OwnerWindow() is not { } owner)
        {
            return;
        }

        if (_services.SaveOptions())
        {
            _savedSnapshot = Snapshot();
            _lastSaveMessage = "已保存　" + DateTime.Now.ToString("HH:mm:ss");
        }

        if (!IsFeishuReady())
        {

            return;
        }

        _status.Text = "正在写入飞书…";

        try
        {
            var result = await _services.PushToFeishuAsync(CancellationToken.None);
            _status.Text = result.Message;

            if (!result.Success)
            {
                await Ui.Info(owner, "写入未成功", result.Message);
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error("写入飞书失败", ex);
            _status.Text = $"写入失败：{ex.Message}";
        }
    }

    private Task ExitAsync()
    {
        // 托盘菜单里的退出是直接退的，这里也保持一致（多点一次反而让人以为没退成）。
        _services.ExitApplication();
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- 列表编辑器

    private void RenderProjects()
    {
        _projects.Children.Clear();

        var list = _services.Options.Summarization.WorkProjects;

        foreach (var project in list.ToList())
        {
            var name = new TextBox { Text = project.Name, Width = 130, PlaceholderText = "项目名称" };
            name.TextChanged += (_, _) => project.Name = name.Text ?? string.Empty;

            var description = new TextBox
            {
                Text = project.Description,
                Width = 320,
                PlaceholderText = "说明：工作内容 / 涉及系统 / 关键词",
            };

            // 框窄，长说明会被截断；鼠标停上去能看到全文。
            ToolTip.SetTip(description, project.Description);
            description.TextChanged += (_, _) => project.Description = description.Text ?? string.Empty;

            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(name);
            line.Children.Add(description);
            line.Children.Add(Ui.Link("删除", () =>
            {
                list.Remove(project);
                RenderProjects();
            }));
            _projects.Children.Add(line);
        }

        var add = Ui.Secondary("添加项目", () =>
        {
            list.Add(new WorkProjectOptions());
            RenderProjects();
            return Task.CompletedTask;
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(add);
        _projects.Children.Add(actions);
    }

    private void RenderMappings()
    {
        _mappings.Children.Clear();

        var list = _services.Options.Feishu.FieldMappings;

        foreach (var mapping in list.ToList())
        {
            _mappings.Children.Add(BuildMappingRow(mapping, list));
        }

        var add = Ui.Secondary("添加映射", () =>
        {
            var mapping = new FeishuFieldMapping
            {
                RecordField = nameof(SummaryRun.Markdown),
                FeishuField = string.Empty,
            };
            list.Add(mapping);
            RenderMappings();
            return Task.CompletedTask;
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(add);
        actions.Children.Add(Ui.Secondary("按列名自动匹配", AutoMatchFieldsAsync));
        _mappings.Children.Add(actions);
    }

    /// <summary>
    /// 勾选式的不记录清单：让用户手打进程名是不现实的（谁知道密码管理器的进程叫什么）。
    /// 列的是库里出现过的程序，勾一下就排除。
    /// </summary>
    private async Task RenderExcludedAsync()
    {
        try
        {
            var seen = await _services.Store.GetProcessNamesAsync(CancellationToken.None);
            var excluded = _services.Options.Triggers.ExcludedProcesses;

            var names = seen
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Concat(excluded)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _excluded.Children.Clear();

            foreach (var name in names)
            {
                var isExcluded = excluded.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));

                _excluded.Children.Add(Ui.Check(FriendlyName(name), isExcluded, checkedNow =>
                {
                    var list = _services.Options.Triggers.ExcludedProcesses.ToList();

                    if (checkedNow)
                    {
                        if (!list.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)))
                        {
                            list.Add(name);
                        }
                    }
                    else
                    {
                        list.RemoveAll(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));
                    }

                    _services.Options.Triggers.ExcludedProcesses = [.. list];
                }));
            }

            if (names.Count == 0)
            {
                _excluded.Children.Add(Ui.Caption("还没有记录过任何程序。"));
            }
        }
        catch (Exception ex)
        {
            _services.Log.Warn($"读取进程清单失败：{ex.Message}");
            _excluded.Children.Add(Ui.Caption("读取进程清单失败，可以先跳过这一项。"));
        }
    }

    /// <summary>
    /// 拉一次飞书表的列名，按关键词猜出对应关系，让用户"确认"而不是"从零填"。
    /// 只做一次只读请求，不写任何数据。
    /// </summary>
    private async Task AutoMatchFieldsAsync()
    {
        if (OwnerWindow() is not { } owner)
        {
            return;
        }

        if (!Ui.IsFeishuReady(_services.Options))
        {
            await Ui.Info(owner, "还差接入信息", "先把 App ID、App Secret、多维表格 token、数据表 ID 填好。");
            return;
        }

        if (_services.SaveOptions())
        {
            _savedSnapshot = Snapshot();
            _lastSaveMessage = "已保存　" + DateTime.Now.ToString("HH:mm:ss");
        }

        _advancedStatus.Text = "正在读取飞书表的列名…";

        try
        {
            var publisher = new FeishuBitablePublisher(_services.Options.Feishu, _services.Log);
            var (fields, error) = await publisher.FetchFieldsAsync(CancellationToken.None);

            if (fields is null)
            {
                _advancedStatus.Text = "读取列名失败";
                await Ui.Info(owner, "读取列名失败", error ?? "未知原因。");
                return;
            }

            var matched = FeishuFieldMatcher.Match(fields, _services.Options.Feishu.FieldMappings);
            _services.Options.Feishu.FieldMappings = matched;
            RenderMappings();

            var lines = matched.Select(m =>
                FeishuFieldMapping.DescribeField(m.RecordField)
                + " 写成 "
                + (m.FeishuField.Length > 0 ? m.FeishuField : "（没找到对应列，将跳过）"));

            _advancedStatus.Text = "已匹配 " + matched.Count(m => m.FeishuField.Length > 0) + " 列，点保存生效";

            await Ui.Info(owner, "已按列名匹配",
                "表里读到 " + fields.Count + " 列。"
                + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, lines)
                + Environment.NewLine + Environment.NewLine
                + "核对无误后点保存。");
        }
        catch (Exception ex)
        {
            _services.Log.Error("自动匹配列名失败", ex);
            _advancedStatus.Text = "自动匹配失败";
            await Ui.Info(owner, "自动匹配失败", ex.Message);
        }
    }

    private Control BuildMappingRow(FeishuFieldMapping mapping, List<FeishuFieldMapping> owner)
    {
        var fields = FeishuFieldMapping.AvailableFields;
        var picker = new ComboBox { Width = 190 };

        var index = 0;
        for (var i = 0; i < fields.Count; i++)
        {
            picker.Items.Add(new ComboBoxItem { Content = FeishuFieldMapping.DescribeField(fields[i]), Tag = fields[i] });
            if (string.Equals(fields[i], mapping.RecordField, StringComparison.Ordinal))
            {
                index = i;
            }
        }

        picker.SelectedIndex = index;
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is ComboBoxItem { Tag: string field })
            {
                mapping.RecordField = field;
            }
        };

        var column = new TextBox { Text = mapping.FeishuField, Width = 220, PlaceholderText = "飞书列名（留空则不写）" };
        column.TextChanged += (_, _) => mapping.FeishuField = column.Text ?? string.Empty;

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(picker);
        line.Children.Add(column);
        line.Children.Add(Ui.Link("删除", () =>
        {
            owner.Remove(mapping);
            RenderMappings();
        }));

        return line;
    }

    private static TextBox BuildMultiline(string text, int lines, Action<string> changed)
    {
        var box = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 22 * lines + 18,
            Width = 420,
        };
        box.TextChanged += (_, _) => changed(box.Text ?? string.Empty);
        return box;
    }

    /// <summary>
    /// 界面上只维护一个模型；模型列表为空时补一条默认的，
    /// 用户以后在 appsettings.json 里加的备用模型不会被这里覆盖。
    /// </summary>
    private static void EnsurePrimaryProvider(SummarizationOptions summarization)
    {
        summarization.Providers ??= [];

        if (summarization.Providers.Count == 0)
        {
            summarization.Providers.Add(new LlmProviderOptions());
        }
    }
}
