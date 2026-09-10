using System.Text.Json;
using Avalonia;
using Avalonia.Styling;
using Material.Styles.Themes;
using Material.Styles.Themes.Base;

namespace FetchIt.Services;

public static class ThemeStore
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WasdFetchIt",
        "theme.json");

    public static bool IsDark()
    {
        try
        {
            if (!File.Exists(FilePath))
                return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            return !doc.RootElement.TryGetProperty("dark", out var dark) || dark.GetBoolean();
        }
        catch
        {
            return true;
        }
    }

    public static void Save(bool dark)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, dark ? """{"dark":true}""" : """{"dark":false}""");
    }

    public static void Apply(Application app, bool dark)
    {
        var material = app.LocateMaterialTheme<MaterialThemeBase>();
        if (material is not null)
            material.BaseTheme = dark ? BaseThemeMode.Dark : BaseThemeMode.Light;
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    public static void ApplySaved(Application app) => Apply(app, IsDark());

    public static bool Toggle(Application app)
    {
        var dark = !IsDark();
        Save(dark);
        Apply(app, dark);
        return dark;
    }
}
