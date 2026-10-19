using Athar.Domain.Model;

namespace Athar.Domain.Memory;

/// <summary>Projects (FR-PJ-01..02). Plain C#, no I/O.</summary>
public static class ProjectRules
{
    /// <summary>
    /// One spelling per repo: <c>git@github.com:Org/Repo.git</c>, <c>https://github.com/org/repo</c> and
    /// <c>ssh://git@github.com/org/repo.git/</c> all become <c>github.com/org/repo</c>. Null if it isn't a remote.
    /// </summary>
    public static string? NormalizeRemote(string? remote)
    {
        var r = remote?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(r)) return null;
        if (r.Contains("://")) r = r[(r.IndexOf("://", StringComparison.Ordinal) + 3)..];
        else if (r.IndexOf(':') is var colon and > 0 && !r[..colon].Contains('/')) r = r[..colon] + "/" + r[(colon + 1)..]; // scp-like git@host:path
        r = r[(r.IndexOf('@') + 1)..].TrimEnd('/');
        if (r.EndsWith(".git", StringComparison.Ordinal)) r = r[..^4];
        var slash = r.IndexOf('/');
        if (slash <= 0 || slash == r.Length - 1) return null;
        var host = r[..slash].Split(':')[0]; // drop a port
        return host + r[slash..];
    }

    /// <summary>Org owners and admins create projects and manage any of them; project admins manage their own.</summary>
    public static bool CanCreate(OrgRole role) => role is OrgRole.Owner or OrgRole.Admin;
    public static bool CanManage(OrgRole role, ProjectRole? projectRole) => CanCreate(role) || projectRole == ProjectRole.Admin;
}
