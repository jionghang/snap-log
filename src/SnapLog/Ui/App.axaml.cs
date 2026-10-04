using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Controls.ApplicationLifetimes;

namespace SnapLog.Ui;

/// <summary>
/// Avalonia 应用。真正的窗口与托盘由 <see cref="AppServices"/> 建好后交给这里——
/// 命令行模式（--selftest 等）根本不会走到这个类。
/// </summary>
public partial class App : Application
{
    /// <summary>托盘模式启动时由 Program 填进来；命令行/界面自检时为 null。</summary>
    internal static AppServices? Services { get; set; }

    // Application 没有编译器生成的 InitializeComponent（那套只给 Window/UserControl），
    // 这里仍是运行时加载；App 里也没有 x:Name 字段要赋值。
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 关掉"最后一个窗口关闭就退出"：这个程序的关闭按钮只是收起窗口，进程要留在托盘里。
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (Services is { } services)
            {
                services.AttachDesktop(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
