using Athar.Data;
using Athar.Domain.Wiki;
using Microsoft.EntityFrameworkCore;

namespace Athar.Web.Auth;

/// <summary>
/// Profile management (T113, U-4): a person's display name and profile photo. Photos are PNG, JPEG or WebP up to 2 MB,
/// checked by their bytes (not the declared type), stored with the attachments (so the nightly backup has them) under a
/// fresh key per upload; the old file goes. Every change is audited.
/// </summary>
public sealed class ProfileService(IDbContextFactory<AtharDbContext> dbs, AttachmentStore store)
{
    public const long MaxAvatarBytes = 2 * 1024 * 1024;
    private static readonly string[] AvatarTypes = ["image/png", "image/jpeg", "image/webp"];

    public sealed record Profile(string Email, string DisplayName, bool HasAvatar);

    public async Task<Profile?> GetAsync(Guid orgId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        return await db.Users.Where(u => u.Id == userId).Select(u => new Profile(u.Email, u.DisplayName, u.AvatarFile != null)).SingleOrDefaultAsync(ct);
    }

    public async Task<bool> SetDisplayNameAsync(Guid orgId, Guid userId, string name, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length is 0 or > 100) return false;
        await using var db = await OpenAsync(orgId, ct);
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (user.DisplayName == name) return true;
        db.Audit(orgId, userId, "user.profile", "user", userId, new { display_name = user.DisplayName }, new { display_name = name });
        user.DisplayName = name;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Stores a new photo; returns null, or why it was refused (<c>too_large</c>, <c>not_an_image</c>).</summary>
    public async Task<string?> SetAvatarAsync(Guid orgId, Guid userId, Stream content, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxAvatarBytes) return "too_large";
        var bytes = buffer.ToArray();
        if (AttachmentRules.Sniff(bytes) is not { } type || !AvatarTypes.Contains(type)) return "not_an_image";

        var key = AttachmentStore.NewKey();
        await store.WriteAsync(key, bytes, ct);
        await using var db = await OpenAsync(orgId, ct);
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        var old = user.AvatarFile;
        user.AvatarFile = key;
        db.Audit(orgId, userId, "user.avatar", "user", userId, after: new { type });
        await db.SaveChangesAsync(ct);
        Delete(old);
        return null;
    }

    public async Task RemoveAvatarAsync(Guid orgId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (user.AvatarFile is not { } old) return;
        user.AvatarFile = null;
        db.Audit(orgId, userId, "user.avatar_remove", "user", userId);
        await db.SaveChangesAsync(ct);
        Delete(old);
    }

    /// <summary>A member's photo for someone in the same org (the org filter), or null.</summary>
    public async Task<(Stream Content, string Type)?> OpenAvatarAsync(Guid orgId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(orgId, ct);
        var key = await db.Users.Where(u => u.Id == userId).Select(u => u.AvatarFile).SingleOrDefaultAsync(ct);
        if (key is null || !File.Exists(store.PathOf(key))) return null;
        var stream = store.Open(key);
        var head = new byte[16];
        var read = await stream.ReadAsync(head, ct);
        stream.Position = 0;
        return AttachmentRules.Sniff(head.AsSpan(0, read)) is { } type && AvatarTypes.Contains(type) ? (stream, type) : null;
    }

    /// <summary><c>/avatars/{userId}</c>: a signed-in member sees the photos of their org's people; 404 shows initials.</summary>
    public static void MapAvatars(WebApplication app) =>
        app.MapGet("/avatars/{userId:guid}", async (Guid userId, HttpContext http, ProfileService profiles, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "private, max-age=300";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return await profiles.OpenAvatarAsync(http.User.OrgId(), userId, ct) is { } photo
                ? Results.Stream(photo.Content, photo.Type)
                : Results.NotFound();
        }).RequireAuthorization().ExcludeFromDescription();

    private void Delete(string? key)
    {
        if (key is not null && File.Exists(store.PathOf(key))) File.Delete(store.PathOf(key));
    }

    private async Task<AtharDbContext> OpenAsync(Guid orgId, CancellationToken ct)
    {
        var db = await dbs.CreateDbContextAsync(ct);
        db.CurrentOrgId = orgId;
        return db;
    }
}
