using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// Scene descriptions for search ("Emma (#10) heads the ball in the rain"). The first ready vision provider writes one per
/// photo. It is told which of our team's players are tagged on the photo and may name only them, by the label parents
/// already see (the number, or name and number when the team uses names in tags). Other players in our colours are "a
/// teammate" and opponents are described only in general words, never by number or name.
/// The players it was told about are kept with the description. When the photo's players change (a player identified, a
/// number corrected, a tag removed) the model's description is written again. A description you wrote is never replaced.
/// </summary>
public static class PhotoDescriptions
{
    public const int MaxLength = 400;
    /// <summary>Failed tries per photo before it waits for you (clearing the description, or a successful write, starts again).</summary>
    public const int MaxAttempts = 3;
    public const string Ai = "ai";
    public const string Admin = "admin";

    /// <summary>How a player is named in a description: "Emma (#10)" when the team uses names in tags, otherwise "#10".</summary>
    public static string Label(Team t, Player p) => t.TagWithNames && p.Name.Trim().Length > 0 ? $"{p.Name.Trim()} (#{p.Number})" : $"#{p.Number}";

    /// <summary>The team's players carrying a tag among these names, in jersey order.</summary>
    public static List<Player> PlayersOn(Team team, IEnumerable<string> tagNames)
    {
        var set = new HashSet<string>(tagNames, StringComparer.OrdinalIgnoreCase);
        return team.Players.Where(p => PlayerTags.TagNames(team, p).Any(set.Contains))
            .OrderBy(p => int.TryParse(p.Number, out var n) ? n : int.MaxValue).ThenBy(p => p.Number.Length).ToList();
    }

    /// <summary>What the model is told about players: the team and the labels. A change means the description is out of date.</summary>
    public static string Signature(Team? team, IEnumerable<string> labels) => team == null ? "" : $"t{team.Id}:" + string.Join("|", labels);

    public static string Prompt(bool hasTeam, string colors, IReadOnlyList<string> labels)
    {
        var what = "Describe the scene in one to three sentences, at most 60 words, so parents can find this photo by searching: what is happening " +
                   "(for example a header, a shot, a save, a tackle, a throw-in, a corner kick, a goal celebration, a team huddle), the weather and light " +
                   "(rain, snow, sun, wind, evening, under lights) and the setting. ";
        var json = "Reply only with JSON: {\"description\": \"...\"}.";
        if (!hasTeam)
            return "This photo is from a photo gallery. " + what +
                   "Do not name or guess who anyone is, do not read out jersey numbers, and do not guess where the photo was taken. " + json;
        var wear = string.IsNullOrWhiteSpace(colors) ? "" : "Our team wears: " + colors.Trim() + ". ";
        var ours = labels.Count == 0 ? ""
            : "These players from our team are in the photo: " + string.Join(", ", labels) + ". Refer to them only by exactly these labels " +
              $"(for example \"{labels[0]} heads the ball\"), and only to the ones you can see. ";
        return "This is a photo from one of our team's youth sports games. " + wear + what + ours +
               "Call any other player in our team's jersey \"a teammate\". Players in other colors are opponents: mention them only in general words, " +
               "such as \"an opponent\" or \"a defender in red\", never by number or name. " +
               "Do not read out any jersey number yourself, do not name or guess who anyone is, and do not guess where the photo was taken. " + json;
    }

    /// <summary>Reads the model's reply: the description from its JSON, or the reply itself when it answered in plain words.</summary>
    public static string Parse(string reply)
    {
        var text = reply.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                    return Clean(d.GetString());
                throw new VisionException("The answer had no description in it");
            }
            catch (JsonException) { throw new VisionException("The answer was not valid JSON"); }
        }
        if (text.Length == 0) throw new VisionException("The answer was empty");
        return Clean(text);
    }

    /// <summary>One line, at most <see cref="MaxLength"/> characters, cut at a word.</summary>
    public static string Clean(string? s)
    {
        s = Regex.Replace((s ?? "").Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim();
        if (s.Length <= MaxLength) return s;
        var cut = s.LastIndexOf(' ', MaxLength);
        return (cut > MaxLength / 2 ? s[..cut] : s[..MaxLength]).TrimEnd(',', ';', ' ');
    }

    static readonly Regex NumberRef = new(@"\s*(?:#\s?|\bno\.\s?|\bnumber\s)(\d{1,3})\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Takes out jersey numbers that are not one of the labels given, in case the model read an opponent's number anyway:
    /// "an opponent #12 slides in" becomes "an opponent slides in".
    /// </summary>
    public static string Scrub(string text, IEnumerable<string> allowedNumbers)
    {
        var allowed = allowedNumbers.ToHashSet();
        var s = NumberRef.Replace(text, m => allowed.Contains(m.Groups[1].Value) ? m.Value : "");
        s = Regex.Replace(s, @"\s+([,.;:!?])", "$1");
        return Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    /// <summary>You wrote (or cleared) the description. Cleared hands it back to the model, which writes a new one.</summary>
    public static void SetByHand(Photo p, string? text)
    {
        p.Description = Clean(text);
        p.DescriptionSource = p.Description.Length > 0 ? Admin : "";
        p.DescriptionPlayers = "";
        p.DescriptionAttempts = 0;
        p.DescribedAt = p.Description.Length > 0 ? DateTime.UtcNow : null;
    }

    /// <summary>A photo that needs a description written, with what the model is told.</summary>
    public record Todo(int PhotoId, Team? Team, string Colors, List<string> Labels, List<string> Numbers, string Signature, bool Stale);

    /// <summary>
    /// The photos waiting for a description, retagged ones first, then the newest. Waits on photos a player check will
    /// soon look at (so it is written once, with the players), and on galleries whose check is running.
    /// </summary>
    public static async Task<List<Todo>> PendingAsync(AppDb db, VisionSettings vision, PlayerTagJobs? jobs, int max, CancellationToken ct = default)
    {
        var galleries = await db.Galleries.AsNoTracking().ToDictionaryAsync(g => g.Id, ct);
        var teams = await db.Teams.AsNoTracking().Include(t => t.Players).ToDictionaryAsync(t => t.Id, ct);
        var rows = await db.Photos.AsNoTracking()
            .Where(p => p.Approved && p.DescriptionSource != Admin && p.DescriptionAttempts < MaxAttempts)
            .Select(p => new { p.Id, p.GalleryId, p.RelPath, p.DescriptionSource, p.DescriptionPlayers, p.PlayersCheckedAt, p.PlayerCheckAttempts })
            .ToListAsync(ct);
        var inTeams = rows.Where(r => galleries.TryGetValue(r.GalleryId, out var g) && g.TeamId != null && teams.ContainsKey(g.TeamId.Value)).Select(r => r.Id).ToList();
        var tags = inTeams.Count == 0 ? new Dictionary<int, List<string>>() : await Tags.NamesByPhotoAsync(db, inTeams);
        var kits = new Dictionary<int, Dictionary<string, string>>();
        var list = new List<Todo>();
        foreach (var r in rows)
        {
            if (!galleries.TryGetValue(r.GalleryId, out var g) || r.Id == GalleryRules.HiddenCoverId(g)) continue;
            if (jobs != null && jobs.Status(g.Id).Running) continue;
            var team = g.TeamId is { } tid && teams.TryGetValue(tid, out var t) ? t : null;
            if (team != null && PlayerTagJobs.AutoTagOn(g, vision) && r.PlayersCheckedAt == null && r.PlayerCheckAttempts < PlayerTagJobs.AutoAttempts) continue;
            var on = team == null ? new List<Player>() : PlayersOn(team, tags.GetValueOrDefault(r.Id) ?? new());
            var labels = team == null ? new List<string>() : on.Select(p => Label(team, p)).ToList();
            var sig = Signature(team, labels);
            var stale = r.DescriptionSource == Ai && r.DescriptionPlayers != sig;
            if (r.DescriptionSource == Ai && !stale) continue;
            var colors = "";
            if (team != null)
            {
                if (!kits.TryGetValue(g.Id, out var fk)) kits[g.Id] = fk = PlayerTags.FolderKits(g);
                colors = PlayerTags.ColorsFor(team, PlayerTags.KitFor(g, r.RelPath, fk));
            }
            list.Add(new Todo(r.Id, team, colors, labels, on.Select(p => p.Number).ToList(), sig, stale));
        }
        return list.OrderByDescending(x => x.Stale).ThenByDescending(x => x.PhotoId).Take(max).ToList();
    }

    /// <summary>What writing one photo's description gave: the text, or why it failed.</summary>
    public record Outcome(bool Ok, string Photo, int GalleryId, string Text, string Provider, long Ms);

    /// <summary>Writes one photo's description with the first provider that answers. Not Ok when every provider failed (or you wrote one meanwhile).</summary>
    public static async Task<Outcome> DescribeAsync(AppDb db, ImageService images, IVisionReader reader, VisionSettings vision, Todo todo, ILogger log, CancellationToken ct)
    {
        var photo = await db.Photos.FirstOrDefaultAsync(p => p.Id == todo.PhotoId, ct);
        if (photo == null) return new Outcome(false, "", 0, "The photo is gone", "", 0);
        var chain = vision.Chain;
        if (chain.Count == 0) return new Outcome(false, photo.Name, photo.GalleryId, "No provider is set up", "", 0);
        string? text = null;
        VisionText? answer = null;
        var errors = new List<string>();
        try
        {
            var v = await images.GetVariantAsync(photo, "ai", PlayerTags.ImageSize, "jpg", false) ?? throw new VisionException("The photo file could not be read");
            var jpeg = await File.ReadAllBytesAsync(v.path, ct);
            var prompt = Prompt(todo.Team != null, todo.Colors, todo.Labels);
            foreach (var provider in chain)
            {
                try
                {
                    answer = await reader.AskTextAsync(provider, jpeg, prompt, ct);
                    text = Scrub(Parse(answer.Text), todo.Numbers);
                    if (text.Length == 0) throw new VisionException("The description was empty");
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    text = null;
                    errors.Add($"{VisionProvider.Label(provider.Kind)}: {ex.Message}");
                }
            }
        }
        catch (VisionException ex) { errors.Add(ex.Message); }

        await db.Entry(photo).ReloadAsync(ct);   // you may have written one by hand while the model was looking
        if (photo.DescriptionSource == Admin) return new Outcome(false, photo.Name, photo.GalleryId, "Skipped: you wrote a description while the AI was looking", answer?.Provider ?? "", answer?.Ms ?? 0);
        if (text == null)
        {
            photo.DescriptionAttempts++;
            await db.SaveChangesAsync(ct);
            log.LogWarning("Describing photo {Id} failed (try {Attempt}): {Error}", photo.Id, photo.DescriptionAttempts, string.Join(" / ", errors));
            return new Outcome(false, photo.Name, photo.GalleryId, $"Try {photo.DescriptionAttempts} of {MaxAttempts} failed: " + string.Join(" / ", errors), "", 0);
        }
        photo.Description = text;
        photo.DescriptionSource = Ai;
        photo.DescriptionPlayers = todo.Signature;
        photo.DescriptionAttempts = 0;
        photo.DescribedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Described photo {Id}{Again} with {Provider} in {Ms} ms", photo.Id, todo.Stale ? " again after a retag" : "", answer?.Provider, answer?.Ms);
        return new Outcome(true, photo.Name, photo.GalleryId, text, answer?.Provider ?? "", answer?.Ms ?? 0);
    }

    /// <summary>
    /// One round: describes up to <paramref name="max"/> photos. Stops after three failures in a row (the provider is down
    /// or misconfigured) instead of using up every photo's tries.
    /// </summary>
    public static async Task<(int Written, int Failed)> RunRoundAsync(IServiceProvider sp, int max, CancellationToken ct)
    {
        var vision = sp.GetRequiredService<SettingsService>().Vision;
        if (!vision.DescribePhotos || vision.Chain.Count == 0) return (0, 0);
        var jobs = sp.GetService<PlayerTagJobs>();
        if (jobs != null && jobs.All().Any(s => s.Running && !s.Waiting)) return (0, 0);   // player checks go first and share the provider
        var db = sp.GetRequiredService<AppDb>();
        var images = sp.GetRequiredService<ImageService>();
        var reader = sp.GetRequiredService<IVisionReader>();
        var log = sp.GetRequiredService<ILogger<DescriptionService>>();
        var svc = sp.GetService<DescriptionService>();
        int written = 0, failed = 0, inARow = 0;
        foreach (var todo in await PendingAsync(db, vision, jobs, max, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (jobs != null && jobs.All().Any(s => s.Running && !s.Waiting)) break;
            svc?.Working(todo.PhotoId);
            var started = DateTime.UtcNow;
            Outcome r;
            try { r = await DescribeAsync(db, images, reader, vision, todo, log, ct); }
            finally { svc?.Working(null); }
            svc?.Add(new DescriptionEntry(todo.PhotoId, r.Photo, r.GalleryId, r.Ok, r.Text, todo.Stale, VisionProvider.Label(r.Provider), r.Ok ? r.Ms : (long)(DateTime.UtcNow - started).TotalMilliseconds, DateTime.UtcNow));
            if (r.Ok) { written++; inARow = 0; }
            else { failed++; if (++inARow >= 3) break; }
        }
        return (written, failed);
    }

    /// <summary>How many photos have a description, and how many are waiting for one. For the settings page.</summary>
    public static async Task<object> CountsAsync(AppDb db)
    {
        var described = await db.Photos.CountAsync(p => p.Approved && p.Description != "");
        var byHand = await db.Photos.CountAsync(p => p.Approved && p.DescriptionSource == Admin);
        var failed = await db.Photos.CountAsync(p => p.Approved && p.DescriptionSource != Admin && p.DescriptionAttempts >= MaxAttempts);
        var total = await db.Photos.CountAsync(p => p.Approved);
        return new { described, byHand, failed, total };
    }
}

/// <summary>
/// Writes scene descriptions in the background, a few photos a round, one at a time. Off unless switched on in Settings,
/// Automatic tagging. Rounds run every minute, and soon after a photo's players change (<see cref="Nudge"/>).
/// </summary>
public class DescriptionService(IServiceScopeFactory scopes, ILogger<DescriptionService> log) : BackgroundService
{
    const int PerRound = 10;
    readonly SemaphoreSlim wake = new(0, 1);
    readonly object gate = new();
    readonly List<DescriptionEntry> recent = new();

    /// <summary>The photo being described now, and since when. Null between photos.</summary>
    public int? CurrentPhotoId { get; private set; }
    public DateTime? CurrentSince { get; private set; }
    /// <summary>When the next round runs at the latest (sooner if something changes). After failures this backs off.</summary>
    public DateTime? NextRoundAt { get; private set; }
    /// <summary>The last round had only failures, so rounds are spaced out until one works.</summary>
    public bool BackingOff { get; private set; }

    public void Working(int? photoId) { CurrentPhotoId = photoId; CurrentSince = photoId == null ? null : DateTime.UtcNow; }

    /// <summary>The last 50 photos described or failed, newest first. Kept in memory only.</summary>
    public List<DescriptionEntry> Recent { get { lock (gate) return recent.ToList(); } }
    public void Add(DescriptionEntry e) { lock (gate) { recent.Insert(0, e); if (recent.Count > 50) recent.RemoveAt(recent.Count - 1); } }

    /// <summary>Stops the background rounds (tests run rounds themselves).</summary>
    public bool Paused { get; set; }

    /// <summary>Something changed (a photo was retagged, the setting was switched on): look again soon.</summary>
    public void Nudge() { try { if (wake.CurrentCount == 0) wake.Release(); } catch (SemaphoreFullException) { } }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        var backoff = TimeSpan.FromMinutes(1);
        while (!ct.IsCancellationRequested)
        {
            int written = 0, failed = 0;
            try
            {
                using var scope = scopes.CreateScope();
                if (!Paused) (written, failed) = await PhotoDescriptions.RunRoundAsync(scope.ServiceProvider, PerRound, ct);
                if (written > 0) log.LogInformation("Wrote {Count} photo descriptions", written);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Photo descriptions stopped for this round");
                failed++;
            }
            // nothing worked: the provider is likely down, so wait longer each time (up to 30 minutes) before trying again
            backoff = failed > 0 && written == 0 ? TimeSpan.FromMinutes(Math.Min(backoff.TotalMinutes * 2, 30)) : TimeSpan.FromMinutes(1);
            BackingOff = failed > 0 && written == 0;
            if (written >= PerRound) continue;   // more are waiting
            NextRoundAt = DateTime.UtcNow + backoff;
            await wake.WaitAsync(backoff, ct);
            NextRoundAt = null;   // a change you make (such as fixing the provider settings) tries again straight away
            await Task.Delay(TimeSpan.FromSeconds(5), ct);   // let a burst of retagging settle
        }
    }
}

/// <summary>One line of the descriptions activity list: the text written, or why it failed.</summary>
public record DescriptionEntry(int PhotoId, string Photo, int GalleryId, bool Ok, string Text, bool Retag, string Provider, long Ms, DateTime At);
