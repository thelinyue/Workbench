using System.Windows;
using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.App;

/// <summary>
/// 统一管理 WPF 主题资源字典的加载和替换。
/// 主题字典使用相同的资源键，页面只依赖语义化 Token，因此切换时无需重建窗口。
/// </summary>
internal static class ThemeManager
{
    public static string? ApplyTheme(string? theme)
    {
        var normalized = string.Equals(theme, AppSettingsConfig.DarkTheme, StringComparison.OrdinalIgnoreCase)
            ? AppSettingsConfig.DarkTheme
            : AppSettingsConfig.LightTheme;
        var assemblyName = typeof(ThemeManager).Assembly.GetName().Name;
        var source = new Uri($"/{assemblyName};component/Themes/{normalized}Theme.xaml", UriKind.Relative);

        try
        {
            var dictionary = new ResourceDictionary { Source = source };
            var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
            var currentIndex = dictionaries
                .Select((item, index) => new { item, index })
                .FirstOrDefault(x => IsThemeDictionary(x.item))?.index;

            if (currentIndex is int index)
            {
                dictionaries[index] = dictionary;
            }
            else
            {
                dictionaries.Insert(0, dictionary);
            }

            (System.Windows.Application.Current.Resources["StatusBrushConverter"] as StatusBrushConverter)?.Refresh();
            return null;
        }
        catch (Exception ex)
        {
            return $"无法加载{normalized}主题资源：{ex.Message}";
        }
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
        => dictionary.Source?.OriginalString.Contains("Themes/", StringComparison.OrdinalIgnoreCase) == true;
}
