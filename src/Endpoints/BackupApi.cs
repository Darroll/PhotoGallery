namespace PhotoGallery;

public static class BackupApi
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/admin/backups").RequireAuthorization("Admin");

        g.MapGet("", (BackupService backups, AppConfig cfg) => Results.Ok(new
        {
            enabled = backups.Enabled,
            everyHours = cfg.BackupHours, keepDaily = cfg.BackupDaily, keepWeekly = cfg.BackupWeekly,
            lastError = backups.LastError,
            copyDir = cfg.BackupCopyDir, copyError = backups.CopyError,
            snapshots = backups.List().Select(s => new { s.Name, s.Time, s.Bytes }),
            safety = backups.SafetyCopies().Select(s => new { s.Name, s.Time, s.Bytes })
        }));

        g.MapPost("run", async (HttpRequest req, BackupService backups) =>
        {
            if (req.Headers["X-Requested-With"] != "pg") return Results.BadRequest("missing header");
            try
            {
                var s = await backups.RunAsync(req.HttpContext.RequestAborted);
                return Results.Ok(new { s.Name, s.Time, s.Bytes });
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Results.Problem("Backup failed: " + ex.Message); }
        });

        // A copy to keep somewhere else: the file is a normal SQLite database.
        g.MapGet("{name}/download", (string name, BackupService backups) =>
            backups.PathOf(name) is { } file ? Results.File(file, "application/octet-stream", name) : Results.NotFound());

        g.MapPost("restore", async (HttpRequest req, RestoreInput input, BackupService backups) =>
        {
            if (req.Headers["X-Requested-With"] != "pg") return Results.BadRequest("missing header");
            if (backups.PathOf(input.Name ?? "") is not { } file) return Results.NotFound();
            return await RestoreAsync(backups, file, req.HttpContext.RequestAborted);
        });

        // Restore from a file the admin kept elsewhere (a backup downloaded earlier).
        g.MapPost("restore-upload", async (HttpRequest req, BackupService backups) =>
        {
            if (req.Headers["X-Requested-With"] != "pg") return Results.BadRequest("missing header");
            if (req.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = null;
            Directory.CreateDirectory(backups.Dir);
            var tmp = Path.Combine(backups.Dir, "upload-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var f = File.Create(tmp)) await req.Body.CopyToAsync(f, req.HttpContext.RequestAborted);
                return await RestoreAsync(backups, tmp, req.HttpContext.RequestAborted);
            }
            finally { try { File.Delete(tmp); } catch { } }
        });
    }

    record RestoreInput(string? Name);

    static async Task<IResult> RestoreAsync(BackupService backups, string file, CancellationToken ct)
    {
        try
        {
            var r = await backups.RestoreAsync(file, ct);
            return Results.Ok(new { safetyCopy = r.SafetyCopy });
        }
        catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        catch (Microsoft.Data.Sqlite.SqliteException ex) { return Results.Problem("That file is not a readable gallery backup. (" + ex.Message + ")", statusCode: 400); }
    }
}
