namespace SnapLog.Ui;

/// <summary>
/// 给控件打开双缓冲。
///
/// 设置页控件多、层级深，每次重排/重绘都会看到"整块重画一遍"的闪动。
/// DataGridView、Panel/TableLayoutPanel 的 DoubleBuffered 属性是受保护的，
/// 从这里访问不到，所以只能反射设置——这是 WinForms 里的通行做法，没有副作用。
/// </summary>
internal static class DoubleBuffer
{
    private static readonly System.Reflection.PropertyInfo? Property =
        typeof(Control).GetProperty(
            "DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    public static void Enable(Control? control)
    {
        if (control is null || Property is null)
        {
            return;
        }

        try
        {
            Property.SetValue(control, true);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Reflection.TargetInvocationException)
        {
            // 打不开就算了，只影响观感。
        }
    }
}
