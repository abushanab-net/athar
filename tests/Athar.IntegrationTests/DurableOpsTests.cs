using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Athar.Data.Seed;
using Athar.Domain.Model;
using Athar.Web.CollabBridge;
using Athar.Web.Mcp;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>AT-45 for page writes (FR-RT-07): ops are recorded before collab is called and reconciled from page_revisions.
/// Pages live in the Engineering wiki, whose AI policy allows agent writes.</summary>
[Collection(AtharCollection.Name)]
public class DurableOpsTests(AtharApp athar)
{
    private static readonly Actor Agent = new(QaSeed.Member, QaSeed.Org, QaSeed.MemberPatId, new HashSet<string> { "read", "write" }, DisclosureClass.ExternalAgent, null);
    private static readonly Actor OtherAgent = new(QaSeed.KnowledgeOwner, QaSeed.Org, QaSeed.KnowledgeOwnerPatId, new HashSet<string> { "read", "write" }, DisclosureClass.ExternalAgent, null);

    /// <summary>Sends to collab (or not), then loses the response.</summary>
    private sealed class LoseResponse(bool send, Func<Task>? afterSend = null) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (send) (await base.SendAsync(request, ct)).Dispose();
            if (afterSend is not null) await afterSend();
            throw new HttpRequestException("response lost");
        }
    }

    private async Task<PageOpsService> Ops(HttpMessageHandler? handler = null)
    {
        var http = new HttpClient(handler ?? new HttpClientHandler()) { BaseAddress = athar.App.GetEndpoint("collab", "http") };
        http.DefaultRequestHeaders.Add("x-collab-secret", await Secret());
        var db = athar.Db(QaSeed.Org);
        return new PageOpsService(db, new CollabClient(http), new PageService(db, TimeProvider.System));
    }

    private async Task<Guid> NewPage(string title) =>
        await new PageService(athar.Db(QaSeed.Org), TimeProvider.System).CreateAsync(new(QaSeed.Admin, QaSeed.Org), QaSeed.EngWiki, null, title, AtharApp.Timeout());

    private async Task<int> Occurrences(Guid page, string marker)
    {
        await using var db = athar.Db(QaSeed.Org);
        var md = (await db.Pages.SingleAsync(p => p.Id == page)).ContentMd ?? "";
        return md.Split(marker).Length - 1;
    }

    private static PageOp Append(string marker) => new("append", $"Line {marker}");

    [Fact]
    public async Task AT_45_lost_response_is_replayed_with_the_stored_result()
    {
        var ct = AtharApp.Timeout();
        var page = await NewPage("AT-45 lost response");
        var op = Guid.NewGuid().ToString();

        // Collab applied and persisted, the answer never arrived: the persist already marked the op done.
        var first = await (await Ops(new LoseResponse(send: true))).RunAsync(Agent, page, op, Append("lost-1"), ct);
        var replay = await (await Ops()).RunAsync(Agent, page, op, Append("lost-1"), ct);

        Assert.Equal(first.Revision, replay.Revision);
        Assert.Equal(1, await Occurrences(page, "lost-1"));
    }

    [Fact]
    public async Task AT_45_an_op_that_never_reached_collab_is_sent_on_retry()
    {
        var ct = AtharApp.Timeout();
        var page = await NewPage("AT-45 never sent");
        var op = Guid.NewGuid().ToString();

        var e = await Assert.ThrowsAsync<AtharError>(async () => await (await Ops(new LoseResponse(send: false))).RunAsync(Agent, page, op, Append("unsent-1"), ct));
        Assert.Equal("pending", e.Code);
        Assert.Equal("pending", (await (await Ops()).StatusAsync(Agent, op, ct)).Status);

        await (await Ops()).RunAsync(Agent, page, op, Append("unsent-1"), ct);
        Assert.Equal(1, await Occurrences(page, "unsent-1"));
        Assert.Equal("done", (await (await Ops()).StatusAsync(Agent, op, ct)).Status);
    }

    [Fact]
    public async Task AT_45_concurrent_duplicates_produce_one_effect()
    {
        var ct = AtharApp.Timeout();
        var page = await NewPage("AT-45 concurrent");
        var op = Guid.NewGuid().ToString();

        var calls = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try { return (await (await Ops()).RunAsync(Agent, page, op, Append("dup-1"), ct)).Revision; }
            catch (AtharError e) when (e.Code == "pending") { return 0; }
        }));

        Assert.Contains(calls, r => r > 0);
        Assert.Equal(1, await Occurrences(page, "dup-1"));
        Assert.Equal("done", (await (await Ops()).StatusAsync(Agent, op, ct)).Status);
    }

    [Fact]
    public async Task AT_45_reused_op_id_and_another_actor()
    {
        var ct = AtharApp.Timeout();
        var page = await NewPage("AT-45 reuse");
        var op = Guid.NewGuid().ToString();
        await (await Ops()).RunAsync(Agent, page, op, Append("reuse-1"), ct);

        var e = await Assert.ThrowsAsync<AtharError>(async () => await (await Ops()).RunAsync(Agent, page, op, Append("reuse-2"), ct));
        Assert.Equal("op_id_reused", e.Code);
        e = await Assert.ThrowsAsync<AtharError>(async () => await (await Ops()).RunAsync(OtherAgent, page, op, Append("reuse-1"), ct));
        Assert.Equal("not_found", e.Code);
        Assert.Equal(1, await Occurrences(page, "reuse-1"));

        // REST polling: the owner sees the outcome; another credential gets 404.
        using var web = athar.Web();
        web.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", QaSeed.MemberPat);
        var mine = await web.GetFromJsonAsync<JsonElement>($"/api/v1/operations/{op}", ct);
        Assert.Equal("done", mine.GetProperty("status").GetString());
        web.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", QaSeed.KnowledgeOwnerPat);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await web.GetAsync($"/api/v1/operations/{op}", ct)).StatusCode);
    }

    [Fact]
    public async Task AT_45_collab_restart_between_apply_and_response_is_reconciled()
    {
        var ct = AtharApp.Timeout();
        var page = await NewPage("AT-45 restart");
        var op = Guid.NewGuid().ToString();

        // Collab applies and persists, then restarts before web hears back.
        var lost = new LoseResponse(send: true, afterSend: async () =>
        {
            var restarted = await athar.App.ResourceCommands.ExecuteCommandAsync("collab", KnownResourceCommands.RestartCommand, ct);
            Assert.True(restarted.Success, restarted.Message);
            await athar.App.ResourceNotifications.WaitForResourceHealthyAsync("collab", ct);
        });
        var first = await (await Ops(lost)).RunAsync(Agent, page, op, Append("restart-1"), ct);

        // A retry against the new collab process gets the stored result and applies nothing again.
        var retry = await (await Ops()).RunAsync(Agent, page, op, Append("restart-1"), ct);
        Assert.Equal(first.Revision, retry.Revision);
        Assert.Equal(1, await Occurrences(page, "restart-1"));
    }

    private async Task<string> Secret() =>
        (await athar.App.Services.GetRequiredService<DistributedApplicationModel>().Resources
            .OfType<ParameterResource>().Single(r => r.Name == "collab-secret").GetValueAsync(default))!;
}
