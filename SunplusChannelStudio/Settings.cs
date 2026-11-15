using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SunplusChannelStudio;

/// <summary>Small user preferences (language, category order) in settings.json next to the error log.</summary>
public static class Settings
{
    static string FilePath => Path.Combine(Path.GetDirectoryName(App.ErrorLog)!, "settings.json");

    static JsonObject Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject o) return o;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return [];
    }

    public static string? GetString(string key) => Load()[key]?.GetValue<string>();

    public static string[]? GetList(string key) =>
        Load()[key] is JsonArray a ? a.Select(n => n?.GetValue<string>()).OfType<string>().ToArray() : null;

    public static void Set(string key, JsonNode? value)
    {
        var o = Load();
        o[key] = value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }
}
