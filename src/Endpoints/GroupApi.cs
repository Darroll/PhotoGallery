using Microsoft.EntityFrameworkCore;

namespace PhotoGallery;

/// <summary>
/// Groups of people kept in the app: the admin makes named groups of email addresses and invites them to Specific people galleries.
/// A person can be in many groups. Everything here is admin only, and changes apply to access at once.
/// </summary>
public static class GroupApi
{
    record GroupInput(string? Name, List<string>? Emails);
    record IdsInput(List<int>? Ids);
    record PersonInput(string? Email, List<int>? GroupIds);

    static bool Csrf(HttpContext ctx) => ctx.Request.Headers["X-Requested-With"] == "pg";

    public static void Map(WebApplication app)
    {
        var secured = app.MapGroup("/api/admin").RequireAuthorization("Admin");

        secured.MapGet("/groups", async (AppDb db) => Results.Ok(await db.PeopleGroups.AsNoTracking().OrderBy(g => g.Name)
            .Select(g => new
            {
                g.Id, g.Name,
                members = db.GroupMembers.Count(m => m.GroupId == g.Id),
                galleries = db.GalleryGroups.Count(x => x.GroupId == g.Id)
            }).ToListAsync()));

        secured.MapGet("/groups/{id:int}", async (int id, AppDb db) =>
        {
            var g = await db.PeopleGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (g == null) return Results.NotFound();
            var emails = await db.GroupMembers.AsNoTracking().Where(m => m.GroupId == id).Select(m => m.Email).OrderBy(e => e).ToListAsync();
            return Results.Ok(new { g.Id, g.Name, emails });
        });

        secured.MapPost("/groups", async (GroupInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var name = PeopleGroups.CleanName(input.Name);
            if (name.Length == 0) return Results.BadRequest("Give the group a name.");
            if (await db.PeopleGroups.AnyAsync(x => x.Name.ToLower() == name.ToLower())) return Results.BadRequest("A group with that name already exists.");
            var emails = GalleryPeople.Clean(input.Emails ?? new List<string>());
            if (emails == null) return Results.BadRequest("One of the addresses is not a valid email address.");
            if (emails.Count > PeopleGroups.MaxMembers) return Results.BadRequest($"Up to {PeopleGroups.MaxMembers} people per group.");

            var group = new PeopleGroup { Name = name };
            db.PeopleGroups.Add(group);
            await db.SaveChangesAsync();
            db.GroupMembers.AddRange(emails.Select(e => new GroupMember { GroupId = group.Id, Email = e }));
            await db.SaveChangesAsync();
            return Results.Ok(new { group.Id, group.Name, members = emails.Count });
        });

        // renames the group and/or replaces its member list (whichever is sent)
        secured.MapPut("/groups/{id:int}", async (int id, GroupInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var group = await db.PeopleGroups.FirstOrDefaultAsync(x => x.Id == id);
            if (group == null) return Results.NotFound();

            if (input.Name != null)
            {
                var name = PeopleGroups.CleanName(input.Name);
                if (name.Length == 0) return Results.BadRequest("Give the group a name.");
                if (await db.PeopleGroups.AnyAsync(x => x.Id != id && x.Name.ToLower() == name.ToLower())) return Results.BadRequest("A group with that name already exists.");
                group.Name = name;
            }
            if (input.Emails != null)
            {
                var emails = GalleryPeople.Clean(input.Emails);
                if (emails == null) return Results.BadRequest("One of the addresses is not a valid email address.");
                if (emails.Count > PeopleGroups.MaxMembers) return Results.BadRequest($"Up to {PeopleGroups.MaxMembers} people per group.");
                var current = await db.GroupMembers.Where(m => m.GroupId == id).ToListAsync();
                db.GroupMembers.RemoveRange(current.Where(m => !emails.Contains(m.Email)));
                db.GroupMembers.AddRange(emails.Where(e => !current.Any(m => m.Email == e)).Select(e => new GroupMember { GroupId = id, Email = e }));
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { group.Id, group.Name, members = await db.GroupMembers.CountAsync(m => m.GroupId == id) });
        });

        // deleting a group also removes it from every gallery it was invited to
        secured.MapDelete("/groups/{id:int}", async (int id, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var group = await db.PeopleGroups.FirstOrDefaultAsync(x => x.Id == id);
            if (group == null) return Results.NotFound();
            await db.GroupMembers.Where(m => m.GroupId == id).ExecuteDeleteAsync();
            await db.GalleryGroups.Where(x => x.GroupId == id).ExecuteDeleteAsync();
            db.PeopleGroups.Remove(group);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // every person who is in at least one group, with the groups they are in
        secured.MapGet("/people", async (AppDb db) =>
        {
            var rows = await db.GroupMembers.AsNoTracking().ToListAsync();
            return Results.Ok(rows.GroupBy(m => m.Email).OrderBy(g => g.Key)
                .Select(g => new { email = g.Key, groupIds = g.Select(m => m.GroupId).OrderBy(i => i).ToList() }));
        });

        // sets all of one person's groups at once; an empty list takes them out of every group
        secured.MapPut("/people/groups", async (PersonInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            var cleaned = GalleryPeople.Clean(new[] { input.Email ?? "" });
            if (cleaned == null || cleaned.Count != 1) return Results.BadRequest("Enter a valid email address.");
            var email = cleaned[0];

            var wanted = (input.GroupIds ?? new List<int>()).Distinct().ToList();
            var valid = await db.PeopleGroups.Where(g => wanted.Contains(g.Id)).Select(g => g.Id).ToListAsync();
            var current = await db.GroupMembers.Where(m => m.Email == email).ToListAsync();
            db.GroupMembers.RemoveRange(current.Where(m => !valid.Contains(m.GroupId)));
            db.GroupMembers.AddRange(valid.Where(i => !current.Any(m => m.GroupId == i)).Select(i => new GroupMember { GroupId = i, Email = email }));
            await db.SaveChangesAsync();
            return Results.Ok(new { email, groupIds = valid.OrderBy(i => i).ToList() });
        });

        // the groups invited to a gallery
        secured.MapGet("/galleries/{id:int}/groups", async (int id, AppDb db) =>
        {
            if (!await db.Galleries.AnyAsync(g => g.Id == id)) return Results.NotFound();
            return Results.Ok(await db.GalleryGroups.AsNoTracking().Where(x => x.GalleryId == id).Select(x => x.GroupId).OrderBy(i => i).ToListAsync());
        });

        // replaces the groups invited to a gallery
        secured.MapPut("/galleries/{id:int}/groups", async (int id, IdsInput input, HttpContext ctx, AppDb db) =>
        {
            if (!Csrf(ctx)) return Results.BadRequest("missing header");
            if (!await db.Galleries.AnyAsync(g => g.Id == id)) return Results.NotFound();
            var wanted = (input.Ids ?? new List<int>()).Distinct().ToList();
            var valid = await db.PeopleGroups.Where(g => wanted.Contains(g.Id)).Select(g => g.Id).ToListAsync();
            var current = await db.GalleryGroups.Where(x => x.GalleryId == id).ToListAsync();
            db.GalleryGroups.RemoveRange(current.Where(x => !valid.Contains(x.GroupId)));
            db.GalleryGroups.AddRange(valid.Where(i => !current.Any(x => x.GroupId == i)).Select(i => new GalleryGroup { GalleryId = id, GroupId = i }));
            await db.SaveChangesAsync();
            return Results.Ok(valid.OrderBy(i => i).ToList());
        });
    }
}
