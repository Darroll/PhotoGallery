using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>Whether this visitor has accepted the current license wording. Downloads are refused until they have.</summary>
public static class LicenseGate
{
    public static async Task<bool> AcceptedAsync(HttpContext ctx, AppDb db, SettingsService settings)
    {
        if (AccessService.IsAdmin(ctx)) return true;
        var license = settings.License.ForVisitors(settings.SiteName);
        if (!license.Enabled || !license.Require) return true;

        var sub = AuthApi.Sub(ctx) ?? "";
        var visitor = ActivityLog.ReadVisitorId(ctx);
        if (sub == "" && visitor == "") return false;
        // an account counts everywhere it signs in; a browser counts for anyone using it
        return await db.LicenseAcceptances.AnyAsync(a => a.Version == license.Version
            && ((sub != "" && a.Sub == sub) || (visitor != "" && a.VisitorId == visitor)));
    }

    /// <summary>The refusal for a download without an accepted license; the page reads licenseRequired to explain it.</summary>
    public static IResult Required() => Results.Json(new { licenseRequired = true }, statusCode: 403);
}
