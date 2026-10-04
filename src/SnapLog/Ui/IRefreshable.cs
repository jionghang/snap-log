namespace SnapLog.Ui;

/// <summary>页面被切到前台时刷新自己。主窗口只在真正显示时才调用，避免无谓的数据库查询。</summary>
internal interface IRefreshable
{
    void Refresh();
}
