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
            snapshots = backups.List().Select(s => new { s.Name, s.Time, s.Bytes })
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
    }
}
