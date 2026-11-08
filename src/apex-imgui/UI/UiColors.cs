using System.Drawing;
using System.Numerics;

/// <summary>
/// UI 颜色常量，ImGui 与 WinForms 两侧共用。
/// 全部取自 Office：字体颜色面板“标准颜色”（产品内置值）、
/// Office 默认主题、以及 Word WdColor 枚举（Microsoft Learn 官方文档）。
/// 状态文字色已按 ImGui 深色背景 #0F0F0F 校验，满足 WCAG 2.2 AA 正文 4.5:1。
/// </summary>
internal static class UiColors
{
    // 依赖状态文字（Office 标准色：绿 / 橙）
    public static readonly Vector4 StatusTextReady = Rgb(0x00B050);   // 6.69:1
    public static readonly Vector4 StatusTextPending = Rgb(0xFFC000); // 11.68:1

    // 吸附曲线预览（ImGui DrawList）
    public static readonly Vector4 SnapCurveLine = Rgb(0x00B0F0);       // Office 标准色「浅蓝」
    public static readonly Vector4 SnapCurvePoint = Rgb(0xED7D31);      // Office 主题 Accent 2
    public static readonly Vector4 SnapCurveBackground = Rgb(0x000000); // Office 主题「深色 1」

    // 武器预览窗口（WinForms GDI+）
    public static readonly Color WeaponPreviewBackground = ColorFromRgb(0x000000); // Office 主题「深色 1」
    public static readonly Color WeaponPreviewTargetBox = ColorFromRgb(0xFF0000);  // Office 标准色「红色」
    public static readonly Color PreviewStatusText = ColorFromRgb(0xC0C0C0);       // WdColor.wdColorGray25

    private static Vector4 Rgb(int rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f,
        1f);

    private static Color ColorFromRgb(int rgb) =>
        Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
