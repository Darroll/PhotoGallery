using System.Collections.Concurrent;

namespace PhotoGallery;

/// <summary>
/// YouTube preview images, fetched by the server and cached in DATA_DIR/cache/vthumb so visitors never contact
/// YouTube until they press play. A cached image is re-fetched when it is older than <see cref="MaxAge"/>, or on
/// demand from the admin page, so a thumbnail changed on YouTube shows up here too.
/// </summary>
public static class VideoThumbs
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);
    static readonly ConcurrentDictionary<string, DateTime> lastTry = new();

    /// <summary>Downloads the current image for a video id; null when YouTube cannot be reached. Replaceable in tests.</summary>
    public static Func<string, Task<byte[]?>> Fetch = async vid =>
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            return await http.GetByteArrayAsync($"https://i.ytimg.com/vi/{vid}/hqdefault.jpg");
        }
        catch { return null; }
    };

    public static string PathFor(AppConfig cfg, string vid) => Path.Combine(cfg.DataDir, "cache", "vthumb", vid + ".jpg");

    /// <summary>Fetches the image again and replaces the cached one. The old image stays if the fetch fails.</summary>
    public static async Task<bool> RefreshAsync(AppConfig cfg, string vid)
    {
        lastTry[vid] = DateTime.UtcNow;
        var bytes = await Fetch(vid);
        if (bytes == null || bytes.Length == 0) return false;
        var file = PathFor(cfg, vid);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes);
        File.Move(tmp, file, overwrite: true);
        return true;
    }

    /// <summary>True when the cached image is old enough to check again and we have not just tried.</summary>
    public static bool NeedsRefresh(string file) =>
        DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > MaxAge
        && (!lastTry.TryGetValue(Path.GetFileNameWithoutExtension(file), out var t) || DateTime.UtcNow - t > RetryAfter);
}
