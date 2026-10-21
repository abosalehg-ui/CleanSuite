using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CleanSuite.App.Localization;
using CleanSuite.App.Theming;

namespace CleanSuite.App;

/// <summary>CleanSuite's own UI preferences in %LocalAppData%\CleanSuite\ui.json (never the original program's settings).</summary>
public sealed class UiSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public UiLanguage Language { get; set; } = UiLanguage.Arabic;
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanSuite", "ui.json");

    public static UiSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
