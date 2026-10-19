using Microsoft.Extensions.Configuration;

namespace Athar.Data;

/// <summary>
/// FR-PG-06 file storage: bytes under random keys in one folder that web (writes, serves) and the worker (extracts) share.
/// <c>Athar:Attachments:Path</c> sets it; locally both processes default to the same temp folder. Phase P points it at the
/// <c>attachments</c> volume.
/// </summary>
public sealed class AttachmentStore(IConfiguration config)
{
    private readonly string _root = config["Athar:Attachments:Path"] is { Length: > 0 } p ? p : Path.Combine(Path.GetTempPath(), "athar", "attachments");

    public static string NewKey() => Guid.NewGuid().ToString("N");

    /// <summary>The file for a key. Keys are our own GUIDs, so nothing from a client reaches the path.</summary>
    public string PathOf(string key) =>
        Guid.TryParseExact(key, "N", out _) ? Path.Combine(_root, key) : throw new ArgumentException("Not a storage key.", nameof(key));

    public async Task WriteAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(PathOf(key), bytes, ct);
    }

    public Stream Open(string key) => File.OpenRead(PathOf(key));
}
