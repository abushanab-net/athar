using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using System.Text;
using Athar.Data.Seed;
using Athar.Domain.Memory;
using Athar.Web.Services;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Athar.IntegrationTests;

/// <summary>T063 (FR-SK-01): the skills library over REST and MCP.</summary>
[Collection(AtharCollection.Name)]
public class SkillTests(AtharApp athar)
{
    [Fact]
    public async Task AT_38_company_skills_are_listed_personal_ones_stay_private_and_only_owners_publish_company()
    {
        var ct = AtharApp.Timeout();
        var tag = Guid.NewGuid().ToString("N")[..8];
        using var owner = Bearer(QaSeed.KnowledgeOwnerPat);
        using var member = Bearer(QaSeed.MemberPat);
        object Skill(string name, string scope) => new
        {
            name, scope, description = $"How to {name}", body_md = $"# {name}\n\nStep one.",
            files = new[] { new { path = "scripts/check.sh", content = "echo ok" } },
        };

        var company = await Publish(owner, Skill($"deploy-checklist-{tag}", "company"), HttpStatusCode.OK, ct);
        using (var pending = await member.PostAsJsonAsync("/api/v1/skills", Skill($"rogue-{tag}", "company"), ct)) // not a knowledge owner: a proposal (T141)
            Assert.Equal("pending", JsonDocument.Parse(await pending.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("status").GetString());
        var personal = await Publish(member, Skill($"my-notes-{tag}", "personal"), HttpStatusCode.OK, ct);
        Assert.Equal(personal, await Publish(member, Skill($"my-notes-{tag}", "personal"), HttpStatusCode.OK, ct)); // republish replaces

        // Over MCP, as an agent: the company skill is listed, the member's personal one isn't, archived ones never are
        await using var admin = await Connect(QaSeed.AdminPat, ct);
        var listed = (await Mcp.Call(admin, "list_skills", new(), ct)).GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("skill_id").GetGuid()).ToList();
        Assert.Contains(company, listed);
        Assert.Contains(QaSeed.SkillCompany, listed);
        Assert.DoesNotContain(personal, listed);
        Assert.DoesNotContain(QaSeed.SkillArchived, listed);
        using (var hidden = await Bearer(QaSeed.AdminPat).GetAsync($"/api/v1/skills/{personal}", ct)) Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        var detail = await Mcp.Call(admin, "get_skill", new() { ["skill_id"] = company }, ct);
        Assert.Equal("echo ok", detail.GetProperty("files")[0].GetProperty("content").GetString());
        var resource = await admin.ReadResourceAsync($"athar://skill/{company}", cancellationToken: ct);
        Assert.Contains($"name: deploy-checklist-{tag}", Assert.IsType<TextResourceContents>(resource.Contents[0]).Text);

        await using var mine = await Connect(QaSeed.MemberPat, ct);
        var own = (await Mcp.Call(mine, "list_skills", new() { ["query"] = $"my-notes-{tag}" }, ct)).GetProperty("skills");
        Assert.Equal(personal, Assert.Single(own.EnumerateArray()).GetProperty("skill_id").GetGuid());

        // Agents publish over MCP too (PM, 2026-10-04): a knowledge owner's agent publishes company skills, a member's proposes them (T141)
        await using var ownerAgent = await Connect(QaSeed.KnowledgeOwnerPat, ct);
        var viaMcp = await Mcp.Call(ownerAgent, "publish_skill", new() { ["name"] = $"incident-triage-{tag}", ["description"] = "Triage an incident",
            ["body_md"] = "# Triage", ["scope"] = "company", ["files"] = new[] { new { path = "notes.md", content = "x" } } }, ct);
        Assert.Equal(("company", "published"), (viaMcp.GetProperty("scope").GetString(), viaMcp.GetProperty("status").GetString()));
        var proposed = await Mcp.Call(mine, "publish_skill", new() { ["name"] = $"rogue2-{tag}", ["description"] = "x", ["body_md"] = "x", ["scope"] = "company" }, ct);
        Assert.Equal("pending", proposed.GetProperty("status").GetString());
        Assert.True(proposed.TryGetProperty("proposal_id", out _));
        Assert.Equal(JsonValueKind.Null, proposed.GetProperty("skill_id").ValueKind);
        var listed2 = (await Mcp.Call(admin, "list_skills", new(), ct)).GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("name").GetString());
        Assert.DoesNotContain($"rogue2-{tag}", listed2); // nothing is published before a knowledge owner approves
    }

    [Fact]
    public async Task AT_64_skill_packages_round_trip_as_zip_and_bad_uploads_are_refused()
    {
        // T140: the download is a skill folder; uploading it again gives back the same skill.
        var skill = new SkillDetail(Guid.NewGuid(), "pr-review", "How we review PRs", "personal", "# Review\n\nRead the diff.",
            [new("scripts/check.sh", "echo ok"), new("notes/أثر.md", "عربي")]);
        var back = SkillPackage.Read("pr-review.zip", SkillPackage.Zip(skill));
        Assert.Equal(("pr-review", "How we review PRs", "# Review\n\nRead the diff."), (back.Name, back.Description, back.BodyMd));
        Assert.Equal(skill.Files, back.Files);

        // A bare SKILL.md works too, with quoted front matter and Windows line endings.
        var md = SkillPackage.Read("SKILL.md", Encoding.UTF8.GetBytes("---\r\nname: \"tidy\"\r\ndescription: Keep it tidy\r\n---\r\n\r\nBody"));
        Assert.Equal(("tidy", "Keep it tidy", "Body", 0), (md.Name, md.Description, md.BodyMd, md.Files.Count));

        static string Refused(string name, byte[] bytes) => Assert.Throws<AtharError>(() => SkillPackage.Read(name, bytes)).Message;
        Assert.Contains("front matter", Refused("SKILL.md", "no front matter"u8.ToArray()));
        Assert.Contains("UTF-8", Refused("SKILL.md", [0xff, 0xfe, 0x00, 0x01]));
        Assert.Contains("readable zip", Refused("x.zip", "not a zip"u8.ToArray()));
        Assert.Contains("no SKILL.md", Refused("x.zip", Zip(("a/readme.md", "x"))));
        Assert.Contains("one folder", Refused("x.zip", Zip(("a/SKILL.md", "---\nname: a\n---\n"), ("b/other.md", "x"))));
        Assert.Contains("'..'", Refused("x.zip", Zip(("../SKILL.md", "x"))));
        Assert.Contains("1 MB", Refused("x.zip", new byte[SkillPackage.MaxUploadBytes + 1]));

        // Publish targets: the member's projects and teams, by name.
        await using var db = athar.Db(QaSeed.Org);
        var member = new Athar.Web.Mcp.Actor(QaSeed.Member, QaSeed.Org, null, new HashSet<string> { "read", "memory:write" }, Athar.Domain.Model.DisclosureClass.TrustedLocal, null);
        var skills = new SkillService(db);
        var targets = await skills.TargetsAsync(member, AtharApp.Timeout());
        Assert.Contains(targets, t => t.Scope == "project" && t.Id == QaSeed.PilotProject);

        // Version history: each publish is a revision; a seeded skill keeps its revision 1 and continues from it.
        var name = $"history-{Guid.NewGuid():N}"[..20];
        var first = await skills.PublishAsync(member, new(name, "v1", "# One", "personal", null, []), AtharApp.Timeout());
        await skills.PublishAsync(member, new(name, "v2", "# Two", "personal", null, []), AtharApp.Timeout());
        var history = await skills.HistoryAsync(member, first.SkillId, AtharApp.Timeout());
        Assert.Equal([(2, "v2", "# Two"), (1, "v1", "# One")], history.Select(v => (v.Revision, v.Description, v.BodyMd)));
        Assert.Equal("Qa Member", history[0].PublishedBy);
        Assert.Equal(1, (await skills.HistoryAsync(member, QaSeed.SkillProject, AtharApp.Timeout())).Single().Revision);
        var admin = member with { UserId = QaSeed.Admin };
        await Assert.ThrowsAsync<AtharError>(() => skills.HistoryAsync(admin, first.SkillId, AtharApp.Timeout())); // personal: owner only
    }

    [Fact]
    public async Task AT_64_company_skill_proposals_are_reviewed_and_approved_atomically()
    {
        // T141 (FR-SK-02): anyone proposes a company skill; a knowledge owner approves it in the review queue.
        var ct = AtharApp.Timeout();
        var name = $"proposal-{Guid.NewGuid():N}"[..20];
        using var member = Bearer(QaSeed.MemberPat);
        using var res = await member.PostAsJsonAsync("/api/v1/skills", new
        {
            name, scope = "company", description = "Proposed by a member", body_md = "# Do it well",
            files = new[] { new { path = "notes.md", content = "Extra notes" } },
        }, ct);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("pending", body.GetProperty("status").GetString());
        var proposalId = body.GetProperty("proposal_id").GetGuid();

        await using var db = athar.Db(QaSeed.Org);
        var memories = new MemoryService(db, new ProjectService(db), new SearchService(db, new QueryEmbedder(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), Microsoft.Extensions.Logging.Abstractions.NullLogger<QueryEmbedder>.Instance)));
        Athar.Web.Mcp.Actor Person(Guid user) => new(user, QaSeed.Org, null, new HashSet<string> { "read", "memory:write" }, Athar.Domain.Model.DisclosureClass.TrustedLocal, null);

        // The queue shows the package (SKILL.md and its files) as a skill proposal, the seeded one too.
        var queue = await memories.ProposalsAsync(Person(QaSeed.KnowledgeOwner), null, ct);
        var view = queue.Single(p => p.Id == proposalId);
        Assert.Equal(("skill", "pending", name), (view.Kind, view.Status, view.Proposed.Title));
        Assert.Contains("# Do it well", view.Proposed.BodyMd);
        Assert.Contains("## notes.md", view.Proposed.BodyMd);
        Assert.Contains(queue, p => p.Id == QaSeed.ProposalSkill && p.Kind == "skill");

        // Only a knowledge owner approves; approval publishes the company skill as the proposer, once.
        await Assert.ThrowsAsync<AtharError>(() => memories.ApproveAsync(Person(QaSeed.Member), proposalId, null, ct));
        var decision = await memories.ApproveAsync(Person(QaSeed.KnowledgeOwner), proposalId, null, ct);
        Assert.Equal(("approved", 1), (decision.Status, decision.Revision));
        await Assert.ThrowsAsync<AtharError>(() => memories.ApproveAsync(Person(QaSeed.KnowledgeOwner), proposalId, null, ct));

        var skills = new SkillService(db);
        var skill = await skills.GetAsync(Person(QaSeed.Member), decision.SkillId!.Value, ct);
        Assert.Equal((name, "company", "# Do it well"), (skill.Name, skill.Scope, skill.BodyMd));
        Assert.Equal("notes.md", skill.Files.Single().Path);
        Assert.Equal("Qa Member", (await skills.HistoryAsync(Person(QaSeed.Member), skill.SkillId, ct)).Single().PublishedBy);
        string[] ids = [skill.SkillId.ToString(), proposalId.ToString()];
        var audit = await db.AuditEvents.AsNoTracking().Where(a => ids.Contains(a.ResourceId)).Select(a => new { a.Action, ActorId = a.ActorUserId }).ToListAsync(ct);
        Assert.Contains(audit, a => a.Action == "proposal.create" && a.ActorId == QaSeed.Member);
        Assert.Contains(audit, a => a.Action == "skill.publish" && a.ActorId == QaSeed.KnowledgeOwner);
        Assert.Contains(audit, a => a.Action == "proposal.approve" && a.ActorId == QaSeed.KnowledgeOwner);
    }

    private static byte[] Zip(params (string Path, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, text) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(text);
            }
        return ms.ToArray();
    }

    private HttpClient Bearer(string pat)
    {
        var http = athar.Web();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        return http;
    }

    private static async Task<Guid> Publish(HttpClient http, object skill, HttpStatusCode expected, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync("/api/v1/skills", skill, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        Assert.True(res.StatusCode == expected, $"{res.StatusCode}: {body}");
        return expected == HttpStatusCode.OK ? JsonDocument.Parse(body).RootElement.GetProperty("skill_id").GetGuid() : Guid.Empty;
    }

    private Task<McpClient> Connect(string pat, CancellationToken ct) =>
        Mcp.Connect(athar.Web(), new Uri(athar.App.GetEndpoint("web", "https"), "/mcp"), pat, ct);
}
