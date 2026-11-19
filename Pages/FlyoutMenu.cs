using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace xinread.Pages;

/// <summary>
/// ☰ 折叠菜单的公共构件：菜单项先收起浮层再执行操作，
/// 禁用项保留可读的灰色文字并说明原因。书架页与阅读页共用。
/// </summary>
internal static class FlyoutMenu
{
    public static Button BuildRow(
        Flyout flyout,
        string text,
        RoutedEventHandler? click,
        bool isEnabled,
        string description)
    {
        var button = new Button
        {
            Content = text,
            IsEnabled = isEnabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinWidth = 0,
        };

        // 禁用态的 Flyout 按钮文字会淡到几乎看不见，这里保持可读的灰色并说明原因。
        if (!isEnabled)
        {
            button.Foreground = DisabledTextBrush();
            AutomationProperties.SetName(button, $"{description}（当前不可用）");
        }
        else
        {
            AutomationProperties.SetName(button, description);
        }

        if (click is not null)
        {
            // 先收起菜单再执行原操作，避免菜单浮在内容上挡住操作结果。
            button.Click += (sender, e) =>
            {
                flyout.Hide();
                click(sender, e);
            };
        }

        return button;
    }

    /// <summary>禁用态文字画刷；资源缺失时退回半透明灰，避免取不到就抛异常。</summary>
    private static Brush DisabledTextBrush()
    {
        if (Application.Current?.Resources is { } resources
            && resources.TryGetValue("TextFillColorDisabledBrush", out object? value)
            && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(0x5C, 0x80, 0x80, 0x80));
    }
}
