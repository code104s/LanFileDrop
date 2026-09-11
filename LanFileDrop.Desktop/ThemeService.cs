using System.Windows;
using System.Windows.Media;
using System.Text.Json;
using Microsoft.Win32;

namespace LanFileDrop.Desktop;

public static class ThemeService
{
    private static readonly object SettingsLock = new();
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanFileDrop", "settings.json");

    public static bool GetInitialDarkMode()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return IsSystemDark();
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath));
            return settings?.IsDark ?? IsSystemDark();
        }
        catch { return IsSystemDark(); }
    }

    public static void SaveDarkMode(bool dark)
    {
        lock (SettingsLock)
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new UserSettings(dark),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, true);
        }
        catch { }
    }

    public static bool IsSystemDark()
    {
        try
        {
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int number && number == 0;
        }
        catch { return false; }
    }

    public static void Apply(bool dark)
    {
        var resources = Application.Current.Resources;
        resources["WindowBrush"] = Brush(dark ? "#111318" : "#F4F6FA");
        resources["SurfaceBrush"] = Brush(dark ? "#1B1E25" : "#FFFFFF");
        resources["SurfaceAltBrush"] = Brush(dark ? "#242833" : "#F0F3F8");
        resources["TextBrush"] = Brush(dark ? "#F4F6FB" : "#172033");
        resources["MutedBrush"] = Brush(dark ? "#9BA5B8" : "#667085");
        resources["BorderBrush"] = Brush(dark ? "#343A49" : "#DCE2EC");
    }

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));

    private sealed record UserSettings(bool? IsDark);
}
