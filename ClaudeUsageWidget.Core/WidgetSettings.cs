using System.Text.Json;

namespace ClaudeUsageWidget.Core;

public sealed class WidgetSettings
{
    public int RefreshSeconds { get; set; } = 30;
    public double Opacity { get; set; } = 0.94;
    public bool AlwaysOnTop { get; set; } = true;
    public bool AutoConnect { get; set; }
    public bool ConnectionDisabled { get; set; }
    public string? BrowserKind { get; set; }
    public bool ManualMode { get; set; }
    public double ManualFiveHour { get; set; }
    public double ManualFable { get; set; }
    public DateTimeOffset? ManualReset { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }

    public void Normalize()
    {
        RefreshSeconds = Math.Clamp(RefreshSeconds, 10, 86400);
        if (BrowserKind is not (null or "Chrome" or "Edge")) BrowserKind = null;
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.2, 1) : 0.94;
        ManualFiveHour = double.IsFinite(ManualFiveHour) ? Math.Clamp(ManualFiveHour, 0, 100) : 0;
        ManualFable = double.IsFinite(ManualFable) ? Math.Clamp(ManualFable, 0, 100) : 0;
        if (Left.HasValue && !double.IsFinite(Left.Value)) Left = null;
        if (Top.HasValue && !double.IsFinite(Top.Value)) Top = null;
    }

    public WidgetSettings Copy() => (WidgetSettings)MemberwiseClone();
}

public static class SettingsStore
{
    public static WidgetSettings Load(string path)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(path)) ?? new();
            settings.Normalize();
            return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public static void Save(string path, WidgetSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
