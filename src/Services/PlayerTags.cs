using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>A team whose players are tagged automatically: its jersey colours and roster.</summary>
public class Team
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>How our jerseys look, in plain words ("white shirts with blue numbers"). The model only reads numbers on these.</summary>
    public string Colors { get; set; } = "";
    /// <summary>Tag photos with the player's name ("Emma 10") instead of the team and number ("Hurricanes 10"). Off by default: names of children stay off the site unless chosen.</summary>
    public bool TagWithNames { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<Player> Players { get; set; } = new();
}

public class Player
{
    public int Id { get; set; }
    public int TeamId { get; set; }
    public Team? Team { get; set; }
    /// <summary>1 to 3 digits, as on the jersey.</summary>
    public string Number { get; set; } = "";
    /// <summary>Optional. Used in tags only when the team's TagWithNames is on.</summary>
    public string Name { get; set; } = "";
}

/// <summary>A player a model thinks is in a photo. Waits for the admin unless it was approved automatically.</summary>
public class PlayerSuggestion
{
    public int Id { get; set; }
    public int PhotoId { get; set; }
    public int PlayerId { get; set; }
    public int Confidence { get; set; }
    /// <summary>Which provider and model read it, such as "ollama qwen3-vl:8b".</summary>
    public string Source { get; set; } = "";
    /// <summary>pending, approved or rejected. A rejected suggestion is kept so checking the photo again does not bring it back.</summary>
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
}

public static class PlayerTags
{
    public const int MaxAttempts = 3;
    /// <summary>The picture sent to a model: big enough to read a number on a jersey, small enough for every provider's limits.</summary>
    public const int ImageSize = 1568;

    public static string TagName(Team t, Player p) =>
        Tags.Clean(t.TagWithNames && p.Name.Length > 0 ? $"{p.Name} {p.Number}" : $"{t.Name} {p.Number}");

    /// <summary>Parses a roster typed one player per line: "10 Emma", "10 - Emma", "#10", or just "10".</summary>
    public static List<(string Number, string Name)> ParseRoster(string? text)
    {
        var list = new List<(string, string)>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(raw.Trim(), @"^#?\s*([0-9]{1,3})\b[\s\-:.,]*(.*)$");
            if (!m.Success) continue;
            var number = m.Groups[1].Value.TrimStart('0') is { Length: > 0 } n ? n : "0";
            var name = m.Groups[2].Value.Trim();
            if (name.Length > 30) name = name[..30];
            if (list.Any(x => x.Item1 == number)) continue;
            list.Add((number, name));
            if (list.Count >= 60) break;
        }
        return list;
    }

    static string Norm(string number) => number.TrimStart('0') is { Length: > 0 } n ? n : "0";

    /// <summary>
    /// Checks one photo: asks each set-up provider in order until one answers (no fallback when only one is set up), then
    /// turns the numbers on our roster into suggestions. Numbers not on the roster are ignored.
    /// Returns how many new suggestions were made, or throws when every provider failed.
    /// </summary>
    public static async Task<int> CheckPhotoAsync(AppDb db, ImageService images, IVisionReader reader, VisionSettings vision, Photo photo, Team team, ILogger log, CancellationToken ct)
    {
        var chain = vision.Chain;
        if (chain.Count == 0) throw new VisionException("No provider is set up. Open Settings, Automatic tagging.");
        var players = team.Players.ToDictionary(p => Norm(p.Number));
        var v = await images.GetVariantAsync(photo, "ai", ImageSize, "jpg", false) ?? throw new VisionException("The photo file could not be read");
        var jpeg = await File.ReadAllBytesAsync(v.path, ct);
        var prompt = PlayerPrompt.Build(team.Colors, players.Keys);

        VisionAnswer? answer = null;
        var errors = new List<string>();
        foreach (var provider in chain)
        {
            try { answer = await reader.AskAsync(provider, jpeg, prompt, ct); break; }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                errors.Add($"{VisionProvider.Label(provider.Kind)}: {ex.Message}");
                log.LogWarning("Player check of photo {Id} with {Provider} failed: {Error}", photo.Id, provider.Kind, ex.Message);
            }
        }
        photo.PlayerCheckAttempts++;
        if (answer == null) throw new VisionException(string.Join(" / ", errors));

        photo.PlayersCheckedAt = DateTime.UtcNow;
        var known = (await db.PlayerSuggestions.Where(s => s.PhotoId == photo.Id).Select(s => s.PlayerId).ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var seen in answer.Numbers)
        {
            if (!players.TryGetValue(Norm(seen.Number), out var player) || known.Contains(player.Id)) continue;
            var auto = vision.AutoApproveAt > 0 && seen.Confidence >= vision.AutoApproveAt;
            var s = new PlayerSuggestion { PhotoId = photo.Id, PlayerId = player.Id, Confidence = seen.Confidence, Source = $"{answer.Provider} {answer.Model}", Status = "pending" };
            db.PlayerSuggestions.Add(s);
            known.Add(player.Id);
            added++;
            if (auto) await ApproveAsync(db, s, team, player);
        }
        return added;
    }

    /// <summary>Puts the player's tag on the photo and marks the suggestion approved.</summary>
    public static async Task ApproveAsync(AppDb db, PlayerSuggestion s, Team team, Player player)
    {
        var name = TagName(team, player);
        var tag = db.Tags.Local.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                  ?? await db.Tags.FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower());
        if (tag == null) { tag = new Tag { Name = name }; db.Tags.Add(tag); await db.SaveChangesAsync(); }
        if (!await db.PhotoTags.AnyAsync(pt => pt.PhotoId == s.PhotoId && pt.TagId == tag.Id) && !db.PhotoTags.Local.Any(pt => pt.PhotoId == s.PhotoId && pt.TagId == tag.Id))
            db.PhotoTags.Add(new PhotoTag { PhotoId = s.PhotoId, TagId = tag.Id });
        s.Status = "approved";
        s.ReviewedAt = DateTime.UtcNow;
    }
}

/// <summary>Where a gallery's player check stands, for the progress line on its settings page.</summary>
public class PlayerJobStatus
{
    public bool Running { get; set; }
    public int Total { get; set; }
    public int Done { get; set; }
    public int Found { get; set; }
    public int Failed { get; set; }
    public string LastError { get; set; } = "";
    public DateTime? FinishedAt { get; set; }
}

/// <summary>
/// Runs player checks in the background, one gallery at a time and one photo at a time, so a slow model never piles up
/// requests. Nothing runs until the admin presses a button; there is no automatic pass over the whole library.
/// </summary>
public class PlayerTagJobs(IServiceScopeFactory scopes, ILogger<PlayerTagJobs> log) : BackgroundService
{
    record Job(int GalleryId, int? Limit, bool Recheck);
    readonly Channel<Job> queue = Channel.CreateUnbounded<Job>();
    readonly ConcurrentDictionary<int, PlayerJobStatus> status = new();

    public PlayerJobStatus Status(int galleryId) => status.TryGetValue(galleryId, out var s) ? s : new PlayerJobStatus();

    /// <summary>Queues a check. False when this gallery already has one running or waiting.</summary>
    public bool Start(int galleryId, int? limit, bool recheck)
    {
        if (status.TryGetValue(galleryId, out var s) && s.Running) return false;
        status[galleryId] = new PlayerJobStatus { Running = true };
        queue.Writer.TryWrite(new Job(galleryId, limit, recheck));
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(ct))
        {
            var st = status[job.GalleryId];
            try { await RunAsync(job, st, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                st.LastError = ex.Message;
                log.LogWarning(ex, "Player check of gallery {Id} stopped", job.GalleryId);
            }
            finally { st.Running = false; st.FinishedAt = DateTime.UtcNow; }
        }
    }

    async Task RunAsync(Job job, PlayerJobStatus st, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var images = scope.ServiceProvider.GetRequiredService<ImageService>();
        var reader = scope.ServiceProvider.GetRequiredService<IVisionReader>();
        var vision = scope.ServiceProvider.GetRequiredService<SettingsService>().Vision;
        var g = await db.Galleries.FirstOrDefaultAsync(x => x.Id == job.GalleryId, ct);
        if (g?.TeamId == null) { st.LastError = "Choose the team for this gallery first."; return; }
        var team = await db.Teams.Include(t => t.Players).FirstOrDefaultAsync(t => t.Id == g.TeamId, ct);
        if (team == null || team.Players.Count == 0) { st.LastError = "Add the team's players (their numbers) first."; return; }

        var hiddenCover = GalleryRules.HiddenCoverId(g);
        var q = db.Photos.Where(p => p.GalleryId == g.Id && p.Approved && p.Id != hiddenCover);
        if (!job.Recheck) q = q.Where(p => p.PlayersCheckedAt == null && p.PlayerCheckAttempts < PlayerTags.MaxAttempts);
        var photos = GalleryRules.Sort(await q.ToListAsync(ct), g, x => x.TakenAt, x => x.Name, x => x.AddedAt);
        if (job.Limit is { } n) photos = photos.Take(n).ToList();
        st.Total = photos.Count;
        var inARow = 0;
        foreach (var p in photos)
        {
            ct.ThrowIfCancellationRequested();
            try { st.Found += await PlayerTags.CheckPhotoAsync(db, images, reader, vision, p, team, log, ct); inARow = 0; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                st.Failed++;
                inARow++;
                st.LastError = ex.Message;
            }
            await db.SaveChangesAsync(ct);
            st.Done++;
            // every provider failing on three photos in a row means it is down or misconfigured: stop instead of hammering it
            if (inARow >= 3) { st.LastError = "Stopped after three failures in a row: " + st.LastError; break; }
        }
    }
}
