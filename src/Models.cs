using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>Who can open a gallery. Restricted galleries open only for the listed, verified sign-in emails (and the admin).</summary>
public enum Visibility { Public = 0, Unlisted = 1, Protected = 2, Restricted = 3 }

/// <summary>Order of photos inside a gallery.</summary>
public enum GallerySort { Taken = 0, Name = 1, Added = 2 }

/// <summary>How a RAW file is matched to its processed image by file name.</summary>
public enum RawMatch { SameFolder = 0, Anywhere = 1, Fuzzy = 2 }

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
    public GallerySort SortMode { get; set; } = GallerySort.Taken;
    public bool SortDesc { get; set; }
    public DateTime? EventDate { get; set; }
    public string Location { get; set; } = "";
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
    public bool WatermarkView { get; set; }
    public bool WatermarkDownload { get; set; }
    public int? CoverPhotoId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
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

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Gallery>().HasIndex(g => g.Slug).IsUnique();
        b.Entity<Photo>().HasIndex(p => new { p.GalleryId, p.RelPath }).IsUnique();
        b.Entity<Photo>().HasIndex(p => p.Fingerprint);
        b.Entity<Comment>().HasIndex(c => new { c.PhotoId, c.CreatedAt });
        b.Entity<Comment>().HasIndex(c => new { c.Sub, c.CreatedAt });
        b.Entity<CommentEvent>().HasIndex(e => e.CommentId);
        b.Entity<CommentEvent>().HasIndex(e => e.At);
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
        b.Entity<ShareLink>().HasOne(l => l.Gallery).WithMany(g => g.Links).HasForeignKey(l => l.GalleryId).OnDelete(DeleteBehavior.Cascade);
    }
}
