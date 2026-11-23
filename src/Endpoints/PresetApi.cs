using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>Gallery presets: save a gallery's settings under a name, list, rename, delete, and choose the one new galleries start with.</summary>
public static class PresetApi
{
    record SaveInput(int GalleryId, string? Name, string? ReplaceId, bool? MakeDefault);
    record ChangeInput(string? Name, bool? IsDefault);

    static bool Csrf(HttpContext ctx) => ctx.Request.Headers["X-Requested-With"] == "pg";

    static async Task<object> ListAsync(AppDb db, PresetSettings p)
    {
        var teams = (await db.Teams.AsNoTracking().ToListAsync()).ToDictionary(t => t.Id, t => t.FullName);
        return new { defaultId = p.DefaultId, items = p.Items.Select(x => GalleryPresets.Dto(x, p.DefaultId, teams)) };
    }

    public static void Map(WebApplication app)
    {
        var secured = app.MapGroup("/api/admin").RequireAuthorization("Admin");

        secured.MapGet("/presets", async (AppDb db, SettingsService settings) => Results.Ok(await ListAsync(db, settings.Presets)));

        // saves a gallery's current settings as a new preset, or over an existing one
        secured.MapPost("/presets", async (SaveInput input, HttpContext ctx, AppDb db, SettingsService settings) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var g = await db.Galleries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == input.GalleryId);
            if (g == null) return Results.NotFound();
            var current = settings.Presets;
            var replacing = input.ReplaceId == null ? null : current.Items.FirstOrDefault(x => x.Id == input.ReplaceId);
            if (input.ReplaceId != null && replacing == null) return Results.BadRequest("That preset no longer exists.");
            var name = (input.Name ?? "").Trim();
            if (name.Length == 0) name = replacing?.Name ?? "";
            if (name.Length == 0) return Results.BadRequest("Give the preset a name.");
            if (current.Items.Any(x => x.Id != replacing?.Id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest("A preset with that name already exists.");
            if (replacing == null && current.Items.Count >= GalleryPreset.MaxPresets) return Results.BadRequest($"You can keep up to {GalleryPreset.MaxPresets} presets. Delete one first.");
            var preset = await GalleryPresets.FromGalleryAsync(db, g, name);
            if (replacing != null) { preset.Id = replacing.Id; preset.CreatedAt = replacing.CreatedAt; }
            var saved = settings.UpdatePresets(p =>
            {
                var at = p.Items.FindIndex(x => x.Id == preset.Id);
                if (at >= 0) p.Items[at] = preset; else p.Items.Add(preset);
                // the first preset, or one asked for, is what new galleries start with
                if (input.MakeDefault == true || p.DefaultId == null && p.Items.Count == 1) p.DefaultId = preset.Id;
            });
            return Results.Ok(await ListAsync(db, saved));
        });

        secured.MapPut("/presets/{id}", async (string id, ChangeInput input, HttpContext ctx, AppDb db, SettingsService settings) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var current = settings.Presets;
            if (!current.Items.Any(x => x.Id == id)) return Results.NotFound();
            var name = input.Name?.Trim();
            if (name != null && name.Length == 0) return Results.BadRequest("Give the preset a name.");
            if (name != null && current.Items.Any(x => x.Id != id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest("A preset with that name already exists.");
            var saved = settings.UpdatePresets(p =>
            {
                var item = p.Items.FirstOrDefault(x => x.Id == id);
                if (item == null) return;
                if (name != null) item.Name = name;
                if (input.IsDefault == true) p.DefaultId = id;
                else if (input.IsDefault == false && p.DefaultId == id) p.DefaultId = null;
            });
            return Results.Ok(await ListAsync(db, saved));
        });

        secured.MapDelete("/presets/{id}", async (string id, HttpContext ctx, AppDb db, SettingsService settings) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            if (!settings.Presets.Items.Any(x => x.Id == id)) return Results.NotFound();
            var saved = settings.UpdatePresets(p => p.Items.RemoveAll(x => x.Id == id));
            return Results.Ok(await ListAsync(db, saved));
        });
    }
}
