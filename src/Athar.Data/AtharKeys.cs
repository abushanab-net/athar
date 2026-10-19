using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Athar.Data;

/// <summary>
/// One Data Protection key ring for web and worker (FR-KS-01): web encrypts a source's GitHub PAT, the worker decrypts it.
/// Keys live in <c>Athar:Keys:Path</c>, which is backed up separately from the database; locally both processes default to
/// the same per-user folder, and Phase P points it at the <c>keys</c> volume.
/// </summary>
public static class AtharKeys
{
    public const string GitHubPatPurpose = "Athar.KnowledgeSource.GitHubPat.v1";
    public const string AiApiKeyPurpose = "Athar.AiSettings.ApiKey.v1";

    public static IServiceCollection AddAtharKeys(this IServiceCollection services, IConfiguration config)
    {
        var path = config["Athar:Keys:Path"] is { Length: > 0 } p
            ? p : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "athar", "keys");
        services.AddDataProtection().SetApplicationName("athar").PersistKeysToFileSystem(new DirectoryInfo(path));
        return services;
    }
}
