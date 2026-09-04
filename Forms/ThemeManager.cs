using System;
using System.Drawing;
using System.Windows.Forms;

namespace myrouter.Forms;

/// <summary>
/// 昼夜主题：按本地时间 18:00-06:00 为深色、其余为浅色，
/// 递归应用到整个窗体（含日志框/输入框等需要双色处理的控件）。
/// </summary>
public static class ThemeManager
{
    public static bool IsDark(DateTime now) => now.Hour is < 6 or >= 18;

    public static void Apply(Control root, bool dark)
    {
        var bg = dark ? Color.FromArgb(30, 31, 36) : Color.FromArgb(244, 246, 248);
        var fg = dark ? Color.FromArgb(226, 229, 234) : Color.FromArgb(26, 30, 36);
        var inputBg = dark ? Color.FromArgb(18, 19, 22) : Color.White;
        ApplyRecursive(root, bg, fg, inputBg);
    }

    private static void ApplyRecursive(Control c, Color bg, Color fg, Color inputBg)
    {
        c.BackColor = bg;
        c.ForeColor = fg;
        switch (c)
        {
            case TextBox tb:
                tb.BackColor = inputBg;
                break;
            case NumericUpDown n:
                n.BackColor = inputBg;
                break;
        }
        foreach (Control child in c.Controls)
            ApplyRecursive(child, bg, fg, inputBg);
    }
}