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

    /// <summary>连通性测试进行中：挡住重复点击，避免重复发请求。</summary>
    private bool _testingLlm;

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
        _appToken = Ui.Input(feishu.AppToken, value => feishu.AppToken = value, "多维表格链接中 /base/ 之后的部分", 380);
        _tableId = Ui.Input(feishu.TableId, value => feishu.TableId = value, "tblxxxxxxxxxxxxxx", 380);

        _status = Ui.Caption(string.Empty);

        Content = Ui.Scroller(Ui.Page(
            Ui.PageTitle("设置"),
            _status,
            BuildLlmCard(),
            BuildFeishuCard(),
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
            : "有未保存的更改，请保存";

        _llmNote.Text = text;
        _feishuNote.Text = text;
        _status.Text = text;
    }

    public void Refresh()
    {
        var summarization = _services.Options.Summarization;
        EnsurePrimaryProvider(summarization);

        RenderProviders();

        // 总开关关掉时把模型区一起置灰：不然子开关还亮着，看不出到底关干净没有。

        var llmReady = Ui.IsLlmReady(_services.Options);
        _llmStatus.Text = llmReady ? "已配置，可生成总结" : "配置不完整：接口地址、模型名称、API Key 均为必填";
        _llmStatus.Foreground = new SolidColorBrush(Color.Parse(llmReady ? "#15803D" : "#B45309"));

        var feishuReady = Ui.IsFeishuReady(_services.Options);
        _feishuStatus.Text = feishuReady ? "已配置，可推送到多维表格" : "配置不完整：以下四项均为必填";
        _feishuStatus.Foreground = new SolidColorBrush(Color.Parse(feishuReady ? "#15803D" : "#B45309"));

    }

    // ---------------------------------------------------------------- 大模型

    private Control BuildLlmCard()
    {
        var head = Ui.FieldRow("启用大模型总结", _summaryEnabled,
            tip: "关闭时不调用任何模型接口，只保留本地记录。");

        // 按钮在设置项之后：先看到要设什么，再保存；页面本身可滚动，不用担心按钮被挤出屏幕。
        var buttons = Ui.ButtonRow(
            Ui.Primary("保存大模型设置", () => SaveAsync(_llmNote)),
            Ui.Secondary("测试连接", TestLlmAsync),
            _llmNote);

        return Ui.CardWith(
            Ui.Header("大模型配置", "按顺序调用，前一个失败自动换下一个；接口地址一般以 /v1 结尾。"),
            _llmStatus, head, _providers, buttons);
    }

    /// <summary>把进程名说成人话：勾选清单里出现 et / msedge 这种名字，没人知道是什么。</summary>
    /// <summary>删模型是不可撤销的（密钥跟着一起没了），所以确认一下。</summary>
    private async Task RemoveProviderAsync(LlmProviderOptions provider)
    {
        if (OwnerWindow() is not { } owner)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(provider.Name) ? provider.Model : provider.Name;

        if (!await Ui.Confirm(owner, "删除这个模型？", $"模型 {name} 将从列表中移除，已填写的密钥会一并删除。", "删除", danger: true))
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
            Ui.FieldRow("启用飞书推送", _feishuEnabled, tip: "关闭后，每日流程不再推送到飞书。"),
            Ui.FieldRow("App ID", _appId, tip: "自建应用的 App ID。"),
            Ui.FieldRow("App Secret", _appSecret, tip: "留空则读取环境变量 SNAPLOG_FEISHU_APP_SECRET。"),
            Ui.FieldRow("多维表格 token", _appToken, tip: "表格链接中 /base/ 之后的部分。"),
            Ui.FieldRow("数据表 ID", _tableId, tip: "形如 tblxxxxxxxxxxxxxx。"));

        var buttons = Ui.ButtonRow(Ui.Primary("保存飞书设置", () => SaveAsync(_feishuNote)), _feishuNote);

        return Ui.CardWith(
            Ui.Header("飞书配置", "把每天的总结按日期写入多维表格，一天一行；需要多维表格的读写权限。"),
            _feishuStatus, rows, buttons);
    }

    private Control BuildAboutCard()
    {
        var buttons = Ui.ButtonRow(
            Ui.Secondary("高级设置", OpenAdvancedAsync),
            Ui.Secondary("关于", () => { Ui.ShowDialog(this, new AboutWindow(_services)); return Task.CompletedTask; }),
            Ui.Secondary("退出 SnapLog", ExitAsync));

        return Ui.CardWith(
            Ui.Header("其他", "关窗只收进托盘，仍在记录；退出请用托盘菜单。"
                              + "排除的程序、附加要求、工作项目与字段映射都在高级设置里。"),
            null, buttons);
    }

    /// <summary>高级设置独立成弹窗：里面的选项多数人一辈子不改一次。</summary>
    private Task OpenAdvancedAsync()
    {
        // 弹窗保存后要让本页重新取一次快照，否则会一直显示"有未保存的更改"。
        var dialog = new AdvancedSettingsWindow(_services, () =>
        {
            _savedSnapshot = Snapshot();
            _lastSaveMessage = string.Empty;
            ShowDirtyState();
        });

        Ui.ShowDialog(this, dialog);
        return Task.CompletedTask;
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

    /// <summary>
    /// 逐个模型做一次真实的小请求：列表里每个开启的模型都要能对上地址、密钥、模型名。
    /// 只测第一个是不够的——备用模型配错了，真要回退时才发现，那就晚了。
    /// </summary>
    private async Task TestLlmAsync()
    {
        // 手快连点两次会发两轮请求；测试期间第二下直接忽略。
        if (_testingLlm || OwnerWindow() is not { } owner)
        {
            return;
        }

        _testingLlm = true;
        try
        {
            await RunLlmTestAsync(owner);
        }
        finally
        {
            _testingLlm = false;
        }
    }

    private async Task RunLlmTestAsync(Window owner)
    {
        if (_services.SaveOptions())
        {
            _savedSnapshot = Snapshot();
            _lastSaveMessage = "已保存　" + DateTime.Now.ToString("HH:mm:ss");
        }

        var all = _services.Options.Summarization;
        var targets = all.Providers.Where(provider => provider.Enabled).ToList();

        if (targets.Count == 0)
        {
            await Ui.Info(owner, "没有可测的模型", "请先启用要测试的模型；未启用的模型不会参与每日流程。");
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
            passed == targets.Count ? "所有已启用的模型均可用" : "部分模型不可用",
            string.Join(Environment.NewLine, lines)
            + Environment.NewLine + Environment.NewLine
            + "地址、密钥或模型名称任一有误都会失败；接口地址通常以 /v1 结尾。");
    }

    private Task ExitAsync()
    {
        // 托盘菜单里的退出是直接退的，这里也保持一致（多点一次反而让人以为没退成）。
        _services.ExitApplication();
        return Task.CompletedTask;
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
