using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace PhotoGallery.Tests;

/// <summary>
/// Boots the real app against a throw-away library and data folder. Each factory gets its own folders and settings,
/// so test classes can run in parallel.
/// </summary>
public class AppFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "test-admin-password";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pgtest-" + Guid.NewGuid().ToString("N"));
    public string Library => Path.Combine(Root, "photos");
    public string Data => Path.Combine(Root, "data");
    readonly Dictionary<string, string?> settings;

    public AppFactory() : this(null) { }

    protected AppFactory(Dictionary<string, string?>? extra)
    {
        settings = extra ?? new();
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Path.Combine(Library, "Album"));
        Directory.CreateDirectory(Path.Combine(Library, "Other"));
        TestImages.Write(Path.Combine(Library, "Album", "a.jpg"), 800, 600, 90);
        TestImages.Write(Path.Combine(Library, "Album", "b.jpg"), 600, 800, 140);
        TestImages.Write(Path.Combine(Library, "Other", "c.jpg"), 640, 480, 200);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        var all = new Dictionary<string, string?>
        {
            ["PHOTO_LIBRARY"] = Library,
            ["DATA_DIR"] = Data,
            ["ADMIN_PASSWORD"] = AdminPassword,
            ["RESCAN_MINUTES"] = "0",
            ["SITE_TITLE"] = "Test Photos",
        };
        foreach (var kv in settings) all[kv.Key] = kv.Value;
        foreach (var kv in all) if (kv.Value != null) builder.UseSetting(kv.Key, kv.Value);
    }

    /// <summary>A browser-like client (cookies on, no automatic redirects) that sends the header the admin API insists on.</summary>
    public HttpClient NewClient()
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        c.DefaultRequestHeaders.Add("X-Requested-With", "pg");
        return c;
    }

    string? adminCookie;
    readonly SemaphoreSlim loginGate = new(1, 1);

    /// <summary>A client signed in as admin. The login happens once per factory (the login endpoint is rate limited) and later clients reuse its session cookie.</summary>
    public async Task<HttpClient> AdminAsync()
    {
        await loginGate.WaitAsync();
        try
        {
            if (adminCookie == null)
            {
                var c = NewClient();
                var r = await c.PostAsJsonAsync("/api/admin/login", new { password = AdminPassword });
                Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
                adminCookie = r.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]).First(v => v.StartsWith("pg_session="));
            }
        }
        finally { loginGate.Release(); }
        var admin = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        admin.DefaultRequestHeaders.Add("X-Requested-With", "pg");
        admin.DefaultRequestHeaders.Add("Cookie", adminCookie);
        return admin;
    }

    /// <summary>A new library folder with n small JPEGs (a.jpg, b.jpg, ...), named uniquely so each gallery gets its own.</summary>
    public string NewFolder(int n = 1)
    {
        var name = "f" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(Path.Combine(Library, name));
        for (int i = 0; i < n; i++) TestImages.Write(Path.Combine(Library, name, $"{(char)('a' + i)}.jpg"), 400 + i * 40, 300, 60 + i * 30);
        return name;
    }

    /// <summary>Creates a public gallery over a library folder and returns its address slug.</summary>
    public static async Task<string> GalleryAsync(HttpClient admin, string title, string folder, bool allowRaw = false, bool allowZip = true)
    {
        var r = await admin.PostAsJsonAsync("/api/admin/galleries", new { title, folder, visibility = 0, allowRaw, allowZip });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.ReadAsync()).GetProperty("gallery").GetProperty("slug").GetString()!;
    }

    public static async Task<int[]> PhotoIdsAsync(HttpClient c, string slug) =>
        (await (await c.GetAsync($"/api/g/{slug}")).ReadAsync()).GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("id").GetInt32()).ToArray();

    public async Task<T> WithDbAsync<T>(Func<AppDb, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDb>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Root, true); } catch { /* files may still be locked on Windows; temp folder, harmless */ }
    }
}

public static class Json
{
    public static async Task<JsonElement> ReadAsync(this HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}

public static class TestImages
{
    /// <summary>Writes a small solid-grey JPEG.</summary>
    public static void Write(string path, int w, int h, int grey)
    {
        using var img = NetVips.Image.Black(w, h) + grey;
        img.Cast(NetVips.Enums.BandFormat.Uchar).WriteToFile(path);
    }
}

public static class LicenseTestExtensions
{
    /// <summary>Accepts the current license wording as this client, the way the download dialog does.</summary>
    public static async Task AcceptLicenseAsync(this HttpClient c)
    {
        var version = (await (await c.GetAsync("/api/license")).ReadAsync()).GetProperty("version").GetString();
        var r = await c.PostAsJsonAsync("/api/license/accept", new { version });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }
}
