using Athar.Data.Seed;
using Athar.Domain.Memory;
using Athar.Domain.Model;
using Athar.Worker.Jobs.Embed;
using Athar.Worker.Jobs.Extract;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace Athar.IntegrationTests;

/// <summary>T139 (FR-AG-09): the <c>save-chat</c> prompt and <c>save_chat</c> tool store a chat as a session that extraction reads.</summary>
[Collection(AtharCollection.Name)]
public class SaveChatTests(AtharApp athar)
{
    private sealed class Fixed(params ExtractedCandidate[] candidates) : ICandidateExtractor
    {
        public Task<IReadOnlyList<ExtractedCandidate>> ExtractAsync(string transcript, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExtractedCandidate>>(candidates);
    }

    [Fact]
    public async Task AT_63_save_chat_stores_a_chat_session_in_its_project_and_extracts_from_it()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;
        using var http = athar.Web();
        await using var member = await Mcp.Connect(http, new Uri(http.BaseAddress!, "/mcp"), QaSeed.MemberPat, ct);

        // The prompt: the repo's project when it resolves, else a pick list of the user's own projects.
        static string Text(GetPromptResult r) => string.Join("\n", r.Messages.Select(m => m.Content).OfType<TextContentBlock>().Select(t => t.Text));
        var fromRepo = Text(await member.GetPromptAsync("save-chat", new Dictionary<string, object?> { ["repo_remote"] = "git@github.com:abushanab-net/athar.git" }, cancellationToken: ct));
        Assert.Contains($"project_id `{QaSeed.PilotProject}`", fromRepo);
        var pick = Text(await member.GetPromptAsync("save-chat", new Dictionary<string, object?>(), cancellationToken: ct));
        Assert.Contains($"Athar pilot: `{QaSeed.PilotProject}`", pick);
        Assert.DoesNotContain(QaSeed.BillingProject.ToString(), pick); // not the member's project
        Assert.Contains("save_chat", pick);

        var op = Guid.NewGuid().ToString();
        Dictionary<string, object?> Args(string summary) => new()
        {
            ["title"] = "Release checklist review", ["summary_md"] = summary, ["project_id"] = QaSeed.PilotProject.ToString(), ["op_id"] = op,
            ["key_points"] = new[] { "Releases are tagged from develop", "The changelog is written before tagging" },
        };
        var saved = await Mcp.Call(member, "save_chat", Args("We walked through the release checklist."), ct);
        Assert.Equal("saved", saved.Str("result"));
        var id = Guid.Parse(saved.Str("session_id"));
        Assert.Equal(id.ToString(), (await Mcp.Call(member, "save_chat", Args("We walked through the release checklist."), ct)).Str("session_id")); // a retry
        Assert.Contains("op_id_reused", await Mcp.CallError(member, "save_chat", Args("Something else entirely."), ct));

        // Someone else's project, a read-only token, and Pause are refused.
        var other = Args("x");
        (other["op_id"], other["project_id"]) = (Guid.NewGuid().ToString(), QaSeed.BillingProject.ToString());
        Assert.Contains("not_found", await Mcp.CallError(member, "save_chat", other, ct));
        object Body() => new { title = "x", summary_md = "x", key_points = Array.Empty<string>(), op_id = Guid.NewGuid().ToString() };
        Assert.Equal(HttpStatusCode.Forbidden, (await Mcp.Post(http, "/api/v1/sessions/chat", QaSeed.MemberReadPat, Body(), ct)).Status);
        try
        {
            await using (var db = athar.Db(QaSeed.Org))
            {
                db.CaptureSettings.Add(new CaptureSettings { OrgId = QaSeed.Org, UserId = QaSeed.Member, Paused = true });
                await db.SaveChangesAsync(ct);
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await Mcp.Post(http, "/api/v1/sessions/chat", QaSeed.MemberPat, Body(), ct)).Status);
        }
        finally
        {
            await using var cleanup = athar.Db(QaSeed.Org);
            await cleanup.CaptureSettings.Where(s => s.UserId == QaSeed.Member).ExecuteDeleteAsync(CancellationToken.None);
        }

        Job job;
        await using (var db = athar.Db(QaSeed.Org))
        {
            var s = await db.AgentSessions.SingleAsync(x => x.Id == id, ct);
            Assert.Equal((SessionSource.Chat, "Release checklist review", QaSeed.PilotProject, 2), (s.Source, s.Title, s.ProjectId!.Value, s.AckedIndex));
            Assert.Equal(3, await db.SessionRecords.CountAsync(r => r.SessionId == id, ct)); // the summary and two key points
            Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Action == "session.save_chat" && e.ResourceId == id.ToString(), ct));
            job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Kind == IndexJobs.ExtractSession && j.Key == $"{id}:2", ct);
        }

        // Extraction runs as for hook sessions; a project candidate lands in the chat's project, with no repo involved.
        await RunAsync(job, new Fixed(new ExtractedCandidate("Tag from develop", "Releases are tagged from develop.", "procedure", "project")), ct);
        await using (var db = athar.Db(QaSeed.Org))
        {
            var c = await db.CaptureCandidates.SingleAsync(x => x.SessionId == id, ct);
            Assert.Equal((Scope.Project, QaSeed.PilotProject), (c.SuggestedScope, c.SuggestedOwner!.Value));
        }
    }

    // ponytail: the same runner as CaptureTests.RunAsync; share it if a third test class needs one.
    private async Task RunAsync(Job job, ICandidateExtractor extractor, CancellationToken ct)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ollama"] = await athar.App.GetConnectionStringAsync("ollama", ct),
        }).Build();
        await using var db = athar.Db(QaSeed.Org);
        var embedder = new OllamaEmbedder(config, NullLogger<OllamaEmbedder>.Instance);
        await embedder.EnsureReadyAsync(ct);
        await new ExtractSessionJob(extractor, embedder).RunAsync(job, db, ct);
        await db.SaveChangesAsync(ct);
    }
}
