using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Aspire.Hosting;
using Athar.Data.Seed;
using Athar.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Athar.IntegrationTests;

/// <summary>
/// Collab acceptance tests (T004, M-B1). Real Yjs clients (`src/collab/scripts/at-clients.mjs`) talk to the collab sidecar
/// and web's internal API under the shared AppHost. Each test edits its own page, created through <see cref="PageService"/>.
/// </summary>
[Collection(AtharCollection.Name)]
public class CollabTests(AtharApp athar)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [Fact]
    public Task AT_10_viewer_forged_update_is_rejected() => RunClients("at10");

    [Fact]
    public Task AT_11_revision_lists_every_contributor_and_agent_credential() => RunClients("at11");

    [Fact]
    public Task AT_12_stale_store_409_is_merged_and_retried() => RunClients("at12-merge");

    [Fact]
    public Task AT_12_collab_restart_keeps_persisted_data_and_resyncs() => RunClients("at12-restart", async ct =>
    {
        var restarted = await athar.App.ResourceCommands.ExecuteCommandAsync("collab", KnownResourceCommands.RestartCommand, ct);
        Assert.True(restarted.Success, restarted.Message);
        await athar.App.ResourceNotifications.WaitForResourceHealthyAsync("collab", ct);
    });

    /// <summary>M-B1: `POST collab:/documents/{id}/ops` applies append and replace serially, checks the live token and persists before answering.</summary>
    [Fact]
    public Task M_B1_ops_endpoint_appends_and_replaces_with_a_live_token() => RunClients("ops");

    /// <summary>T016 (FR-PG-04, docs/06 §6.7): every block, mark and directive survives Markdown → Yjs → the persisted projection, and `/convert` both ways.</summary>
    [Fact]
    public async Task T016_markdown_round_trips_every_block_and_mark()
    {
        var ct = AtharApp.Timeout();
        var sample = (await File.ReadAllTextAsync(Path.Combine(CollabDir, "scripts", "markdown-sample.md"), ct)).ReplaceLineEndings("\n").TrimEnd();

        var page = await RunClients("markdown");
        await using (var db = athar.Db(QaSeed.Org))
            Assert.Equal(sample, (await db.Pages.SingleAsync(p => p.Id == page, ct)).ContentMd);

        using var http = new HttpClient { BaseAddress = athar.App.GetEndpoint("collab", "http") };
        http.DefaultRequestHeaders.Add("x-collab-secret", await Secret(ct));
        var json = (await (await http.PostAsJsonAsync("/convert", new { markdown = sample }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("json");
        // Images are block nodes in the editor's schema; inside a paragraph the editor would drop them.
        Assert.Contains(json.GetProperty("content").EnumerateArray(), n => n.GetProperty("type").GetString() == "image");
        var back = await (await http.PostAsJsonAsync("/convert", new { json }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(sample, back.GetProperty("markdown").GetString());
    }

    private async Task<Guid> RunClients(string scenario, Func<CancellationToken, Task>? restart = null)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var page = await new PageService(athar.Db(QaSeed.Org), TimeProvider.System)
            .CreateAsync(new(QaSeed.Admin, QaSeed.Org), QaSeed.EngWiki, null, $"collab {scenario}", cts.Token);
        var collab = athar.App.GetEndpoint("collab", "http").ToString().TrimEnd('/');
        var psi = new ProcessStartInfo("node", $"scripts/at-clients.mjs {scenario}")
        {
            WorkingDirectory = CollabDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["WEB_URL"] = athar.App.GetEndpoint("web", "http").ToString().TrimEnd('/');
        psi.Environment["COLLAB_HTTP"] = collab;
        psi.Environment["COLLAB_URL"] = collab.Replace("http://", "ws://");
        psi.Environment["COLLAB_SECRET"] = await Secret(cts.Token);
        psi.Environment["PAGE"] = page.ToString();
        psi.Environment["EDITOR"] = QaSeed.Admin.ToString();
        psi.Environment["VIEWER"] = QaSeed.Guest.ToString();
        psi.Environment["AGENT_USER"] = QaSeed.Member.ToString();
        psi.Environment["AGENT_CREDENTIAL"] = QaSeed.MemberPatId.ToString();

        using var node = Process.Start(psi)!;
        var output = new StringBuilder();
        var stderr = node.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            while (await node.StandardOutput.ReadLineAsync(cts.Token) is { } line)
            {
                output.AppendLine(line);
                if (line == "RESTART" && restart is not null)
                {
                    await restart(cts.Token);
                    await node.StandardInput.WriteLineAsync("go");
                    await node.StandardInput.FlushAsync();
                }
            }
            await node.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            node.Kill(entireProcessTree: true);
            throw;
        }
        Assert.True(node.ExitCode == 0, $"{scenario} failed:\n{output}{await stderr}");
        return page;
    }

    private async Task<string> Secret(CancellationToken ct) =>
        (await athar.App.Services.GetRequiredService<DistributedApplicationModel>().Resources
            .OfType<ParameterResource>().Single(r => r.Name == "collab-secret").GetValueAsync(ct))!;

    private static readonly string CollabDir = FindCollabDir();

    private static string FindCollabDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "collab", "package.json")))
                return Path.Combine(dir.FullName, "src", "collab");
        throw new DirectoryNotFoundException("src/collab not found above the test output directory");
    }
}
