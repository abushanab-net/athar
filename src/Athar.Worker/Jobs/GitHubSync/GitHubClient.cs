using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Athar.Worker.Jobs.GitHubSync;

/// <summary>The PAT can't read the repo any more (revoked, expired or narrowed): the source pauses (FR-KS-03).</summary>
public sealed class GitHubAuthException(string message) : Exception(message);

public sealed record GitHubTreeItem(string Path, string Type, string Sha, long Size);

/// <summary>
/// The four GitHub REST calls the sync needs, with the source's PAT on each. <c>Athar:GitHub:ApiUrl</c> overrides the API
/// (tests point it at a fake). 401, 403 (other than rate limiting) and 404 on the repo all mean the PAT has no access.
/// </summary>
public sealed class GitHubClient(IHttpClientFactory http, IConfiguration config)
{
    public const string HttpClientName = "github";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<string> DefaultBranchAsync(string repo, string pat, CancellationToken ct) =>
        (await GetAsync<RepoInfo>($"repos/{repo}", pat, ct)).DefaultBranch;

    public async Task<string> HeadAsync(string repo, string branch, string pat, CancellationToken ct) =>
        (await GetAsync<CommitInfo>($"repos/{repo}/commits/{Uri.EscapeDataString(branch)}", pat, ct)).Sha;

    public async Task<IReadOnlyList<GitHubTreeItem>> TreeAsync(string repo, string sha, string pat, CancellationToken ct)
    {
        var tree = await GetAsync<TreeInfo>($"repos/{repo}/git/trees/{sha}?recursive=1", pat, ct);
        return [.. tree.Tree.Where(t => t.Type == "blob")];
    }

    public async Task<string> BlobAsync(string repo, string blobSha, string pat, CancellationToken ct)
    {
        var blob = await GetAsync<BlobInfo>($"repos/{repo}/git/blobs/{blobSha}", pat, ct);
        return blob.Encoding == "base64" ? Encoding.UTF8.GetString(Convert.FromBase64String(blob.Content.Replace("\n", ""))) : blob.Content;
    }

    private async Task<T> GetAsync<T>(string path, string pat, CancellationToken ct)
    {
        var client = http.CreateClient(HttpClientName);
        client.BaseAddress ??= new Uri(config["Athar:GitHub:ApiUrl"] is { Length: > 0 } u ? u.TrimEnd('/') + "/" : "https://api.github.com/");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("athar-sync");
        using var res = await client.SendAsync(request, ct);
        var rateLimited = res.StatusCode == HttpStatusCode.Forbidden && res.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0";
        if (!rateLimited && res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new GitHubAuthException($"GitHub answered {(int)res.StatusCode} for {path}.");
        res.EnsureSuccessStatusCode(); // anything else (rate limit, 5xx) is retried by the job runner
        return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private sealed record RepoInfo(string DefaultBranch);
    private sealed record CommitInfo(string Sha);
    private sealed record TreeInfo(List<GitHubTreeItem> Tree, bool Truncated);
    private sealed record BlobInfo(string Content, string Encoding);
}
