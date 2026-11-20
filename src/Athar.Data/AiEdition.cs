using Pgvector;

namespace Athar.Data;

/// <summary>
/// Whether every model call must belong to an admitted operation. Athar on its own does not require it. An edition that meters
/// or limits AI use installs a policy once, before any request is served; from then on a call without an admitted operation is
/// refused instead of running unmetered.
/// </summary>
public static class AiOperationPolicy
{
    private sealed record Installed(Func<AtharDbContext, AiModelOperation> Create, Func<AiModelOperation, bool> Admitted, Func<Exception> Missing);
    private static volatile Installed? installed;

    public static void Install(Func<AtharDbContext, AiModelOperation> create, Func<AiModelOperation, bool> admitted, Func<Exception> missing) =>
        installed = new(create, admitted, missing);

    public static bool Strict => installed is not null;

    /// <summary>The operation for a unit of work: the edition's admitted kind when a policy is installed.</summary>
    public static AiModelOperation Create(AtharDbContext work) => installed?.Create(work) ?? new AiModelOperation(work);

    /// <summary>Under a policy, throws unless <paramref name="operation"/> is one the edition admitted. Null means there is none.</summary>
    public static void Require(AiModelOperation? operation)
    {
        if (installed is { } policy && (operation is null || !policy.Admitted(operation))) throw policy.Missing();
    }

    /// <summary>The refusal for a call that has no admitted operation.</summary>
    public static Exception Missing() => installed?.Missing() ?? new InvalidOperationException("ai_operation_missing");

    /// <summary>The admission for a snapshot built outside any operation: refuses under a policy, absent otherwise.</summary>
    public static Func<CancellationToken, Task>? Unadmitted => installed is { } policy ? _ => throw policy.Missing() : null;
}

public sealed record VectorHit(Guid ChunkId, string OwnerKind, Guid OwnerId, double Score);

/// <summary>An edition's own store for the vectors of some embedding models; Athar's own queries serve the rest.</summary>
public interface IVectorIndex
{
    bool Handles(string modelId);

    /// <summary>The nearest chunks among the given owners, which the caller has already authorized. <c>OwnerKind</c> is the stored value.</summary>
    Task<IReadOnlyList<VectorHit>> NearestAsync(AtharDbContext db, Guid org, string modelId, Vector vector, Guid[] pages, Guid[] memories, Guid[] sources,
        int count, bool exact, CancellationToken ct);

    /// <summary>Among the given memories, those with a chunk at cosine similarity of at least <paramref name="threshold"/>, most similar first.</summary>
    Task<Guid[]> SimilarAsync(AtharDbContext db, Guid org, string modelId, Vector vector, Guid[] memories, double threshold, CancellationToken ct);
}

public static class VectorIndexes
{
    private static volatile IVectorIndex[] registered = [];
    private static readonly Lock Gate = new();

    public static void Register(IVectorIndex index) { lock (Gate) registered = [.. registered, index]; }

    public static IVectorIndex? For(string modelId) => registered.FirstOrDefault(i => i.Handles(modelId));
}
