using SnapLog.Configuration;
using SnapLog.Core;
using SnapLog.Diagnostics;
using SnapLog.Storage;

namespace SnapLog.Ui;

/// <summary>设置面板需要的运行时依赖。用一个上下文对象传，避免构造函数堆一长串参数。</summary>
internal sealed record SettingsContext(
    AppOptions Options,
    AppPaths Paths,
    FileLogger Log,
    IActivityRepository Store,
    SummaryRunner SummaryRunner,
    Func<bool> OnSave);
