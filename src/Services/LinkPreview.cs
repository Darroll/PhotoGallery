using System.Net;
using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// Open Graph and Twitter card tags for the app's pages, so a shared link shows a picture and a title in chat apps and social feeds.
/// Protected and expired galleries reveal nothing beyond the site name: their titles and photos stay private.
/// </summary>
public static class LinkPreview
{
    public static async Task<string> BuildAsync(HttpContext ctx, AppDb db, SettingsService settings)
    {
        var site = settings.SiteName;
        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var path = ctx.Request.Path.Value ?? "/";
        var title = site;
        var description = "";
        string? image = null;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0] == "g")
        {
            var g = await db.Galleries.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == parts[1]);
            if (g != null && g.Visibility != Visibility.Protected && !GalleryRules.IsExpired(g))
            {
                title = $"{g.Title} · {site}";
                description = !string.IsNullOrWhiteSpace(g.Description) ? g.Description : EventLine(g);

                // a single photo page previews that photo; a gallery previews its cover (or its first approved photo)
                if (parts.Length >= 4 && parts[2] == "p" && int.TryParse(parts[3], out var pid))
                {
                    var p = await db.Photos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid && x.GalleryId == g.Id && x.Approved);
                    if (p != null)
                    {
                        title = $"{p.Name} · {g.Title} · {site}";
                        image = $"{baseUrl}/img/{p.Id}/view";
                    }
                }
                else
                {
                    var coverId = g.CoverPhotoId ?? await db.Photos.AsNoTracking().Where(x => x.GalleryId == g.Id && x.Approved).OrderBy(x => x.Id).Select(x => (int?)x.Id).FirstOrDefaultAsync();
                    if (coverId != null) image = $"{baseUrl}/img/{coverId}/view";
                }
            }
        }

        var url = baseUrl + path;
        var tags = new List<string?>
        {
            Meta("property", "og:site_name", site),
            Meta("property", "og:type", "website"),
            Meta("property", "og:title", title),
            Meta("property", "og:url", url),
            description.Length > 0 ? Meta("name", "description", description) : null,
            description.Length > 0 ? Meta("property", "og:description", description) : null,
            Meta("name", "twitter:card", image != null ? "summary_large_image" : "summary"),
        };
        if (image != null) tags.Add(Meta("property", "og:image", image));
        return string.Join("\n", tags.Where(t => t != null));
    }

    static string EventLine(Gallery g)
    {
        var date = g.EventDate?.ToString("MMMM d, yyyy");
        return string.Join(" · ", new[] { date, g.Location }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    static string Meta(string attr, string name, string value) =>
        $"<meta {attr}=\"{name}\" content=\"{WebUtility.HtmlEncode(value)}\">";
}
