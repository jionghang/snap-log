using SnapLog.Diagnostics;

namespace SnapLog.Ui;

/// <summary>
/// 跨模块可用的托盘气泡。它只是一个"怎么安全地弹提示"的工具，不持有任何业务状态。
///
/// 设计上故意不依赖具体的 TrayApplicationContext：由它在构造时把气泡函数注册进来，
/// 别的模块只管调用 TryShow，谁注册了就用谁的图标弹，没人注册就什么都不弹。
/// </summary>
internal static class TrayBalloon
{
    private static readonly object Gate = new();
    private static Action<string, ToolTipIcon>? _show;

    /// <summary>由托盘宿主在启动时注册。重复注册会覆盖之前的。</summary>
    public static void Register(Action<string, ToolTipIcon> show)
    {
        lock (Gate)
        {
            _show = show;
        }
    }

    public static void TryShow(string message, ToolTipIcon icon)
    {
        try
        {
            Action<string, ToolTipIcon>? show;
            lock (Gate)
            {
                show = _show;
            }

            show?.Invoke(message, icon);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 弹提示失败不值得再说一遍，直接吞掉。
        }
    }
}
