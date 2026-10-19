using System.Net;
using Aspire.Hosting;
using Athar.Data;
using Athar.Data.Seed;
using Athar.Web.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Athar.IntegrationTests;

/// <summary>T113 (U-4): profile photos and display names.</summary>
[Collection(AtharCollection.Name)]
public class ProfileTests(AtharApp athar)
{
    // A real 1×1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private ProfileService Profiles() => new(new Factory(athar), new AttachmentStore(new ConfigurationBuilder().Build()));

    [Fact]
    public async Task A_photo_is_an_image_up_to_2_MB_seen_only_by_the_org_and_removable()
    {
        var ct = AtharApp.Timeout();
        var profiles = Profiles();
        Assert.Equal("not_an_image", await profiles.SetAvatarAsync(QaSeed.Org, QaSeed.Member, new MemoryStream("<svg/>"u8.ToArray()), ct));
        Assert.Equal("too_large", await profiles.SetAvatarAsync(QaSeed.Org, QaSeed.Member, new MemoryStream([.. Png, .. new byte[ProfileService.MaxAvatarBytes]]), ct));
        Assert.Null(await profiles.SetAvatarAsync(QaSeed.Org, QaSeed.Member, new MemoryStream(Png), ct));

        var photo = await profiles.OpenAvatarAsync(QaSeed.Org, QaSeed.Member, ct);
        Assert.Equal("image/png", photo!.Value.Type);
        photo.Value.Content.Dispose();
        Assert.Null(await profiles.OpenAvatarAsync(QaSeed.OtherOrg, QaSeed.Member, ct)); // another org can't load it
        Assert.True((await profiles.GetAsync(QaSeed.Org, QaSeed.Member, ct))!.HasAvatar);

        // Over HTTP: a signed-in member gets it; without a photo it's a 404 (the component shows initials); signed out, no.
        using var browser = new Browser(athar.App.GetEndpoint("web", "https"));
        await browser.SubmitAsync("/account/login", new() { ["Input.Email"] = QaSeed.Email("Qa Admin"), ["Input.Password"] = QaSeed.Password }, ct);
        var served = await browser.Http.GetAsync($"/avatars/{QaSeed.Member}", ct);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await served.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.Http.GetAsync($"/avatars/{QaSeed.Guest}", ct)).StatusCode);
        using var anonymous = new Browser(athar.App.GetEndpoint("web", "https"));
        Assert.NotEqual(HttpStatusCode.OK, (await anonymous.Http.GetAsync($"/avatars/{QaSeed.Member}", ct)).StatusCode);

        await profiles.RemoveAvatarAsync(QaSeed.Org, QaSeed.Member, ct);
        Assert.Null(await profiles.OpenAvatarAsync(QaSeed.Org, QaSeed.Member, ct));

        await using var db = athar.Db(QaSeed.Org);
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "user.avatar" && e.ResourceId == QaSeed.Member.ToString(), ct));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Action == "user.avatar_remove" && e.ResourceId == QaSeed.Member.ToString(), ct));
    }

    [Fact]
    public async Task People_change_their_display_name()
    {
        var ct = AtharApp.Timeout();
        var profiles = Profiles();
        Assert.False(await profiles.SetDisplayNameAsync(QaSeed.Org, QaSeed.LabViewer, "  ", ct));
        Assert.True(await profiles.SetDisplayNameAsync(QaSeed.Org, QaSeed.LabViewer, " Lab Viewer Renamed ", ct));
        Assert.Equal("Lab Viewer Renamed", (await profiles.GetAsync(QaSeed.Org, QaSeed.LabViewer, ct))!.DisplayName);
        await profiles.SetDisplayNameAsync(QaSeed.Org, QaSeed.LabViewer, "Lab Viewer", ct); // back, for the other tests
    }
}
