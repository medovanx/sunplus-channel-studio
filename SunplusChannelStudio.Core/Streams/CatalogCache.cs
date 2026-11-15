namespace SunplusChannelStudio.Core.Streams;

/// <summary>Downloads the iptv-org files at most once a day and keeps them in a local folder.</summary>
public sealed class CatalogCache(string folder, HttpClient http)
{
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    /// <summary>The catalogue from the local copy only, whatever its age; null when it was never downloaded.</summary>
    public static StreamCatalog? TryLoadCached(string folder)
    {
        string Read(string f) => File.ReadAllText(Path.Combine(folder, f));
        try
        {
            if (!File.Exists(Path.Combine(folder, "channels.json")) || !File.Exists(Path.Combine(folder, "streams.json"))) return null;
            var blocklist = File.Exists(Path.Combine(folder, "blocklist.json")) ? Read("blocklist.json") : null;
            return StreamCatalog.Parse(Read("channels.json"), Read("streams.json"), blocklist);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return null; }
    }

    /// <param name="progress">Told the name of each file as it starts downloading.</param>
    public async Task<StreamCatalog> LoadAsync(IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(folder);
        var channels = await GetAsync("channels.json", StreamCatalog.ChannelsUrl, progress, cancel);
        var streams = await GetAsync("streams.json", StreamCatalog.StreamsUrl, progress, cancel);
        var blocklist = await GetAsync("blocklist.json", StreamCatalog.BlocklistUrl, progress, cancel);
        return StreamCatalog.Parse(channels, streams, blocklist);
    }

    /// <summary>Fresh copy from disk, else download; a stale copy is still used when the download fails.</summary>
    async Task<string> GetAsync(string file, string url, IProgress<string>? progress, CancellationToken cancel)
    {
        var path = Path.Combine(folder, file);
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MaxAge)
            return await File.ReadAllTextAsync(path, cancel);
        try
        {
            progress?.Report(file);
            var text = await http.GetStringAsync(url, cancel);
            await File.WriteAllTextAsync(path + ".tmp", text, cancel);
            File.Move(path + ".tmp", path, overwrite: true);
            return text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, cancel);
        }
    }
}
