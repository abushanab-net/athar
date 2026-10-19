using System.Diagnostics;
using Aspire.Hosting;
using Athar.Data.Seed;
using Athar.Web.CollabBridge;
using Athar.Web.Services;

namespace Athar.IntegrationTests;

/// <summary>Services and live Yjs clients for the wiki tests, wired to the AppHost's web and collab.</summary>
public static class PageClient
{
    public static async Task<string> SecretAsync(this AtharApp athar) =>
        (await athar.App.Services.GetRequiredService<DistributedApplicationModel>().Resources
            .OfType<ParameterResource>().Single(r => r.Name == "collab-secret").GetValueAsync(default))!;

    /// <summary>Page ops against the real collab; <paramref name="handler"/> can fake a lost response or an outage.</summary>
    public static async Task<PageOpsService> OpsAsync(this AtharApp athar, HttpMessageHandler? handler = null, Data.AtharDbContext? db = null)
    {
        var http = new HttpClient(handler ?? new HttpClientHandler()) { BaseAddress = athar.App.GetEndpoint("collab", "http") };
        http.DefaultRequestHeaders.Add("x-collab-secret", await athar.SecretAsync());
        db ??= athar.Db(QaSeed.Org);
        return new PageOpsService(db, new CollabClient(http), new PageService(db, TimeProvider.System));
    }

    /// <summary>Starts `src/collab/scripts/page-client.mjs` as <paramref name="user"/> at <paramref name="level"/>.</summary>
    public static Process Start(this AtharApp athar, Guid page, Guid user, string level, params string[] args)
    {
        var psi = new ProcessStartInfo("node")
        {
            WorkingDirectory = CollabDir, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("scripts/page-client.mjs");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var collab = athar.App.GetEndpoint("collab", "http").ToString().TrimEnd('/');
        psi.Environment["WEB_URL"] = athar.App.GetEndpoint("web", "http").ToString().TrimEnd('/');
        psi.Environment["COLLAB_URL"] = collab.Replace("http://", "ws://");
        psi.Environment["COLLAB_SECRET"] = athar.SecretAsync().GetAwaiter().GetResult();
        psi.Environment["PAGE"] = page.ToString();
        psi.Environment["USER_ID"] = user.ToString();
        psi.Environment["LEVEL"] = level;
        return Process.Start(psi)!;
    }

    /// <summary>Runs a one-shot command and returns its output; fails the test if it fails.</summary>
    public static async Task<string> RunAsync(this AtharApp athar, Guid page, Guid user, string level, CancellationToken ct, params string[] args)
    {
        using var p = athar.Start(page, user, level, args);
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        Assert.True(p.ExitCode == 0, await p.StandardError.ReadToEndAsync(ct));
        return output.Trim();
    }

    /// <summary>Waits for a line on the client's output.</summary>
    public static async Task ExpectAsync(this Process p, string line, CancellationToken ct)
    {
        while (await p.StandardOutput.ReadLineAsync(ct) is { } l)
            if (l == line) return;
        throw new InvalidOperationException($"page-client ended before '{line}': {await p.StandardError.ReadToEndAsync(ct)}");
    }

    public static readonly string CollabDir = FindCollabDir();

    private static string FindCollabDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "collab", "package.json")))
                return Path.Combine(dir.FullName, "src", "collab");
        throw new DirectoryNotFoundException("src/collab not found above the test output directory");
    }
}
