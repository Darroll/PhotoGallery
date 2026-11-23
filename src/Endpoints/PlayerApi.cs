using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// Automatic player tagging: the AI providers (Settings, Automatic tagging), teams with their rosters, choosing a gallery's
/// team, starting a check, and reviewing what the model found. Admin only.
/// </summary>
public static class PlayerApi
{
    record TeamInput(string? Name, string? Colors, bool? TagWithNames, string? Roster, string? Season = null);
    /// <summary>A fix to one photo: the player really in it (a roster number, or none for "not a player"), replacing a suggestion or a number read wrongly.</summary>
    record PhotoPlayerInput(string? Number, int? Suggestion, string? Sighting);
    record AutoTagInput(bool? AutoTag);
    record GalleryTeamInput(int? TeamId, string? Kit, string? Folder = null);
    record CheckInput(int? Limit, bool? Recheck);
    record ReviewInput(List<int>? Approve, List<int>? Reject);
    record TestInput(string? Kind, VisionSettings? Settings);
    record NewPlayerInput(string? Number, string? Name, List<int>? Skip = null);
    record NumberInput(string? Number);
    record CopyTeamInput(string? Season, string? Name = null);

    static bool Csrf(HttpContext ctx) => ctx.Request.Headers["X-Requested-With"] == "pg";
    static string Cut(string? s, int max) { s = (s ?? "").Trim(); return s.Length > max ? s[..max] : s; }

    record FoundNumber(string Number, int Photos);

    static object TeamOut(Team t, int galleries, IEnumerable<FoundNumber>? found = null) => new
    {
        t.Id, t.Name, t.Season, fullName = t.FullName, t.Colors, t.TagWithNames, galleries, t.CreatedAt,
        // numbers the AI read on players in this team's jerseys (not ones the admin said are not ours), most photos first
        found = found ?? [],
        kits = PlayerTags.Kits(t.Colors).Select(k => { var (shirt, number) = PlayerTags.SplitJersey(k.Description); return new { name = k.Name, description = k.Description, shirt, number }; }),
        players = t.Players.OrderBy(p => p.Number.Length).ThenBy(p => p.Number, StringComparer.Ordinal).Select(p => new { p.Id, p.Number, p.Name, p.AthleteId }),
        roster = string.Join("\n", t.Players.OrderBy(p => p.Number.Length).ThenBy(p => p.Number, StringComparer.Ordinal).Select(p => p.Name.Length > 0 ? $"{p.Number} {p.Name}" : p.Number)),
    };

    public static void Map(WebApplication app)
    {
        var secured = app.MapGroup("/api/admin").RequireAuthorization("Admin");

        // --- providers ---
        secured.MapGet("/settings/vision", (SettingsService settings) => Results.Ok(settings.Vision.Public()));

        secured.MapPut("/settings/vision", (VisionSettings input, HttpContext ctx, SettingsService settings) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            return Results.Ok(settings.SetVision(input).Public());
        });

        // scene descriptions for search: how far along they are
        secured.MapGet("/settings/vision/descriptions", async (AppDb db) => Results.Ok(await PhotoDescriptions.CountsAsync(db)));

        // what the description writer is doing: the photo now, how many wait, and the latest results (refreshed by the settings page)
        secured.MapGet("/settings/vision/descriptions/activity", async (AppDb db, SettingsService settings, DescriptionService svc, PlayerTagJobs jobs) =>
        {
            var vision = settings.Vision;
            var waiting = vision.DescribePhotos ? (await PhotoDescriptions.PendingAsync(db, vision, jobs, int.MaxValue)).Count : 0;
            var recent = svc.Recent;
            var ids = recent.Select(r => r.GalleryId).Append(0).Distinct().ToList();
            var galleries = await db.Galleries.AsNoTracking().Where(g => ids.Contains(g.Id)).Select(g => new { g.Id, g.Slug, g.Title }).ToDictionaryAsync(g => g.Id);
            var current = svc.CurrentPhotoId is { } cid ? await db.Photos.AsNoTracking().Where(p => p.Id == cid).Select(p => new { p.Id, p.Name, gallery = p.Gallery!.Title }).FirstOrDefaultAsync() : null;
            return Results.Ok(new
            {
                on = vision.DescribePhotos, ready = vision.Chain.Count > 0,
                playerCheckRunning = jobs.All().Any(s => s.Running && !s.Waiting),
                current = current == null ? null : new { current.Id, current.Name, current.gallery, since = svc.CurrentSince },
                waiting, nextRoundAt = svc.NextRoundAt, backingOff = svc.BackingOff,
                counts = await PhotoDescriptions.CountsAsync(db),
                recent = recent.Select(r => new
                {
                    r.PhotoId, r.Photo, r.Ok, r.Text, r.Retag, r.Provider, r.Ms, r.At,
                    slug = galleries.TryGetValue(r.GalleryId, out var g) ? g.Slug : null, gallery = galleries.TryGetValue(r.GalleryId, out var g2) ? g2.Title : null,
                }),
            });
        });

        // gives the photos whose description failed a few times new tries
        secured.MapPost("/settings/vision/descriptions/retry", async (HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var n = await db.Photos.Where(p => p.DescriptionSource != PhotoDescriptions.Admin && p.DescriptionAttempts >= PhotoDescriptions.MaxAttempts)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.DescriptionAttempts, 0));
            return Results.Ok(new { photos = n });
        });

        // Sends one photo (or a plain test picture) to a single provider with the settings as typed, without saving them.
        secured.MapPost("/settings/vision/test", async (TestInput input, HttpContext ctx, SettingsService settings, AppDb db, ImageService images, IVisionReader reader) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var typed = (input.Settings ?? settings.Vision).Sanitized().WithStoredKeys(settings.Vision);
            var p = typed.Providers.FirstOrDefault(x => x.Kind == input.Kind);
            if (p == null) return Results.BadRequest("Unknown provider.");
            if (p.Model.Length == 0) return Results.BadRequest("Type the model name first.");
            if (!p.IsCloud && p.Url.Length == 0) return Results.BadRequest("Type the Ollama address first, such as http://192.168.1.20:11434.");
            if (p.IsCloud && p.ApiKey.Length == 0) return Results.BadRequest("Paste the API key first.");
            if (p.IsCloud && !p.SendPhotosAgreed) return Results.BadRequest("Tick that photos may be sent to this company first.");
            var photo = await db.Photos.Where(x => x.Approved && !x.IsGuest).OrderByDescending(x => x.Id).FirstOrDefaultAsync();
            byte[] jpeg;
            if (photo != null && await images.GetVariantAsync(photo, "ai", PlayerTags.ImageSize, "jpg", false) is { } v) jpeg = await File.ReadAllBytesAsync(v.path);
            else { using var img = NetVips.Image.Black(64, 64) + 128; jpeg = img.Cast(NetVips.Enums.BandFormat.Uchar).JpegsaveBuffer(); }
            try
            {
                var a = await reader.AskAsync(p, jpeg, PlayerPrompt.Build("any colors", Array.Empty<string>()), ctx.RequestAborted);
                return Results.Ok(new { ok = true, ms = a.Ms, numbers = a.Numbers.Select(n => n.Number + " (" + n.Confidence + "%)"), photo = photo?.Name });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Ok(new { ok = false, error = ex.Message });
            }
        });

        // --- teams ---
        secured.MapGet("/teams", async (AppDb db) =>
        {
            var teams = await db.Teams.Include(t => t.Players).OrderBy(t => t.Name).ThenByDescending(t => t.CreatedAt).ToListAsync();
            var use = await db.Galleries.Where(g => g.TeamId != null).GroupBy(g => g.TeamId!.Value).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
            var seen = (await db.NumberSightings.Where(x => !x.Dismissed).GroupBy(x => new { x.TeamId, x.Number })
                    .Select(x => new { x.Key.TeamId, x.Key.Number, n = x.Select(y => y.PhotoId).Distinct().Count() }).ToListAsync())
                .GroupBy(x => x.TeamId).ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.n).ThenBy(y => y.Number.Length).ThenBy(y => y.Number, StringComparer.Ordinal)
                    .Take(60).Select(y => new FoundNumber(y.Number, y.n)).ToList());
            return Results.Ok(teams.Select(t => TeamOut(t, use.GetValueOrDefault(t.Id), seen.GetValueOrDefault(t.Id))));
        });

        secured.MapPost("/teams", async (TeamInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var t = new Team();
            var err = await ApplyAsync(db, t, input);
            if (err != null) return Results.BadRequest(err);
            db.Teams.Add(t);
            await db.SaveChangesAsync();
            await PlayerTags.LinkAthletesAsync(db, t);
            await PlayerTags.AdoptSightingsAsync(db, t);
            return Results.Ok(TeamOut(t, 0));
        });

        secured.MapPut("/teams/{id:int}", async (int id, TeamInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var t = await db.Teams.Include(x => x.Players).FirstOrDefaultAsync(x => x.Id == id);
            if (t == null) return Results.NotFound();
            var err = await ApplyAsync(db, t, input);
            if (err != null) return Results.BadRequest(err);
            await db.SaveChangesAsync();
            await PlayerTags.LinkAthletesAsync(db, t);
            await PlayerTags.AdoptSightingsAsync(db, t);
            return Results.Ok(TeamOut(t, await db.Galleries.CountAsync(g => g.TeamId == id)));
        });

        // A new season of a team: the same name, jerseys, names-in-tags choice and roster, so only the players who left or joined
        // (and a new jersey) need changing. Galleries and tags stay with the old season; named players are the same girls, so follows carry on.
        secured.MapPost("/teams/{id:int}/copy", async (int id, CopyTeamInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var src = await db.Teams.AsNoTracking().Include(x => x.Players).FirstOrDefaultAsync(x => x.Id == id);
            if (src == null) return Results.NotFound();
            if (GalleryRules.CleanSeason(input.Season).Length == 0) return Results.BadRequest("Type the new season, such as Spring 2027.");
            var roster = string.Join("\n", src.Players.OrderBy(p => p.Number.Length).ThenBy(p => p.Number, StringComparer.Ordinal).Select(p => p.Name.Length > 0 ? $"{p.Number} {p.Name}" : p.Number));
            var t = new Team();
            var err = await ApplyAsync(db, t, new TeamInput(string.IsNullOrWhiteSpace(input.Name) ? src.Name : input.Name, src.Colors, src.TagWithNames, roster, input.Season));
            if (err != null) return Results.BadRequest(err);
            db.Teams.Add(t);
            await db.SaveChangesAsync();
            await PlayerTags.LinkAthletesAsync(db, t);
            return Results.Ok(TeamOut(t, 0));
        });

        // removes the team, its roster and its waiting suggestions; tags already on photos stay
        secured.MapDelete("/teams/{id:int}", async (int id, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var t = await db.Teams.FindAsync(id);
            if (t == null) return Results.NotFound();
            foreach (var g in await db.Galleries.Where(g => g.TeamId == id).ToListAsync()) g.TeamId = null;
            db.Teams.Remove(t);
            await db.SaveChangesAsync();
            await PlayerTags.LinkAthletesAsync(db);   // forgets girls no roster names any more
            return Results.NoContent();
        });

        // adds one player from a number the model read, and turns the photos it was read on into suggestions
        secured.MapPost("/teams/{id:int}/players", async (int id, NewPlayerInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var t = await db.Teams.Include(x => x.Players).FirstOrDefaultAsync(x => x.Id == id);
            if (t == null) return Results.NotFound();
            var parsed = PlayerTags.ParseRoster(Cut(input.Number, 4) + " " + Cut(input.Name, 30));
            if (parsed.Count == 0) return Results.BadRequest("A jersey number is 1 to 3 digits.");
            var (number, name) = parsed[0];
            if (t.Players.Any(p => p.Number == number)) return Results.Conflict($"#{number} is already on the roster.");
            if (t.Players.Count >= 60) return Results.BadRequest("A team can have up to 60 players.");
            t.Players.Add(new Player { Number = number, Name = name });
            // photos the admin unticked are not this player: settle them so they are not suggested
            if (input.Skip is { Count: > 0 } skip)
            {
                var ids = skip.Take(500).ToList();
                foreach (var x in await db.NumberSightings.Where(x => x.TeamId == id && x.Number == number && ids.Contains(x.PhotoId)).ToListAsync()) x.Fixed = true;
            }
            await db.SaveChangesAsync();
            await PlayerTags.LinkAthletesAsync(db, t);
            var added = await PlayerTags.AdoptSightingsAsync(db, t);
            return Results.Ok(new { number, name, suggestions = added });
        });

        // the admin says a number is not one of ours: it stops being offered, for every gallery of this team
        secured.MapPost("/teams/{id:int}/numbers/dismiss", async (int id, NumberInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var number = PlayerTags.ParseRoster(Cut(input.Number, 4)).FirstOrDefault().Number;
            if (number == null) return Results.BadRequest("A jersey number is 1 to 3 digits.");
            var list = await db.NumberSightings.Where(x => x.TeamId == id && x.Number == number).ToListAsync();
            foreach (var x in list) x.Dismissed = true;
            await db.SaveChangesAsync();
            return Results.Ok(new { dismissed = list.Count });
        });

        // --- per gallery ---
        secured.MapPut("/galleries/{id:int}/team", async (int id, GalleryTeamInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var g = await db.Galleries.FindAsync(id);
            if (g == null) return Results.NotFound();
            if (input.TeamId != null && !await db.Teams.AnyAsync(t => t.Id == input.TeamId)) return Results.BadRequest("That team no longer exists.");
            if (input.Folder != null)
            {
                // the jersey for one sub folder (a game of a tournament); empty means the gallery's jersey
                if (g.TeamId == null || input.TeamId != g.TeamId) return Results.BadRequest("Choose the team for this gallery first.");
                var folder = input.Folder.Trim().Trim('/');
                if (folder.Length == 0 || folder.Contains('/') || folder.Contains('\\') || folder is "." or "..") return Results.BadRequest("Pick one of the gallery's folders.");
                var kits = PlayerTags.FolderKits(g);
                var kit = Cut(input.Kit, 200);
                if (kit.Length == 0) kits.Remove(folder); else if (kits.Count < 200 || kits.ContainsKey(folder)) kits[folder] = kit;
                g.FolderKits = System.Text.Json.JsonSerializer.Serialize(kits);
                await db.SaveChangesAsync();
                return Results.NoContent();
            }
            // a different team has different jersey names: start over
            if (input.TeamId != g.TeamId) g.FolderKits = "{}";
            g.TeamKit = input.TeamId == null ? "" : Cut(input.Kit, 200);
            g.TeamId = input.TeamId;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // check new photos automatically: on, off, or null to follow the main setting
        secured.MapPut("/galleries/{id:int}/autotag", async (int id, AutoTagInput input, HttpContext ctx, AppDb db, PlayerTagJobs jobs, SettingsService settings) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var g = await db.Galleries.FindAsync(id);
            if (g == null) return Results.NotFound();
            g.AutoTag = input.AutoTag;
            await db.SaveChangesAsync();
            return Results.Ok(new { autoTag = g.AutoTag, on = PlayerTagJobs.AutoTagOn(g, settings.Vision) });
        });

        // one photo fixed by hand: tag the right player and/or settle the wrong reading, for this photo only
        secured.MapPost("/galleries/{id:int}/photos/{photoId:int}/player", async (int id, int photoId, PhotoPlayerInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var g = await db.Galleries.FindAsync(id);
            if (g?.TeamId == null) return Results.BadRequest("Choose the team for this gallery first.");
            if (!await db.Photos.AnyAsync(p => p.Id == photoId && p.GalleryId == id)) return Results.NotFound();
            var team = await db.Teams.Include(t => t.Players).FirstAsync(t => t.Id == g.TeamId);
            Player? player = null;
            string? newNumber = null;   // a number not on the roster yet: the photo waits under it until the player is added
            if (!string.IsNullOrWhiteSpace(input.Number))
            {
                var number = PlayerTags.ParseRoster(Cut(input.Number, 4)).FirstOrDefault().Number;
                if (number == null) return Results.BadRequest("A jersey number is 1 to 3 digits.");
                player = team.Players.FirstOrDefault(p => p.Number == number);
                if (player == null) newNumber = number;
            }
            if (input.Suggestion is { } sid)
            {
                var wrong = await db.PlayerSuggestions.FirstOrDefaultAsync(x => x.Id == sid && x.PhotoId == photoId);
                if (wrong != null && wrong.PlayerId != player?.Id && wrong.Status == "pending") { wrong.Status = "rejected"; wrong.ReviewedAt = DateTime.UtcNow; }
            }
            if (!string.IsNullOrWhiteSpace(input.Sighting))
                foreach (var x in await db.NumberSightings.Where(x => x.TeamId == team.Id && x.PhotoId == photoId && x.Number == input.Sighting).ToListAsync()) x.Fixed = true;
            if (player != null) await PlayerTags.TagByHandAsync(db, photoId, team, player);
            if (newNumber != null) await PlayerTags.HoldNumberAsync(db, photoId, team, newNumber);
            await db.SaveChangesAsync();
            return Results.Ok(new { tag = player == null ? null : PlayerTags.TagName(team, player), number = newNumber });
        });

        // where the gallery stands: its team, how many photos were checked, the job's progress, and the suggestions waiting
        secured.MapGet("/galleries/{id:int}/players", async (int id, AppDb db, PlayerTagJobs jobs, SettingsService settings) =>
        {
            var job = jobs.Status(id).Snapshot();   // taken first: when it says the check ended, the counts below include all of it
            var g = await db.Galleries.FindAsync(id);
            if (g == null) return Results.NotFound();
            var hiddenCover = GalleryRules.HiddenCoverId(g);
            var photos = db.Photos.Where(p => p.GalleryId == id && p.Approved && p.Id != hiddenCover);
            var total = await photos.CountAsync();
            var checkedCount = await photos.CountAsync(p => p.PlayersCheckedAt != null);
            var team = g.TeamId == null ? null : await db.Teams.Include(t => t.Players).FirstOrDefaultAsync(t => t.Id == g.TeamId);
            var rows = await (from s in db.PlayerSuggestions
                              join p in db.Photos on s.PhotoId equals p.Id
                              join pl in db.Players on s.PlayerId equals pl.Id
                              where p.GalleryId == id && s.Status == "pending"
                              select new { s.Id, s.PhotoId, photo = p.Name, p.TakenAt, pl.Number, player = pl.Name, s.Confidence, s.Source, pl.TeamId, s.Unsure, s.Note, s.ReadAs })
                             .ToListAsync();
            var teamNames = await db.Teams.ToDictionaryAsync(t => t.Id, t => t);
            var sightings = g.TeamId == null ? new() : await (from x in db.NumberSightings
                                   join p in db.Photos on x.PhotoId equals p.Id
                                   where p.GalleryId == id && x.TeamId == g.TeamId && !x.Dismissed && !x.Fixed
                                   select new { x.Number, x.PhotoId, x.Confidence }).ToListAsync();
            var unknown = sightings.GroupBy(x => x.Number)
                .Select(x => new { number = x.Key, photos = x.Select(y => y.PhotoId).Distinct().Count(), confidence = x.Max(y => y.Confidence), photoIds = x.OrderByDescending(y => y.Confidence).Select(y => y.PhotoId).Distinct().Take(60) })
                .OrderBy(x => int.TryParse(x.number, out var n) ? n : int.MaxValue).ThenBy(x => x.number.Length).ThenBy(x => x.number, StringComparer.Ordinal).ToList();   // in number order, like a roster
            // every tag already on the photos waiting for review, so the review grid shows who else is in each photo
            var photoTags = await Tags.NamesByPhotoAsync(db, rows.Select(r => r.PhotoId).Distinct().ToList());
            var approved = await (from s in db.PlayerSuggestions join p in db.Photos on s.PhotoId equals p.Id where p.GalleryId == id && s.Status == "approved" select s.Id).CountAsync();
            // sub folders (the games of a tournament), each with its own jersey: the gallery's sections first, then any other folder holding photos
            var folderKits = PlayerTags.FolderKits(g);
            var sectionList = GalleryRules.Sections(g);
            var photoFolders = (await photos.Select(p => p.RelPath).ToListAsync()).Select(r => GalleryRules.SectionOf(g, r)).Where(f => f.Length > 0)
                .GroupBy(f => f, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            var folders = sectionList.Where(s => photoFolders.ContainsKey(s.Folder)).Select(s => (s.Folder, s.Title))
                .Concat(photoFolders.Keys.Where(f => !sectionList.Any(s => string.Equals(s.Folder, f, StringComparison.OrdinalIgnoreCase))).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => (Folder: f, Title: GalleryRules.Pretty(f))))
                .Select(x => new { folder = x.Folder, title = x.Title, photos = photoFolders[x.Folder], kit = folderKits.GetValueOrDefault(x.Folder, "") }).ToList();
            return Results.Ok(new
            {
                teamId = g.TeamId, teamKit = g.TeamKit, folders, autoTag = g.AutoTag, allowPlayerTags = g.AllowPlayerTags, allowFollow = g.FollowPlayers, followDefault = settings.Follow.Enabled, followMail = PlayerFollows.Available(settings),
                followers = team == null ? 0 : await db.PlayerFollows.Where(f => db.Players.Any(p => p.Id == f.PlayerId && p.TeamId == team.Id)).Select(f => f.Sub).Distinct().CountAsync(),
                autoTagDefault = settings.Vision.AutoCheckNew, teamPlayers = team?.Players.Count ?? 0,
                photos = total, @checked = checkedCount, approved,
                providers = settings.Vision.Chain.Select(p => VisionProvider.Label(p.Kind)),
                job,
                unknown,
                photoTags,
                pending = rows.OrderBy(r => r.TakenAt).ThenBy(r => r.photo).Select(r => new
                {
                    r.Id, r.PhotoId, r.photo, r.Number, r.player, r.Confidence, r.Source, r.Unsure, r.Note, r.ReadAs,
                    tag = teamNames.TryGetValue(r.TeamId, out var t) ? PlayerTags.TagName(t, new Player { Number = r.Number, Name = r.player }) : r.Number,
                }),
            });
        });

        secured.MapPost("/galleries/{id:int}/players/check", async (int id, CheckInput input, HttpContext ctx, AppDb db, PlayerTagJobs jobs, SettingsService settings) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var g = await db.Galleries.FindAsync(id);
            if (g == null) return Results.NotFound();
            if (settings.Vision.Chain.Count == 0) return Results.BadRequest("No AI provider is set up yet. Open Settings, Automatic tagging.");
            if (g.TeamId == null) return Results.BadRequest("Choose the team for this gallery first.");
            if (!await db.Players.AnyAsync(p => p.TeamId == g.TeamId)) return Results.BadRequest("Add the team's players (their numbers) first.");
            var limit = input.Limit is { } n ? Math.Clamp(n, 1, 100) : (int?)null;
            if (!jobs.Start(id, g.Title, limit, input.Recheck == true)) return Results.Conflict("A check is already running for this gallery.");
            return Results.Accepted();
        });

        secured.MapPost("/galleries/{id:int}/players/stop", (int id, HttpContext ctx, PlayerTagJobs jobs) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            return jobs.Stop(id) ? Results.Accepted() : Results.Conflict("No check is running for this gallery.");
        });

        // every gallery's check that is running, waiting, or ended in the last day: the activity panel
        secured.MapGet("/players/jobs", async (AppDb db, PlayerTagJobs jobs) =>
        {
            var list = jobs.All();
            var ids = list.Select(j => j.GalleryId).ToList();
            var pending = await (from s in db.PlayerSuggestions join p in db.Photos on s.PhotoId equals p.Id
                                 where s.Status == "pending" && ids.Contains(p.GalleryId) group s by p.GalleryId into x select new { x.Key, n = x.Count() })
                                .ToDictionaryAsync(x => x.Key, x => x.n);
            return Results.Ok(list.Select(j => new { job = j, pending = pending.GetValueOrDefault(j.GalleryId) }));
        });

        secured.MapPost("/players/review", async (ReviewInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var ids = (input.Approve ?? new()).Concat(input.Reject ?? new()).Distinct().Take(2000).ToList();
            var list = await db.PlayerSuggestions.Where(s => ids.Contains(s.Id) && s.Status == "pending").ToListAsync();
            var players = await db.Players.Include(p => p.Team).Where(p => list.Select(s => s.PlayerId).Contains(p.Id)).ToDictionaryAsync(p => p.Id);
            var approve = (input.Approve ?? new()).ToHashSet();
            int a = 0, r = 0;
            foreach (var s in list)
            {
                if (s.Status != "pending") continue;   // already settled by a choice below
                if (approve.Contains(s.Id) && players.TryGetValue(s.PlayerId, out var pl))
                {
                    await PlayerTags.ApproveAsync(db, s, pl.Team!, pl); a++;
                    if (!s.Unsure) continue;
                    // one partly hidden number is one player: picking it rejects the other players it could have been
                    var others = await db.PlayerSuggestions.Where(x => x.PhotoId == s.PhotoId && x.Id != s.Id && x.Status == "pending" && x.Unsure && x.ReadAs == s.ReadAs).ToListAsync();
                    foreach (var o in others) { o.Status = "rejected"; o.ReviewedAt = DateTime.UtcNow; r++; }
                }
                else { s.Status = "rejected"; s.ReviewedAt = DateTime.UtcNow; r++; }
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { approved = a, rejected = r });
        });
    }

    /// <summary>Copies the typed team onto the record. The roster replaces the old one; players still listed keep their suggestions.</summary>
    static async Task<string?> ApplyAsync(AppDb db, Team t, TeamInput input)
    {
        var name = Cut(input.Name, 30);
        if (name.Length == 0) return "Give the team a name.";
        var season = input.Season == null ? t.Season : GalleryRules.CleanSeason(input.Season);
        if (await db.Teams.AnyAsync(x => x.Id != t.Id && x.Name.ToLower() == name.ToLower() && x.Season.ToLower() == season.ToLower()))
            return season.Length == 0 ? "Another team already has that name. Give this one a season, such as Fall 2026." : $"{name} already has a {season} season.";
        t.Name = name;
        t.Season = season;
        t.Colors = string.Join("\n", PlayerTags.Kits(Cut(input.Colors, 20000)).Take(100).Select(k => k.Name == k.Description ? k.Description : $"{k.Name}: {k.Description}"));
        t.TagWithNames = input.TagWithNames == true;
        if (input.Roster != null)
        {
            var roster = PlayerTags.ParseRoster(input.Roster);
            foreach (var old in t.Players.ToList())
                if (!roster.Any(x => x.Number == old.Number)) { t.Players.Remove(old); db.Players.Remove(old); }
            foreach (var (number, pname) in roster)
            {
                var p = t.Players.FirstOrDefault(x => x.Number == number);
                if (p == null) t.Players.Add(new Player { Number = number, Name = pname });
                else p.Name = pname;
            }
        }
        return null;
    }
}
