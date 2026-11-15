using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace SunplusChannelStudio;

/// <summary>A UI language: its strings live in Strings/{Code}.xaml and its flag in Assets/Languages/{Code}.png.</summary>
public sealed record UiLanguage(string Code, string NativeName, bool RightToLeft = false)
{
    public Uri Icon => new($"pack://application:,,,/Assets/Languages/{Code}.png");
}

/// <summary>
/// App language. XAML reads strings with DynamicResource, so switching swaps the dictionary and every
/// open window updates at once.
/// </summary>
public static class Loc
{
    /// <summary>Every language the app ships. Adding one needs a row here, a strings file and a flag.</summary>
    public static readonly UiLanguage[] Languages =
    [
        new("en", "English"),
        new("ar", "العربية", RightToLeft: true),
    ];

    static ResourceDictionary? _strings;

    public static UiLanguage Current { get; private set; } = Languages[0];
    public static string Language => Current.Code;
    public static bool IsRightToLeft => Current.RightToLeft;
    public static FlowDirection Flow => IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>Raised after the language changed, for text built in code.</summary>
    public static event Action? Changed;

    /// <summary>Saved choice first, then the Windows display language.</summary>
    public static void Init()
    {
        // Docs screenshots pick a language without touching the user's saved choice.
        if (Find(Environment.GetEnvironmentVariable("SCS_LANG")) is not null)
        {
            Apply(Environment.GetEnvironmentVariable("SCS_LANG")!, save: false);
            return;
        }
        string? saved = Settings.GetString("language");
        Apply(saved ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, save: false);
    }

    public static void Apply(string language, bool save = true)
    {
        Current = Find(language) ?? Languages[0];
        var next = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Strings/{Language}.xaml") };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_strings is not null) merged.Remove(_strings);
        merged.Add(next);
        _strings = next;

        if (save) Settings.Set("language", Language);
        Changed?.Invoke();
    }

    static UiLanguage? Find(string? code) => Languages.FirstOrDefault(l => l.Code == code);

    public static string Get(string key) => Application.Current.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>"Name" in quotes for one channel, "3 channels" for several.</summary>
    public static string Channels(IReadOnlyList<ChannelRow> rows) =>
        rows.Count == 1 ? Format("Phrase.Quoted", rows[0].Name) : Format("Phrase.Channels", rows.Count);

    public static MessageBoxOptions BoxOptions => IsRightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
}
