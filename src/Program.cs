using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using PhotoGallery;

var builder = WebApplication.CreateBuilder(args);
var cfg = new AppConfig(builder.Configuration);
Directory.CreateDirectory(cfg.DataDir);

builder.Services.AddSingleton(cfg);
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={Path.Combine(cfg.DataDir, "photogallery.db")}"));
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<ImageService>();
builder.Services.AddScoped<AccessService>();
builder.Services.AddScoped<Scanner>();
builder.Services.AddHostedService<RescanService>();

builder.Services.AddDataProtection()
    .SetApplicationName("PhotoGallery")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(cfg.DataDir, "keys")));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "pg_admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = (cfg.MaxUploadMb + 1L) * 1024 * 1024);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    RateLimitPartition<string> Per(HttpContext c, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "?",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1) });
    o.AddPolicy("login", c => Per(c, 8));
    o.AddPolicy("unlock", c => Per(c, 10));
    o.AddPolicy("upload", c => Per(c, 30));
});

if (cfg.TrustProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var startDb = scope.ServiceProvider.GetRequiredService<AppDb>();
    startDb.Database.EnsureCreated();
    Schema.Upgrade(startDb);
}
if (cfg.AdminPasswordGenerated)
    app.Logger.LogWarning("ADMIN_PASSWORD not set. Generated admin password for this run: {Password}", cfg.AdminPassword);
if (!Directory.Exists(cfg.LibraryRoot))
    app.Logger.LogWarning("Photo library {Root} does not exist", cfg.LibraryRoot);
if (cfg.ExifTool == null)
    app.Logger.LogInformation("exiftool not found: RAW-only files will be skipped (RAW files paired with a JPEG still work)");

if (cfg.TrustProxy) app.UseForwardedHeaders();

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "same-origin";
    h["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    await next();
});

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
// The page is served with a content hash on its asset URLs, so CDNs/browsers can never keep a stale app.js.
var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
string AssetVersion()
{
    using var sha = System.Security.Cryptography.SHA256.Create();
    foreach (var f in new[] { "app.js", "style.css" })
        sha.TransformBlock(File.ReadAllBytes(Path.Combine(webRoot, f)), 0, (int)new FileInfo(Path.Combine(webRoot, f)).Length, null, 0);
    sha.TransformFinalBlock([], 0, 0);
    return Convert.ToHexString(sha.Hash!)[..10].ToLowerInvariant();
}
var indexHtml = File.ReadAllText(Path.Combine(webRoot, "index.html")).Replace("__V__", AssetVersion());
app.Use(async (ctx, next) =>
{
    if (HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Path is { Value: "/" or "/index.html" })
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(indexHtml);
        return;
    }
    await next();
});
app.UseStaticFiles(new StaticFileOptions
{
    // always revalidate the app shell so a new release is picked up without a hard refresh
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});

app.MapGet("/healthz", () => Results.Ok("ok"));
PublicApi.Map(app);
AdminApi.Map(app);

app.Run();
