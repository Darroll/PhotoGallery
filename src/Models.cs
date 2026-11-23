using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>Who can open a gallery. Restricted galleries open only for the listed, verified sign-in emails (and the admin).</summary>
public enum Visibility { Public = 0, Unlisted = 1, Protected = 2, Restricted = 3 }

/// <summary>Order of photos inside a gallery.</summary>
public enum GallerySort { Taken = 0, Name = 1, Added = 2 }

/// <summary>How a RAW file is matched to its processed image by file name.</summary>
public enum RawMatch { SameFolder = 0, Anywhere = 1, Fuzzy = 2 }
/// <summary>How the gallery cover photo is used: also one of the photos, only the cover, or the cover plus a banner at the top of the gallery page.</summary>
public enum CoverMode { WithPhotos = 0, CoverOnly = 1, Banner = 2 }

public class Gallery
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Folder relative to the library root, '/' separated.</summary>
    public string Folder { get; set; } = "";
    public Visibility Visibility { get; set; } = Visibility.Protected;
    public string? PasswordHash { get; set; }
    /// <summary>For Restricted galleries: the email addresses allowed in, one per line, lower case.</summary>
    public string AllowedEmails { get; set; } = "";
    /// <summary>Changes whenever the password changes; invalidates old access cookies.</summary>
    public string AccessStamp { get; set; } = Guid.NewGuid().ToString("N");
    public RawMatch RawMatchMode { get; set; } = RawMatch.Anywhere;
    /// <summary>When true, RAW frames without an edited image are hidden until the viewer turns the curated filter off.</summary>
    public bool HideRawOnly { get; set; } = true;
    /// <summary>Position used when the home page order is "manual".</summary>
    public int SortOrder { get; set; }
    /// <summary>JSON list of {label,url} links shown under the title (e.g. game footage on YouTube).</summary>
    public string FootageLinks { get; set; } = "[]";
    /// <summary>JSON list of sections: sub folders of the gallery folder shown under their own heading, each with its own links (e.g. one game of a tournament and its YouTube video). Empty means one flat grid.</summary>
    public string Sections { get; set; } = "[]";
    public GallerySort SortMode { get; set; } = GallerySort.Taken;
    public bool SortDesc { get; set; }
    public DateTime? EventDate { get; set; }
    /// <summary>Last day of an event that spans several days (the weekend of a tournament). Null for a one-day event.</summary>
    public DateTime? EventEndDate { get; set; }
    public string Location { get; set; } = "";
    /// <summary>The season the gallery is listed under on the home page ("Fall 2026"). Empty means worked out from the event date (see <see cref="GalleryRules.SeasonOf"/>).</summary>
    public string Season { get; set; } = "";
    /// <summary>Comma separated long-edge sizes offered for download; empty = all configured sizes.</summary>
    public string AllowedSizes { get; set; } = "";
    /// <summary>Comma separated lossless formats (png,webp,tif); empty = all.</summary>
    public string AllowedFormats { get; set; } = "";
    /// <summary>After this moment the gallery is unavailable to everyone but the admin.</summary>
    public DateTime? ExpiresAt { get; set; }
    public bool AllowDownload { get; set; } = true;
    public bool AllowRaw { get; set; }
    public bool AllowZip { get; set; } = true;
    public bool AllowGuestUpload { get; set; }
    /// <summary>Visitors may heart (like) photos. Off hides the heart everywhere.</summary>
    public bool AllowLikes { get; set; } = true;
    /// <summary>Show how many people liked each photo. The admin always sees the counts.</summary>
    public bool ShowLikeCounts { get; set; } = true;
    /// <summary>Signed-in visitors with a verified email may comment on photos here. Off until the admin turns it on.</summary>
    public bool AllowComments { get; set; }
    /// <summary>Visitors may send the photographer a short message from this gallery. Off until the admin turns it on.</summary>
    public bool AllowFeedback { get; set; }
    public bool WatermarkView { get; set; }
    public bool WatermarkDownload { get; set; }
    public int? CoverPhotoId { get; set; }
    /// <summary>With CoverOnly or Banner the cover photo is left out of the photo grid, slideshow, counts and ZIPs.</summary>
    public CoverMode CoverMode { get; set; } = CoverMode.WithPhotos;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>The team whose players are tagged automatically in this gallery, or null.</summary>
    public int? TeamId { get; set; }
    /// <summary>What the team wears in this gallery: the name of one of its jersey sets ("Home"), colours typed for this gallery alone, or empty for any of its sets.</summary>
    public string TeamKit { get; set; } = "";
    /// <summary>JSON object: sub folder name (a tournament's game) to the jersey worn in it, like <see cref="TeamKit"/>. A folder not listed uses <see cref="TeamKit"/>.</summary>
    public string FolderKits { get; set; } = "{}";
    /// <summary>Check new photos for players automatically after a scan: true or false here, or null to follow the main setting (<see cref="VisionSettings.AutoCheckNew"/>).</summary>
    public bool? AutoTag { get; set; }
    /// <summary>Signed-in visitors (parents) may suggest which players are in a photo. Their suggestions wait for the admin's review. Off by default.</summary>
    public bool AllowPlayerTags { get; set; }
    /// <summary>New photos of a player here are emailed to the player's followers: true or false here, or null to follow the site-wide setting (<see cref="FollowSettings.Enabled"/>).</summary>
    public bool? FollowPlayers { get; set; }
    public List<Photo> Photos { get; set; } = new();
    public List<ShareLink> Links { get; set; } = new();
}

public class Photo
{
    public int Id { get; set; }
    public int GalleryId { get; set; }
    public Gallery? Gallery { get; set; }
    /// <summary>Primary file (viewable image, or a RAW when no image sibling exists).</summary>
    public string RelPath { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>RAW file path (equals RelPath when RAW-only).</summary>
    public string? RawRelPath { get; set; }
    /// <summary>True when the RAW link was set (or removed) by hand; scans leave it alone.</summary>
    public bool RawLocked { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime TakenAt { get; set; }
    /// <summary>TakenAt was read from the camera's RAW file (the exact shooting time, to the hundredth of a second), so the photo sorts by when it was taken. False until a scan has looked.</summary>
    public bool TakenFromRaw { get; set; }
    public long SizeBytes { get; set; }
    public bool Approved { get; set; } = true;
    public bool IsGuest { get; set; }
    /// <summary>Short title shown under the photo. Empty when there is none.</summary>
    public string Caption { get; set; } = "";
    /// <summary>Description for screen readers (alt text).</summary>
    public string AltText { get; set; } = "";
    /// <summary>Who wrote the current caption: "auto" (a vision model), "admin" (you, or a suggestion you approved), or "" for none.</summary>
    public string CaptionSource { get; set; } = "";
    /// <summary>Automatic captioning attempts made for this photo (it stops after a few).</summary>
    public int CaptionAttempts { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Identity of the file's contents, so a rename or move keeps this photo's likes, tags and caption. See <see cref="FileIdentity"/>. Empty until the first scan that sees the file.</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary>Comments are switched off for this photo even if the gallery allows them.</summary>
    public bool CommentsOff { get; set; }
    /// <summary>When a model last looked for our players' numbers on this photo (null = not yet). See <see cref="PlayerTags"/>.</summary>
    public DateTime? PlayersCheckedAt { get; set; }
    /// <summary>Player checks tried on this photo; after a few failures it is skipped unless checked again on purpose.</summary>
    public int PlayerCheckAttempts { get; set; }
    /// <summary>
    /// What is happening in the photo, in a sentence or two, so parents can search for it ("a header in the rain"). Written by
    /// a vision model (<see cref="DescriptionSource"/> "ai") or by you ("admin"). It names only our own team's players tagged
    /// on the photo; see <see cref="PhotoDescriptions"/>.
    /// </summary>
    public string Description { get; set; } = "";
    /// <summary>"ai" (a vision model wrote it and rewrites it when the photo's players change), "admin" (you wrote it; never replaced), or "".</summary>
    public string DescriptionSource { get; set; } = "";
    /// <summary>The team and players the model was told about when it wrote <see cref="Description"/>. A different set now means the photo was retagged and the description is written again.</summary>
    public string DescriptionPlayers { get; set; } = "";
    /// <summary>Failed tries at writing the description since the last one that worked; after a few it waits until the photo changes or you ask again.</summary>
    public int DescriptionAttempts { get; set; }
    public DateTime? DescribedAt { get; set; }
    public bool RawOnly => RawRelPath != null && RawRelPath == RelPath;
}

/// <summary>A photo a visitor hearted. Visitors are identified by the anonymous id cookie.</summary>
public class Favorite
{
    public int Id { get; set; }
    public int GalleryId { get; set; }
    public int PhotoId { get; set; }
    public string VisitorId { get; set; } = "";
    /// <summary>Who this like counts for: u:&lt;account id&gt; when signed in, otherwise ip:&lt;network&gt;. See <see cref="Likes"/>.</summary>
    public string IdentityKey { get; set; } = "";
    /// <summary>The address the heart was given from. Kept for the record, never shown on any page.</summary>
    public string Ip { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Optional name a visitor gives their favorites list so the photographer knows whose it is.</summary>
public class FavoriteOwner
{
    public int Id { get; set; }
    public int GalleryId { get; set; }
    public string VisitorId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ShareLink
{
    public int Id { get; set; }
    public int GalleryId { get; set; }
    public Gallery? Gallery { get; set; }
    public string Token { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime? ExpiresAt { get; set; }
    public int? MaxDownloads { get; set; }
    public int DownloadCount { get; set; }
    public bool Revoked { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsValid(DateTime now) =>
        !Revoked && (ExpiresAt == null || ExpiresAt > now);
    public bool DownloadsExhausted => MaxDownloads != null && DownloadCount >= MaxDownloads;
}

/// <summary>A visitor who signed in through single sign-on, keyed by the provider's stable user id.</summary>
public class AppUser
{
    public string Sub { get; set; } = "";
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    /// <summary>JSON, see UserPreferences.</summary>
    public string Preferences { get; set; } = "{}";
    /// <summary>The name shown on this person's comments. Empty until they choose one.</summary>
    public string DisplayName { get; set; } = "";
}

/// <summary>One accepted license, recorded per accept click. Signed-in people are recorded by provider id; every accept is also tied to the browser's visitor id.</summary>
public class LicenseAcceptance
{
    public int Id { get; set; }
    public string Sub { get; set; } = "";
    public string VisitorId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The license wording version that was accepted (see LicenseView.Version).</summary>
    public string Version { get; set; } = "";
    public DateTime AcceptedAt { get; set; } = DateTime.UtcNow;
}

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Gallery> Galleries => Set<Gallery>();
    public DbSet<Photo> Photos => Set<Photo>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<FavoriteOwner> FavoriteOwners => Set<FavoriteOwner>();
    public DbSet<EmailOutbox> EmailOutbox => Set<EmailOutbox>();
    public DbSet<AlertState> AlertStates => Set<AlertState>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<LicenseAcceptance> LicenseAcceptances => Set<LicenseAcceptance>();
    public DbSet<Subscriber> Subscribers => Set<Subscriber>();
    public DbSet<CaptionEdit> CaptionEdits => Set<CaptionEdit>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PhotoTag> PhotoTags => Set<PhotoTag>();
    public DbSet<GalleryTag> GalleryTags => Set<GalleryTag>();
    public DbSet<PeopleGroup> PeopleGroups => Set<PeopleGroup>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<GalleryGroup> GalleryGroups => Set<GalleryGroup>();
    public DbSet<PhotoRetired> PhotosRetired => Set<PhotoRetired>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<CommentEvent> CommentEvents => Set<CommentEvent>();
    public DbSet<CommentBlock> CommentBlocks => Set<CommentBlock>();
    public DbSet<FeedbackMessage> FeedbackMessages => Set<FeedbackMessage>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<PlayerSuggestion> PlayerSuggestions => Set<PlayerSuggestion>();
    public DbSet<NumberSighting> NumberSightings => Set<NumberSighting>();
    public DbSet<PlayerFollow> PlayerFollows => Set<PlayerFollow>();
    public DbSet<Athlete> Athletes => Set<Athlete>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Gallery>().HasIndex(g => g.Slug).IsUnique();
        b.Entity<Photo>().HasIndex(p => new { p.GalleryId, p.RelPath }).IsUnique();
        b.Entity<Photo>().HasIndex(p => p.Fingerprint);
        b.Entity<Comment>().HasIndex(c => new { c.PhotoId, c.CreatedAt });
        b.Entity<Comment>().HasIndex(c => new { c.Sub, c.CreatedAt });
        b.Entity<CommentEvent>().HasIndex(e => e.CommentId);
        b.Entity<CommentEvent>().HasIndex(e => e.At);
        b.Entity<FeedbackMessage>().HasIndex(x => new { x.GalleryId, x.CreatedAt });
        b.Entity<CommentBlock>().HasIndex(x => x.Sub);
        b.Entity<CommentBlock>().HasIndex(x => x.Email);
        b.Entity<PhotoRetired>().HasIndex(r => r.Fingerprint);
        b.Entity<ShareLink>().HasIndex(l => l.Token).IsUnique();
        b.Entity<Favorite>().HasIndex(f => new { f.GalleryId, f.PhotoId, f.VisitorId }).IsUnique();
        b.Entity<Favorite>().HasIndex(f => new { f.PhotoId, f.IdentityKey });
        b.Entity<FavoriteOwner>().HasIndex(o => new { o.GalleryId, o.VisitorId }).IsUnique();
        b.Entity<AlertState>().HasKey(a => a.Key);
        b.Entity<AppUser>().HasKey(u => u.Sub);
        b.Entity<PhotoTag>().HasKey(x => new { x.PhotoId, x.TagId });
        b.Entity<GalleryTag>().HasKey(x => new { x.GalleryId, x.TagId });
        b.Entity<GroupMember>().HasKey(x => new { x.GroupId, x.Email });
        b.Entity<GalleryGroup>().HasKey(x => new { x.GalleryId, x.GroupId });
        b.Entity<Subscriber>().HasIndex(s => s.Token).IsUnique();
        b.Entity<Tag>().Property(t => t.Name).UseCollation("NOCASE");
        b.Entity<Tag>().HasIndex(t => t.Name).IsUnique();
        b.Entity<PeopleGroup>().Property(g => g.Name).UseCollation("NOCASE");
        b.Entity<PeopleGroup>().HasIndex(g => g.Name).IsUnique();
        b.Entity<Visit>().HasIndex(v => v.VisitorId);
        b.Entity<Visit>().HasIndex(v => v.Time);
        b.Entity<Visit>().HasIndex(v => new { v.GalleryId, v.Time });
        b.Entity<Photo>().HasOne(p => p.Gallery).WithMany(g => g.Photos).HasForeignKey(p => p.GalleryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Player>().HasOne(p => p.Team).WithMany(t => t.Players).HasForeignKey(p => p.TeamId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Player>().HasIndex(p => new { p.TeamId, p.Number }).IsUnique();
        b.Entity<Player>().HasOne<Athlete>().WithMany().HasForeignKey(p => p.AthleteId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Athlete>().Property(a => a.Name).UseCollation("NOCASE");
        b.Entity<Athlete>().HasIndex(a => a.Name).IsUnique();
        b.Entity<PlayerSuggestion>().HasOne<Photo>().WithMany().HasForeignKey(s => s.PhotoId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlayerSuggestion>().HasOne<Player>().WithMany().HasForeignKey(s => s.PlayerId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlayerSuggestion>().HasIndex(s => new { s.PhotoId, s.PlayerId }).IsUnique();
        b.Entity<PlayerSuggestion>().HasIndex(s => s.Status);
        b.Entity<NumberSighting>().HasOne<Photo>().WithMany().HasForeignKey(s => s.PhotoId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<NumberSighting>().HasOne<Team>().WithMany().HasForeignKey(s => s.TeamId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<NumberSighting>().HasIndex(s => new { s.TeamId, s.Number, s.PhotoId }).IsUnique();
        b.Entity<NumberSighting>().HasIndex(s => s.PhotoId);
        b.Entity<PlayerFollow>().HasOne<Player>().WithMany().HasForeignKey(f => f.PlayerId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlayerFollow>().HasIndex(f => new { f.Sub, f.PlayerId }).IsUnique();
        b.Entity<PlayerFollow>().HasIndex(f => f.Token).IsUnique();
        b.Entity<ShareLink>().HasOne(l => l.Gallery).WithMany(g => g.Links).HasForeignKey(l => l.GalleryId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>A message a visitor sent to the photographer from a gallery. The contact line is optional and only for replying.</summary>
public class FeedbackMessage
{
    public int Id { get; set; }
    public int GalleryId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Name { get; set; } = "";
    public string Contact { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Read { get; set; }
}
