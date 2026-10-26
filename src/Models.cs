using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

public enum Visibility { Public = 0, Unlisted = 1, Protected = 2 }

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
    /// <summary>Changes whenever the password changes; invalidates old access cookies.</summary>
    public string AccessStamp { get; set; } = Guid.NewGuid().ToString("N");
    public RawMatch RawMatchMode { get; set; } = RawMatch.Anywhere;
    public bool AllowDownload { get; set; } = true;
    public bool AllowRaw { get; set; }
    public bool AllowZip { get; set; } = true;
    public bool AllowGuestUpload { get; set; }
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
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public bool RawOnly => RawRelPath != null && RawRelPath == RelPath;
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

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Gallery> Galleries => Set<Gallery>();
    public DbSet<Photo> Photos => Set<Photo>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Gallery>().HasIndex(g => g.Slug).IsUnique();
        b.Entity<Photo>().HasIndex(p => new { p.GalleryId, p.RelPath }).IsUnique();
        b.Entity<ShareLink>().HasIndex(l => l.Token).IsUnique();
        b.Entity<Photo>().HasOne(p => p.Gallery).WithMany(g => g.Photos).HasForeignKey(p => p.GalleryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ShareLink>().HasOne(l => l.Gallery).WithMany(g => g.Links).HasForeignKey(l => l.GalleryId).OnDelete(DeleteBehavior.Cascade);
    }
}
