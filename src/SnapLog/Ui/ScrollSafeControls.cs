namespace SnapLog.Ui;

/// <summary>
/// 设置页是一整页可滚动的内容，滚轮经过下拉框、数值框时不应该改动它们的值——
/// 那是"想滚页面却把设置改了"的典型误操作，而且改完不容易发现。
///
/// 这两个派生控件把滚轮一律交给所在的可滚动父容器：
/// 值只能用鼠标点击下拉、键盘方向键或直接输入来改。
/// </summary>
internal static class WheelGuard
{
    private const int ScrollStep = 48;

    /// <summary>把滚轮用来滚动最近的可滚动祖先，返回是否真的滚动了。</summary>
    public static bool ScrollAncestor(Control control, int delta)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not ScrollableControl { AutoScroll: true } scrollable)
            {
                continue;
            }

            var current = -scrollable.AutoScrollPosition.Y;
            var target = Math.Max(0, current - Math.Sign(delta) * ScrollStep);
            if (target == current)
            {
                return false;
            }

            scrollable.AutoScrollPosition = new Point(0, target);
            return true;
        }

        return false;
    }
}

/// <summary>滚轮不改选中项的下拉框。</summary>
internal sealed class ScrollSafeComboBox : ComboBox
{
    protected override void OnMouseWheel(MouseEventArgs e) => WheelGuard.ScrollAncestor(this, e.Delta);
}

/// <summary>滚轮不改数值的数值框。</summary>
internal sealed class ScrollSafeNumericUpDown : NumericUpDown
{
    protected override void OnMouseWheel(MouseEventArgs e) => WheelGuard.ScrollAncestor(this, e.Delta);
}
