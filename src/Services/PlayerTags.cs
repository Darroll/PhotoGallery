using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>A team whose players are tagged automatically: its jersey colours and roster.</summary>
public class Team
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>
    /// How our jerseys look, in plain words, one set per line, optionally named: "Home: white shirts with blue numbers".
    /// The model only reads numbers on these. Each gallery can pick one set (<see cref="Gallery.TeamKit"/>).
    /// </summary>
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
    /// <summary>The number may be someone else's: part of it was hidden, or it reads like another player's with a digit missing. Never approved automatically.</summary>
    public bool Unsure { get; set; }
    /// <summary>What the model actually read, such as "1?" (a hidden digit) or "1". Unsure suggestions from one reading share it, so approving one rejects the others.</summary>
    public string ReadAs { get; set; } = "";
    /// <summary>Why it is unsure, shown in review: "read as 1?, number partly hidden".</summary>
    public string Note { get; set; } = "";
}

/// <summary>
/// A number the model read on a player in our colours that is not on the team's roster: a new player, a guest, or a
/// misreading. Kept so the admin can add the player, which turns these into suggestions without checking the photos again.
/// </summary>
public class NumberSighting
{
    public int Id { get; set; }
    public int TeamId { get; set; }
    public int PhotoId { get; set; }
    public string Number { get; set; } = "";
    public int Confidence { get; set; }
    public string Source { get; set; } = "";
    /// <summary>The admin said this number is not one of ours (for example the other team, or a misreading). It is not offered again.</summary>
    public bool Dismissed { get; set; }
    /// <summary>The admin settled this photo alone (tagged the right player, or said it is not a player). Kept so checking again does not bring it back.</summary>
    public bool Fixed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class PlayerTags
{
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
            var number = m.Groups[1].Value;   // exactly as written: 0, 00 and 07 are different jerseys
            var name = m.Groups[2].Value.Trim();
            if (name.Length > 30) name = name[..30];
            if (list.Any(x => x.Item1 == number)) continue;
            list.Add((number, name));
            if (list.Count >= 60) break;
        }
        return list;
    }

    /// <summary>The team's jersey colour sets, one per line: "Home: white shirts with blue numbers" or just the description.</summary>
    public static List<(string Name, string Description)> Kits(string? colors)
    {
        var list = new List<(string, string)>();
        foreach (var raw in (colors ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var c = line.IndexOf(':');
            var kit = c > 0 && c <= 30 && line[(c + 1)..].Trim().Length > 0 ? (line[..c].Trim(), line[(c + 1)..].Trim()) : (line.Length > 30 ? line[..30].Trim() : line, line);
            if (!list.Any(x => string.Equals(x.Item1, kit.Item1, StringComparison.OrdinalIgnoreCase))) list.Add(kit);
        }
        return list;
    }

    /// <summary>Splits "white shirts with black numbers" into its shirt and number colours, as the Teams page enters them. Other wording goes whole into the shirt.</summary>
    public static (string Shirt, string Number) SplitJersey(string description)
    {
        var m = System.Text.RegularExpressions.Regex.Match(description.Trim(), @"^(.+?) shirts(?: with (.+) numbers)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (description.Trim(), "");
    }

    /// <summary>The colours to tell the model for a gallery: one of the team's sets by name, colours typed for that gallery alone, or every set when none is chosen.</summary>
    /// <summary>The jersey set per sub folder of a gallery (folder names compared in any case). Bad JSON reads as none.</summary>
    public static Dictionary<string, string> FolderKits(Gallery g)
    {
        try
        {
            var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(g.FolderKits) ? "{}" : g.FolderKits);
            return new Dictionary<string, string>((d ?? new()).Where(x => !string.IsNullOrWhiteSpace(x.Value)), StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>The jersey worn on this photo: the one chosen for its sub folder, else the gallery's.</summary>
    public static string KitFor(Gallery g, string relPath, Dictionary<string, string>? folderKits = null)
    {
        var folder = GalleryRules.SectionOf(g, relPath);
        return folder.Length > 0 && (folderKits ?? FolderKits(g)).TryGetValue(folder, out var k) ? k : g.TeamKit;
    }

    public static string ColorsFor(Team team, string? kit)
    {
        var kits = Kits(team.Colors);
        kit = (kit ?? "").Trim();
        var chosen = kits.FirstOrDefault(x => string.Equals(x.Name, kit, StringComparison.OrdinalIgnoreCase));
        if (chosen.Description != null) return chosen.Description;
        if (kit.Length > 0) return kit;   // typed for this gallery only, not one of the team's sets
        if (kits.Count == 0) return "";
        return kits.Count == 1 ? kits[0].Description : "one of these sets: " + string.Join("; or ", kits.Select(x => x.Description));
    }

    /// <summary>The roster numbers a partly hidden reading could be: "1?" fits 10 to 19. Without a "?" only the number as read.</summary>
    public static List<string> Candidates(string read, bool partial, IEnumerable<string> roster)
    {
        if (read.Contains('?'))
        {
            var rx = new System.Text.RegularExpressions.Regex("^" + read.Replace("?", "[0-9]") + "$");
            return roster.Where(r => rx.IsMatch(r)).ToList();
        }
        return roster.Where(r => r == read).ToList();
    }

    /// <summary>More roster numbers than this for one partial reading tells nothing; it is skipped.</summary>
    const int MaxCandidates = 4;

    /// <summary>
    /// Checks one photo: asks each set-up provider in order until one answers (no fallback when only one is set up), then
    /// turns the numbers on our roster into suggestions. Numbers not on the roster are ignored.
    /// Returns what was found, or throws when every provider failed. <paramref name="trying"/> hears each provider as it is asked.
    /// </summary>
    public static async Task<PhotoCheckResult> CheckPhotoAsync(AppDb db, ImageService images, IVisionReader reader, VisionSettings vision, Photo photo, Team team, ILogger log, CancellationToken ct, Action<VisionProvider>? trying = null, string? kit = null)
    {
        var chain = vision.Chain;
        if (chain.Count == 0) throw new VisionException("No provider is set up. Open Settings, Automatic tagging.");
        var players = team.Players.ToDictionary(p => p.Number);
        var v = await images.GetVariantAsync(photo, "ai", ImageSize, "jpg", false) ?? throw new VisionException("The photo file could not be read");
        var jpeg = await File.ReadAllBytesAsync(v.path, ct);
        var prompt = PlayerPrompt.Build(ColorsFor(team, kit), players.Keys);

        VisionAnswer? answer = null;
        var errors = new List<string>();
        foreach (var provider in chain)
        {
            trying?.Invoke(provider);
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
        var known = await db.PlayerSuggestions.Where(s => s.PhotoId == photo.Id).ToDictionaryAsync(s => s.PlayerId, ct);
        var added = 0;
        var seenOurs = new List<string>();
        var source = $"{answer.Provider} {answer.Model}";
        var roster = players.Keys.ToList();
        async Task Suggest(Player player, int confidence, bool unsure, string readAs, string note)
        {
            if (known.TryGetValue(player.Id, out var had))
            {
                // a clear reading settles an unsure one still waiting
                if (!unsure && had.Unsure && had.Status == "pending") { had.Unsure = false; had.Note = ""; had.ReadAs = readAs; had.Confidence = Math.Max(had.Confidence, confidence); had.Source = source; }
                return;
            }
            var s = new PlayerSuggestion { PhotoId = photo.Id, PlayerId = player.Id, Confidence = confidence, Source = source, Status = "pending", Unsure = unsure, ReadAs = readAs, Note = note };
            db.PlayerSuggestions.Add(s);
            known[player.Id] = s;
            added++;
            if (!unsure && vision.AutoApproveAt > 0 && confidence >= vision.AutoApproveAt) await ApproveAsync(db, s, team, player);
        }
        var unknown = new List<SeenNumber>();
        foreach (var seen in answer.Numbers)
        {
            var hidden = seen.Partial || seen.Number.Contains('?');
            if (hidden)
            {
                // part of the number is hidden: suggest each roster number it could be, for the admin to pick one
                var maybe = Candidates(seen.Number, true, roster);
                if (maybe.Count == 0 || maybe.Count > MaxCandidates)
                {
                    seenOurs.Add($"#{seen.Number} may be partly hidden ({(maybe.Count == 0 ? "not on the roster" : $"{maybe.Count} roster numbers fit")})");
                    if (maybe.Count == 0 && !seen.Number.Contains('?')) unknown.Add(seen);
                    continue;
                }
                seenOurs.Add($"#{seen.Number} may be partly hidden, maybe {string.Join(" or ", maybe.Select(n => "#" + n))} ({seen.Confidence}%)");
                foreach (var n in maybe) await Suggest(players[n], seen.Confidence, true, seen.Number, seen.Number.Contains('?') ? $"read as {seen.Number}, number partly hidden" : "number may be partly hidden");
                continue;
            }
            if (players.TryGetValue(seen.Number, out var player))
            {
                seenOurs.Add($"#{player.Number} ({seen.Confidence}%)");
                await Suggest(player, seen.Confidence, false, seen.Number, "");
                continue;
            }
            unknown.Add(seen);
        }
        if (unknown.Count > 0)
        {
            var nums = unknown.Select(n => n.Number).Distinct().ToList();
            var existing = await db.NumberSightings.Where(x => x.TeamId == team.Id && nums.Contains(x.Number) && (x.PhotoId == photo.Id || x.Dismissed)).ToListAsync(ct);
            foreach (var n in unknown)
            {
                var num = n.Number;
                var same = existing.FirstOrDefault(x => x.PhotoId == photo.Id && x.Number == num) ?? db.NumberSightings.Local.FirstOrDefault(x => x.TeamId == team.Id && x.PhotoId == photo.Id && x.Number == num);
                if (same != null) { same.Confidence = Math.Max(same.Confidence, n.Confidence); continue; }
                db.NumberSightings.Add(new NumberSighting { TeamId = team.Id, PhotoId = photo.Id, Number = num, Confidence = n.Confidence, Source = source, Dismissed = existing.Any(x => x.Number == num && x.Dismissed) });
            }
        }
        var ignored = unknown.Count;
        log.LogInformation("Player check of photo {Id} ({Name}) with {Provider}: {Seen}, {Ignored} not on the roster, {Ms} ms",
            photo.Id, photo.Name, answer.Provider, seenOurs.Count == 0 ? "no players" : string.Join(", ", seenOurs), ignored, answer.Ms);
        return new PhotoCheckResult(added, seenOurs, unknown.Select(n => "#" + n.Number).Distinct().ToList(), VisionProvider.Label(answer.Provider), answer.Ms);
    }

    /// <summary>The source of a number the admin set by hand.</summary>
    public const string AdminSource = "admin";

    /// <summary>The admin says this player is in this photo: approves the suggestion, making one if the model never read it.</summary>
    public static async Task TagByHandAsync(AppDb db, int photoId, Team team, Player player)
    {
        var s = await db.PlayerSuggestions.FirstOrDefaultAsync(x => x.PhotoId == photoId && x.PlayerId == player.Id);
        if (s == null) { s = new PlayerSuggestion { PhotoId = photoId, PlayerId = player.Id, Confidence = 100, Source = AdminSource }; db.PlayerSuggestions.Add(s); }
        s.Unsure = false; s.Note = "";
        await ApproveAsync(db, s, team, player);
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

    /// <summary>
    /// Turns the numbers read earlier that are now on the team's roster into suggestions waiting for review, so a player
    /// added later does not need the photos checked again. Dismissed numbers are left alone. Call after the players are saved.
    /// </summary>
    public static async Task<int> AdoptSightingsAsync(AppDb db, Team team)
    {
        var players = team.Players.Where(p => p.Id > 0).ToDictionary(p => p.Number);
        if (players.Count == 0) return 0;
        var nums = players.Keys.ToList();
        var sightings = await db.NumberSightings.Where(x => x.TeamId == team.Id && !x.Fixed && (!x.Dismissed || x.Source == AdminSource) && nums.Contains(x.Number)).ToListAsync();
        if (sightings.Count == 0) return 0;
        var ids = players.Values.Select(p => p.Id).ToList();
        var photoIds = sightings.Select(x => x.PhotoId).Distinct().ToList();
        var known = (await db.PlayerSuggestions.Where(s => ids.Contains(s.PlayerId) && photoIds.Contains(s.PhotoId)).Select(s => new { s.PhotoId, s.PlayerId }).ToListAsync())
            .Select(x => (x.PhotoId, x.PlayerId)).ToHashSet();
        var added = 0;
        foreach (var x in sightings)
        {
            var pl = players[x.Number];
            if (known.Add((x.PhotoId, pl.Id)))
            {
                var s = new PlayerSuggestion { PhotoId = x.PhotoId, PlayerId = pl.Id, Confidence = x.Confidence, Source = x.Source, Status = "pending" };
                db.PlayerSuggestions.Add(s);
                added++;
                if (x.Source == AdminSource) await ApproveAsync(db, s, team, pl);   // the admin already said who it is
            }
            db.NumberSightings.Remove(x);
        }
        await db.SaveChangesAsync();
        return added;
    }

    /// <summary>
    /// The player chips for a gallery's filter: which of these tag names are players' tags, in jersey order. A tag counts
    /// in either form ("Hurricanes 10" or "Emma 10"), so photos tagged before the team's name setting changed still filter.
    /// The label is "#10" when every chip is one team's team-and-number tag, otherwise the tag itself.
    /// </summary>
    public static async Task<List<PlayerChip>> ChipsAsync(AppDb db, IEnumerable<string> tagNames)
    {
        var present = new HashSet<string>(tagNames, StringComparer.OrdinalIgnoreCase);
        if (present.Count == 0) return new();
        var teams = await db.Teams.AsNoTracking().Include(t => t.Players).ToListAsync();
        var found = new Dictionary<string, (Team Team, Player Player, bool Named)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in teams)
            foreach (var p in t.Players)
            {
                var plain = Tags.Clean($"{t.Name} {p.Number}");
                var named = p.Name.Length > 0 ? Tags.Clean($"{p.Name} {p.Number}") : null;
                if (present.TryGetValue(plain, out var a)) found.TryAdd(a, (t, p, false));
                if (named != null && present.TryGetValue(named, out var b)) found.TryAdd(b, (t, p, true));
            }
        var shortLabels = found.Values.All(x => !x.Named) && found.Values.Select(x => x.Team.Id).Distinct().Count() == 1;
        return found
            .OrderBy(x => x.Value.Team.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => int.TryParse(x.Value.Player.Number, out var n) ? n : int.MaxValue)
            .ThenBy(x => x.Value.Player.Number.Length)   // 0 before 00, kept apart
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new PlayerChip(x.Key, shortLabels ? "#" + x.Value.Player.Number : x.Key, x.Value.Player.Number))
            .ToList();
    }
}

/// <summary>A player filter chip on a gallery: the tag on the photos, the text on the chip, and the jersey number.</summary>
public record PlayerChip(string Tag, string Label, string Number);

/// <summary>What checking one photo gave: new suggestions, our players it read (with confidence), numbers not on the roster.</summary>
public record PhotoCheckResult(int Added, List<string> Seen, List<string> NotOnRoster, string Provider, long Ms);

/// <summary>One line of a check's activity list.</summary>
public record PlayerJobEntry(int PhotoId, string Photo, bool Ok, string Result, string Provider, long Ms, DateTime At);

/// <summary>Where a gallery's player check stands: progress, the photo being read now, and the latest results. Kept in memory only.</summary>
public class PlayerJobStatus
{
    readonly object gate = new();
    readonly List<PlayerJobEntry> recent = new();

    public int GalleryId { get; set; }
    public string Gallery { get; set; } = "";
    /// <summary>True from the button press until the check ends, including while it waits for another gallery's check.</summary>
    public bool Running { get; set; }
    /// <summary>Queued behind another gallery's check (one gallery runs at a time).</summary>
    public bool Waiting { get; set; }
    public bool StopRequested { get; set; }
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public int Total { get; set; }
    public int Done { get; set; }
    public int Found { get; set; }
    public int Failed { get; set; }
    public string LastError { get; set; } = "";
    public DateTime? FinishedAt { get; set; }
    public int? CurrentPhotoId { get; set; }
    public string CurrentPhoto { get; set; } = "";
    public string CurrentProvider { get; set; } = "";
    public DateTime? CurrentSince { get; set; }
    /// <summary>Started by a scan that found new photos, not by the admin.</summary>
    public bool Automatic { get; set; }

    /// <summary>The last 30 photos, newest first.</summary>
    public List<PlayerJobEntry> Recent { get { lock (gate) return recent.ToList(); } }

    /// <summary>A copy as it stands now, so a page can read the database after it without the job moving on in between.</summary>
    public PlayerJobStatus Snapshot() => (PlayerJobStatus)MemberwiseClone();

    public void Add(PlayerJobEntry e) { lock (gate) { recent.Insert(0, e); if (recent.Count > 30) recent.RemoveAt(recent.Count - 1); } }
}

/// <summary>
/// Runs player checks in the background, one gallery at a time and one photo at a time, so a slow model never piles up
/// requests. A check runs when the admin presses a button, or after a scan adds photos to a gallery that checks new photos
/// automatically (<see cref="AutoStartAsync"/>); there is no pass over the whole library.
/// </summary>
public class PlayerTagJobs(IServiceScopeFactory scopes, ILogger<PlayerTagJobs> log) : BackgroundService
{
    record Job(int GalleryId, int? Limit, bool Recheck, CancellationTokenSource Stop, bool Automatic = false);

    /// <summary>A failed photo is tried again on later automatic checks only this many times; a button press tries it again regardless.</summary>
    public const int AutoAttempts = 3;

    /// <summary>Whether new photos in this gallery are checked automatically: its own choice, else the main setting.</summary>
    public static bool AutoTagOn(Gallery g, VisionSettings vision) => g.AutoTag ?? vision.AutoCheckNew;

    /// <summary>
    /// After a scan or an approval adds photos: queues a check of the gallery's unchecked photos when it checks new photos
    /// automatically, has a team with players, and a provider is set up. Does nothing when a check is already running or waiting.
    /// </summary>
    public async Task<bool> AutoStartAsync(AppDb db, Gallery g, VisionSettings vision)
    {
        if (!AutoTagOn(g, vision) || g.TeamId == null || vision.Chain.Count == 0) return false;
        if (status.TryGetValue(g.Id, out var s) && s.Running) return false;
        if (!await db.Players.AnyAsync(p => p.TeamId == g.TeamId)) return false;
        var hiddenCover = GalleryRules.HiddenCoverId(g);
        if (!await db.Photos.AnyAsync(p => p.GalleryId == g.Id && p.Approved && p.Id != hiddenCover && p.PlayersCheckedAt == null && p.PlayerCheckAttempts < AutoAttempts)) return false;
        if (!Start(g.Id, g.Title, null, false, automatic: true)) return false;
        log.LogInformation("Player check of gallery {Id} ({Title}) queued automatically for its new photos", g.Id, g.Title);
        return true;
    }
    readonly Channel<Job> queue = Channel.CreateUnbounded<Job>();
    readonly ConcurrentDictionary<int, PlayerJobStatus> status = new();
    readonly ConcurrentDictionary<int, CancellationTokenSource> stops = new();

    public PlayerJobStatus Status(int galleryId) => status.TryGetValue(galleryId, out var s) ? s : new PlayerJobStatus { GalleryId = galleryId };

    /// <summary>Every check that is running or waiting, and those that ended in the last day, for the admin's activity panel.</summary>
    public List<PlayerJobStatus> All() => status.Values
        .Where(s => s.Running || s.FinishedAt > DateTime.UtcNow.AddDays(-1))
        .OrderByDescending(s => s.Running && !s.Waiting).ThenByDescending(s => s.Waiting).ThenByDescending(s => s.FinishedAt ?? s.QueuedAt)
        .ToList();

    /// <summary>Queues a check. False when this gallery already has one running or waiting.</summary>
    public bool Start(int galleryId, string galleryTitle, int? limit, bool recheck, bool automatic = false)
    {
        lock (queue)
        {
            if (status.TryGetValue(galleryId, out var s) && s.Running) return false;
            var anyRunning = status.Values.Any(x => x.Running);
            status[galleryId] = new PlayerJobStatus { GalleryId = galleryId, Gallery = galleryTitle, Running = true, Waiting = anyRunning, Automatic = automatic };
        }
        var cts = new CancellationTokenSource();
        stops[galleryId] = cts;
        queue.Writer.TryWrite(new Job(galleryId, limit, recheck, cts, automatic));
        return true;
    }

    /// <summary>Stops a gallery's check after the photo being read (that request is cancelled). False when nothing is running.</summary>
    public bool Stop(int galleryId)
    {
        if (!status.TryGetValue(galleryId, out var s) || !s.Running) return false;
        s.StopRequested = true;
        if (stops.TryGetValue(galleryId, out var cts)) cts.Cancel();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(ct))
        {
            var st = status[job.GalleryId];
            st.Waiting = false;
            st.StartedAt = DateTime.UtcNow;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, job.Stop.Token);
            try
            {
                if (!st.StopRequested) await RunAsync(job, st, linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                st.LastError = ex.Message;
                log.LogWarning(ex, "Player check of gallery {Id} stopped", job.GalleryId);
            }
            finally
            {
                if (st.StopRequested) st.LastError = "Stopped by you.";
                st.CurrentPhotoId = null; st.CurrentPhoto = ""; st.CurrentProvider = ""; st.CurrentSince = null;
                st.Running = false; st.FinishedAt = DateTime.UtcNow;
                stops.TryRemove(new KeyValuePair<int, CancellationTokenSource>(job.GalleryId, job.Stop));
                job.Stop.Dispose();
            }
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
        // every unchecked photo, including ones that failed before: a check only runs when the admin asks for it
        if (!job.Recheck) q = q.Where(p => p.PlayersCheckedAt == null);
        // an automatic check leaves photos that keep failing for the admin, instead of trying them after every scan
        if (job.Automatic) q = q.Where(p => p.PlayerCheckAttempts < AutoAttempts);
        var photos = GalleryRules.Sort(await q.ToListAsync(ct), g, x => x.TakenAt, x => x.Name, x => x.AddedAt);
        if (job.Limit is { } n) photos = photos.Take(n).ToList();
        st.Total = photos.Count;
        log.LogInformation("Player check of gallery {Id} ({Title}) started: {Count} photos", g.Id, g.Title, photos.Count);
        var inARow = 0;
        var folderKits = PlayerTags.FolderKits(g);
        foreach (var p in photos)
        {
            ct.ThrowIfCancellationRequested();
            st.CurrentPhotoId = p.Id; st.CurrentPhoto = p.Name; st.CurrentSince = DateTime.UtcNow; st.CurrentProvider = "";
            var started = DateTime.UtcNow;
            try
            {
                var r = await PlayerTags.CheckPhotoAsync(db, images, reader, vision, p, team, log, ct, pr => st.CurrentProvider = VisionProvider.Label(pr.Kind), PlayerTags.KitFor(g, p.RelPath, folderKits));
                st.Found += r.Added;
                inARow = 0;
                var text = r.Seen.Count == 0 ? "No players read" : string.Join(", ", r.Seen) + (r.Added < r.Seen.Count ? $" ({r.Seen.Count - r.Added} already suggested)" : "");
                if (r.NotOnRoster.Count > 0) text += $"; not on the roster: {string.Join(", ", r.NotOnRoster)}";
                st.Add(new PlayerJobEntry(p.Id, p.Name, true, text, r.Provider, r.Ms, DateTime.UtcNow));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                st.Failed++;
                inARow++;
                st.LastError = ex.Message;
                st.Add(new PlayerJobEntry(p.Id, p.Name, false, ex.Message, st.CurrentProvider, (long)(DateTime.UtcNow - started).TotalMilliseconds, DateTime.UtcNow));
            }
            await db.SaveChangesAsync(CancellationToken.None);
            st.Done++;
            // every provider failing on three photos in a row means it is down or misconfigured: stop instead of hammering it
            if (inARow >= 3) { st.LastError = "Stopped after three failures in a row: " + st.LastError; break; }
        }
        log.LogInformation("Player check of gallery {Id} finished: {Done} photos, {Found} new suggestions, {Failed} failed", g.Id, st.Done, st.Found, st.Failed);
    }
}
