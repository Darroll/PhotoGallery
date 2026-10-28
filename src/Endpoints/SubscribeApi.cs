using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>Reader subscriptions: sign up by email, confirm by link, unsubscribe by link.</summary>
public static class SubscribeApi
{
    record SubscribeInput(string? Email);

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/subscribe", async (SubscribeInput input, HttpContext ctx, AppDb db, SettingsService settings) =>
        {
            if (ctx.Request.Headers["X-Requested-With"] != "pg") return Results.BadRequest("missing header");
            if (!Subscribers.Available(settings)) return Results.BadRequest("Subscriptions are not available on this site.");
            var email = (input.Email ?? "").Trim();
            if (!Subscribers.ValidAddress(email)) return Results.BadRequest("Enter a valid email address.");

            // the same answer whether or not the address is already known, so the form cannot be used to look people up
            var s = await db.Subscribers.FirstOrDefaultAsync(x => x.Email.ToLower() == email.ToLower());
            if (s != null && s.ConfirmedAt != null && !s.Unsubscribed) return Results.Accepted();
            if (s == null)
            {
                s = new Subscriber { Email = email };
                db.Subscribers.Add(s);
            }
            s.Token = Passwords.NewToken();
            s.ConfirmedAt = null;
            s.Unsubscribed = false;
            await db.SaveChangesAsync();
            await Subscribers.QueueConfirmationAsync(db, settings, s);
            return Results.Accepted();
        }).RequireRateLimiting("subscribe");

        app.MapGet("/subscribe/confirm/{token}", async (string token, AppDb db) =>
        {
            var s = await db.Subscribers.FirstOrDefaultAsync(x => x.Token == token);
            if (s == null) return Results.Redirect("/?subscribed=0");
            s.ConfirmedAt ??= DateTime.UtcNow;
            s.Unsubscribed = false;
            await db.SaveChangesAsync();
            return Results.Redirect("/?subscribed=1");
        });

        app.MapGet("/unsubscribe/{token}", async (string token, AppDb db) =>
        {
            var s = await db.Subscribers.FirstOrDefaultAsync(x => x.Token == token);
            if (s != null)
            {
                s.Unsubscribed = true;
                await db.SaveChangesAsync();
            }
            return Results.Redirect("/?unsubscribed=1");
        });
    }
}
