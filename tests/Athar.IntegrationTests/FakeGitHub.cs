using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Athar.IntegrationTests;

/// <summary>
/// The four GitHub REST calls the sync makes (repo, head commit, recursive tree, blob), served from memory for the
/// AppHost's worker (T070). Tests set a repo's files and which PATs may read it; anything else answers 401.
/// </summary>
public sealed class FakeGitHub : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _repos = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _tokens = new();
    public string Url { get; private set; } = "";

    private FakeGitHub(WebApplication app) => _app = app;

    public static async Task<FakeGitHub> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeGitHub(app);
        app.MapGet("/repos/{owner}/{name}", (string owner, string name, HttpContext http) =>
            fake.Authorized(http, owner, name) is { } r ? Results.Json(new { default_branch = "main" }) : Results.Unauthorized());
        app.MapGet("/repos/{owner}/{name}/commits/{branch}", (string owner, string name, HttpContext http) =>
            fake.Authorized(http, owner, name) is { } r ? Results.Json(new { sha = CommitSha(r) }) : Results.Unauthorized());
        app.MapGet("/repos/{owner}/{name}/git/trees/{sha}", (string owner, string name, HttpContext http) =>
            fake.Authorized(http, owner, name) is { } r
                ? Results.Json(new { truncated = false, tree = r.Select(f => new { path = f.Key, type = "blob", sha = BlobSha(f.Value), size = Encoding.UTF8.GetByteCount(f.Value) }) })
                : Results.Unauthorized());
        app.MapGet("/repos/{owner}/{name}/git/blobs/{sha}", (string owner, string name, string sha, HttpContext http) =>
            fake.Authorized(http, owner, name) is { } r && r.Values.FirstOrDefault(v => BlobSha(v) == sha) is { } content
                ? Results.Json(new { encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) })
                : Results.Unauthorized());
        await app.StartAsync();
        fake.Url = app.Urls.First();
        return fake;
    }

    /// <summary>Replaces a repo's files (path → content), which moves its head commit.</summary>
    public void SetRepo(string repo, Dictionary<string, string> files) => _repos[repo] = new(files);

    public void Grant(string token, string repo) => _tokens.AddOrUpdate(token, _ => [repo], (_, s) => { lock (s) s.Add(repo); return s; });
    public void Revoke(string token) => _tokens.TryRemove(token, out _);

    private Dictionary<string, string>? Authorized(HttpContext http, string owner, string name)
    {
        var repo = $"{owner}/{name}".ToLowerInvariant();
        var token = http.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
        return _tokens.TryGetValue(token, out var repos) && repos.Contains(repo) && _repos.TryGetValue(repo, out var files) ? files : null;
    }

    private static string BlobSha(string content) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(content)));
    private static string CommitSha(Dictionary<string, string> files) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("\n", files.OrderBy(f => f.Key).Select(f => f.Key + ":" + BlobSha(f.Value))))));

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
