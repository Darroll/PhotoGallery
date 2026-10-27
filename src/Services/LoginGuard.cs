using System.Collections.Concurrent;

namespace PhotoGallery;

/// <summary>Slows down repeated wrong admin passwords and tells the admin by email when guessing is going on.</summary>
public static class LoginGuard
{
    static readonly ConcurrentDictionary<string, (int count, DateTime last)> perIp = new();
    static readonly object gate = new();
    static readonly List<DateTime> recent = new();
    static DateTime lastAlert = DateTime.MinValue;

    /// <summary>Waits longer after each wrong password from the same address (1 s, 2 s ... up to 8 s) and returns the recent failure count.</summary>
    public static async Task<int> FailedAsync(string ip)
    {
        var now = DateTime.UtcNow;
        var entry = perIp.AddOrUpdate(ip, _ => (1, now), (_, e) => (now - e.last > TimeSpan.FromMinutes(15) ? 1 : e.count + 1, now));
        if (perIp.Count > 5000) foreach (var k in perIp.Where(x => now - x.Value.last > TimeSpan.FromMinutes(15)).Select(x => x.Key).ToList()) perIp.TryRemove(k, out _);
        int total;
        lock (gate) { recent.RemoveAll(t => now - t > TimeSpan.FromMinutes(15)); recent.Add(now); total = recent.Count; }
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(entry.count, 8)));
        return total;
    }

    public static void Succeeded(string ip) => perIp.TryRemove(ip, out _);

    /// <summary>True at most once an hour once five wrong passwords were seen in 15 minutes.</summary>
    public static bool ShouldAlert(int recentFailures)
    {
        lock (gate)
        {
            if (recentFailures < 5 || DateTime.UtcNow - lastAlert < TimeSpan.FromHours(1)) return false;
            lastAlert = DateTime.UtcNow;
            return true;
        }
    }
}
