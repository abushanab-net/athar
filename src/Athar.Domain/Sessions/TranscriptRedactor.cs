using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Athar.Domain.Sessions;

/// <summary>
/// FR-AG-04 server-side redaction of committed transcript records, the same rules as the client hook
/// (<c>agent-skills/hooks/athar-hook.mjs</c>): common token formats, key=value secrets and secret-named JSON keys.
/// It's a pattern detector, not a guarantee; the UI says so. Keep the two lists in step.
/// </summary>
public static partial class TranscriptRedactor
{
    public const string Mask = "[REDACTED]";

    [GeneratedRegex("^(password|passwd|pwd|secret|client_secret|token|access_token|refresh_token|api[_-]?key|authorization|cookie)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKey();

    private static readonly Regex[] Tokens =
    [
        new(@"\bathar_pat_[A-Za-z0-9_-]{16,}"), new(@"\bgh[pousr]_[A-Za-z0-9]{20,}"), new(@"\bsk-[A-Za-z0-9_-]{20,}"), new(@"\bAKIA[0-9A-Z]{16}\b"),
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----"),
    ];

    [GeneratedRegex(@"\b(?<k>password|passwd|secret|token|api[_-]?key)(?<sep>\s*[:=]\s*)[^\s""',;]+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValue();

    public static string RedactString(string s)
    {
        foreach (var re in Tokens) s = re.Replace(s, Mask);
        return KeyValue().Replace(s, m => m.Groups["k"].Value + m.Groups["sep"].Value + Mask);
    }

    /// <summary>A redacted copy of <paramref name="node"/>; the input is left untouched.</summary>
    public static JsonNode? Redact(JsonNode? node, string key = "") => node switch
    {
        JsonValue v when key.Length > 0 && SecretKey().IsMatch(key) && v.GetValueKind() is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number
            => JsonValue.Create(Mask),
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => JsonValue.Create(RedactString(v.GetValue<string>())),
        JsonArray a => new JsonArray([.. a.Select(x => Redact(x))]),
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Redact(p.Value, p.Key)))),
        _ => node?.DeepClone(),
    };
}
