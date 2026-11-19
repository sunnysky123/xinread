using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace xinread.Rendering;

/// <summary>
/// 正文里需要跟随主题的配色角色。正文控件是在 C# 里动态创建的，用不了 <c>{ThemeResource}</c>
/// 标记扩展，所以渲染时只能取到「当前主题」的画刷实例；一旦系统切换主题，这些实例就变成旧主题的
/// 颜色。因此这里按角色保存，主题变化时再重新解析一次。
/// </summary>
internal enum ReaderBrushRole
{
    /// <summary>引用块的底色。</summary>
    QuoteBackground,

    /// <summary>引用块左侧竖线。</summary>
    QuoteRule,

    /// <summary>代码块底色。</summary>
    CodeBackground,

    /// <summary>分隔线。</summary>
    Divider,

    /// <summary>次要文字（引用块正文）。</summary>
    SecondaryText,

    /// <summary>朗读中的段落高亮。</summary>
    Highlight,
}

/// <summary>按当前主题解析正文画刷，见 <see cref="ReaderBrushRole"/> 里的说明。</summary>
internal static class ReaderTheme
{
    private static readonly Lazy<AccessibilitySettings?> Accessibility =
        new(CreateAccessibilitySettings, isThreadSafe: true);

    /// <summary>取当前主题下该角色对应的画刷；资源缺失时退回透明，避免整章渲染失败。</summary>
    /// <param name="role">需要的配色角色。</param>
    /// <param name="theme">
    /// 调用方已知的实际主题。主题切换事件触发时 <see cref="Application.RequestedTheme"/> 未必已经更新，
    /// 由页面把自己的 <c>ActualTheme</c> 传进来最可靠；传 null 时退回应用级设置。
    /// </param>
    public static Brush Resolve(ReaderBrushRole role, ElementTheme? theme = null)
    {
        string key = KeyOf(role);

        if (TryFromThemeDictionary(CurrentThemeKey(theme), key) is { } brush)
        {
            return brush;
        }

        // 自定义字典没命中时再试一次应用级查找，兼容被别处覆盖或补上的同名键。
        if (Application.Current?.Resources is { } resources
            && resources.TryGetValue(key, out object? value)
            && value is Brush fallback)
        {
            return fallback;
        }

        return TransparentBrush();
    }

    /// <summary>当前生效的主题字典键：高对比度优先，其次用调用方给的主题或应用（即系统）的明暗设置。</summary>
    public static string CurrentThemeKey(ElementTheme? theme = null)
    {
        if (Accessibility.Value?.HighContrast == true)
        {
            return "HighContrast";
        }

        // Default 表示「跟随应用」，不能当成浅色，否则会把未知情况误判成 Light。
        bool dark = theme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Application.Current?.RequestedTheme == ApplicationTheme.Dark,
        };

        return dark ? "Dark" : "Light";
    }

    private static Brush? TryFromThemeDictionary(string themeKey, string resourceKey)
    {
        if (Application.Current?.Resources?.ThemeDictionaries is not { } dictionaries)
        {
            return null;
        }

        if (dictionaries.TryGetValue(themeKey, out object? dictionary)
            && dictionary is ResourceDictionary themeResources
            && themeResources.TryGetValue(resourceKey, out object? value)
            && value is Brush brush)
        {
            return brush;
        }

        return null;
    }

    private static string KeyOf(ReaderBrushRole role) => role switch
    {
        ReaderBrushRole.QuoteBackground => "ReaderQuoteBackgroundBrush",
        ReaderBrushRole.QuoteRule => "ReaderQuoteBorderBrush",
        ReaderBrushRole.CodeBackground => "ReaderCodeBackgroundBrush",
        ReaderBrushRole.Divider => "ReaderDividerBrush",
        ReaderBrushRole.SecondaryText => "ReaderSecondaryTextBrush",
        ReaderBrushRole.Highlight => "ReaderHighlightBrush",
        _ => string.Empty,
    };

    /// <summary>高对比度查询偶尔会在没有桌面会话的环境里失败，失败就按普通主题处理。</summary>
    private static AccessibilitySettings? CreateAccessibilitySettings()
    {
        try
        {
            return new AccessibilitySettings();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static Brush TransparentBrush() => new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}
