namespace SunplusChannelStudio.Core.Streams;

/// <summary>Downloads the iptv-org files at most once a day and keeps them in a local folder.</summary>
public sealed class CatalogCache(string folder, HttpClient http)
{
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    public async Task<StreamCatalog> LoadAsync(CancellationToken cancel = default)
    {
        Directory.CreateDirectory(folder);
        var channels = await GetAsync("channels.json", StreamCatalog.ChannelsUrl, cancel);
        var streams = await GetAsync("streams.json", StreamCatalog.StreamsUrl, cancel);
        var blocklist = await GetAsync("blocklist.json", StreamCatalog.BlocklistUrl, cancel);
        return StreamCatalog.Parse(channels, streams, blocklist);
    }

    /// <summary>Fresh copy from disk, else download; a stale copy is still used when the download fails.</summary>
    async Task<string> GetAsync(string file, string url, CancellationToken cancel)
    {
        var path = Path.Combine(folder, file);
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MaxAge)
            return await File.ReadAllTextAsync(path, cancel);
        try
        {
            var text = await http.GetStringAsync(url, cancel);
            await File.WriteAllTextAsync(path + ".tmp", text, cancel);
            File.Move(path + ".tmp", path, overwrite: true);
            return text;
        }
        catch (HttpRequestException) when (File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, cancel);
        }
    }
}
