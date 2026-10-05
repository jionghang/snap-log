using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>
/// 高级设置：独立窗口，选项按用途分组（记录范围 / 总结偏好 / 工作项目 / 飞书字段映射）。
///
/// 从设置页拆出来的原因：这些选项多数人一辈子不改一次，铺在页面里会把常规设置淹掉。
/// 独立窗口也给每项留出了说明空间，设置页本身保持简短。
/// </summary>
internal sealed class AdvancedSettingsWindow : Window
{
    private readonly AppServices _services;

    /// <summary>保存成功后的回调：设置页用它刷新未保存快照，避免误报"有未保存的更改"。</summary>
    private readonly Action? _saved;

    private readonly Panel _excluded;
    private readonly TextBox _extraInstructions;
    private readonly StackPanel _projects;
    private readonly StackPanel _mappings;
    private readonly TextBlock _status = Ui.Caption(string.Empty);

    public AdvancedSettingsWindow(AppServices services, Action? saved = null)
    {
        _services = services;
        _saved = saved;

        Title = "高级设置";
        Width = 780;
        Height = 880;          // 常用数据量下尽量一屏放下，减少滚动
        MinWidth = 620;
        MinHeight = 460;
        MaxHeight = 980;       // 别超过工作区（本机 1040，留出任务栏）
        Icon = IconFactory.WindowIcon;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // 次要窗口的通用约定：Esc 关闭。
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        // 勾选面板：按"库里出现过的程序"填（手打进程名不现实）。
        // 流式排列：条目多时自动换行，不用滚动区，也不会出现半个条目被裁掉。
        _excluded = new WrapPanel { ItemWidth = 210, ItemHeight = 30 };
        _extraInstructions = BuildMultiline(
            services.Options.Summarization.ExtraInstructions, 3,
            value => services.Options.Summarization.ExtraInstructions = value);
        _projects = new StackPanel { Spacing = 8 };
        _mappings = new StackPanel { Spacing = 8 };

        Content = Ui.Scroller(Ui.Page(
            BuildRecordingCard(),
            BuildSummaryCard(),
            BuildProjectsCard(),
            BuildMappingsCard(),
            Ui.ButtonRow(Ui.Primary("保存高级设置", SaveAsync), _status)));

        RenderProjects();
        RenderMappings();
        _ = RenderExcludedAsync();
    }

    // ---------------------------------------------------------------- 分组

    private Control BuildRecordingCard() => Ui.CardWith(
        Ui.Header("记录范围",
            "按进程名匹配：勾选后不再截图，也不会进入总结；已有记录不受影响。"
            + "清单取自历史记录中出现过的程序。"),
        null,
        Ui.FieldRow("排除的程序", _excluded));

    private Control BuildSummaryCard() => Ui.CardWith(
        Ui.Header("总结偏好",
            "这段文字会原样追加到发给模型的提示词末尾，用于补充写作要求，"
            + "例如：只写完成的事和结论。留空则不加。"),
        null,
        Ui.FieldRow("附加要求", _extraInstructions));

    private Control BuildProjectsCard() => Ui.CardWith(
        Ui.Header("工作项目",
            "总结的主要工作主题按这里的项目分组：项目名称与说明都会发给模型用于归类，"
            + "说明写得越具体，归类越准。"),
        null,
        _projects);

    private Control BuildMappingsCard() => Ui.CardWith(
        Ui.Header("飞书字段映射",
            "左侧是总结里的字段，右侧填对应的飞书表格列名，需与表头完全一致；留空表示该列不写入。"
            + "下方的自动匹配会读取表头列名后自动配对，核对无误再保存。"),
        null,
        _mappings);

    // ---------------------------------------------------------------- 保存

    private Task SaveAsync()
    {
        if (_services.SaveOptions())
        {
            _status.Text = "已保存　" + DateTime.Now.ToString("HH:mm:ss");
            _saved?.Invoke();
        }
        else
        {
            _status.Text = "保存失败：数据目录可能没有写权限。";
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- 工作项目

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

    // ---------------------------------------------------------------- 飞书字段映射

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

    /// <summary>
    /// 拉一次飞书表的列名，按关键词猜出对应关系，让用户确认而不是从零填。
    /// 只做一次只读请求，不写任何数据。
    /// </summary>
    private async Task AutoMatchFieldsAsync()
    {
        if (!Ui.IsFeishuReady(_services.Options))
        {
            await Ui.Info(this, "还差接入信息", "先把 App ID、App Secret、多维表格 token、数据表 ID 填好。");
            return;
        }

        if (_services.SaveOptions())
        {
            _status.Text = "已保存　" + DateTime.Now.ToString("HH:mm:ss");
            _saved?.Invoke();
        }

        _status.Text = "正在读取飞书表的列名…";

        try
        {
            var publisher = new FeishuBitablePublisher(_services.Options.Feishu, _services.Log);
            var (fields, error) = await publisher.FetchFieldsAsync(CancellationToken.None);

            if (fields is null)
            {
                _status.Text = "读取列名失败";
                await Ui.Info(this, "读取列名失败", error ?? "未知原因。");
                return;
            }

            var matched = FeishuFieldMatcher.Match(fields, _services.Options.Feishu.FieldMappings);
            _services.Options.Feishu.FieldMappings = matched;
            RenderMappings();

            var lines = matched.Select(m =>
                FeishuFieldMapping.DescribeField(m.RecordField)
                + " 写成 "
                + (m.FeishuField.Length > 0 ? m.FeishuField : "（没找到对应列，将跳过）"));

            _status.Text = "已匹配 " + matched.Count(m => m.FeishuField.Length > 0) + " 列，保存后生效";

            await Ui.Info(this, "已按列名匹配",
                "表里读到 " + fields.Count + " 列。"
                + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, lines)
                + Environment.NewLine + Environment.NewLine
                + "核对无误后保存。");
        }
        catch (Exception ex)
        {
            _services.Log.Error("自动匹配列名失败", ex);
            _status.Text = "自动匹配失败";
            await Ui.Info(this, "自动匹配失败", ex.Message);
        }
    }

    // ---------------------------------------------------------------- 排除的程序

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
                _excluded.Children.Add(Ui.Caption("暂无已记录的程序。"));
            }
        }
        catch (Exception ex)
        {
            _services.Log.Warn($"读取进程清单失败：{ex.Message}");
            _excluded.Children.Add(Ui.Caption("读取进程清单失败，此项可稍后重试。"));
        }
    }

    // ---------------------------------------------------------------- 小工具

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
}
