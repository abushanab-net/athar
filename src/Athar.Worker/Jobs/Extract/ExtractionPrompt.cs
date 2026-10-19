using System.Text.RegularExpressions;

namespace Athar.Worker.Jobs.Extract;

/// <summary>
/// The extraction prompt shared by <see cref="OllamaExtractor"/> (local fallback) and <see cref="GatewayExtractor"/> (gateway):
/// the system message with the language rule, the session-specific language hint, the JSON schema, and the backstop that
/// drops a candidate written in a script the session doesn't use (docs/03 D8, T085).
/// </summary>
public static class ExtractionPrompt
{
    public const string System = """
        You turn a coding-agent session into memory candidates for a team knowledge base.
        Keep only durable knowledge: decisions, conventions, procedures, policies, preferences, incidents, facts and glossary terms.
        Skip small talk, one-off debugging steps, and anything secret such as tokens, keys or passwords.
        For each candidate give: a short title; a self-contained Markdown body; a type; a suggested scope (personal for the user's own
        preferences, project for this repository's conventions and decisions, team or company only when the session says so); and,
        as evidence, the exact words from the session it came from.
        Write every candidate in the language of its evidence. Arabic stays Arabic and English stays English. Never translate, and
        never answer in any other language.
        Return {"candidates": []} when there is nothing durable.
        """;

    /// <summary>
    /// The JSON schema both extractors ask for (strict for the gateway: every field required, nothing extra). Evidence comes
    /// first: structured output is generated in property order, so the model quotes its evidence before choosing the title's
    /// language (T091, with the mixed-session hint below).
    /// </summary>
    public static readonly object Schema = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "candidates" },
        properties = new
        {
            candidates = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "evidence", "title", "body_md", "type", "scope" },
                    properties = new Dictionary<string, object>
                    {
                        ["evidence"] = new { type = "string" },
                        ["title"] = new { type = "string" },
                        ["body_md"] = new { type = "string" },
                        ["type"] = new { type = "string", @enum = new[] { "fact", "decision", "convention", "procedure", "policy", "preference", "incident", "glossary" } },
                        ["scope"] = new { type = "string", @enum = new[] { "personal", "team", "project", "company" } },
                    },
                },
            },
        },
    };

    private static readonly Regex Arabic = new(@"\p{IsArabic}"),
        Foreign = new(@"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}\p{IsHangulSyllables}\p{IsCyrillic}]");

    /// <summary>
    /// The language rule made specific to this session. With only the general rule, qwen answered a one-line English session
    /// in Chinese (T085); naming the language fixed it. Any Arabic means mixed: role labels and code are always English.
    /// T091, mixed sessions on the gateway model (12 runs, each candidate in its evidence's language): the old hint with evidence
    /// last 0/12, evidence first 8/12, this hint alone 6/12, both 12/12.
    /// </summary>
    public static string LanguageHint(string transcript) => Arabic.IsMatch(transcript)
        ? "\nThis session mixes Arabic and English. Decide each candidate's language from its evidence alone: evidence in English "
          + "gives an English title and body; evidence in Arabic gives an Arabic title and body. Never translate between them, and use no other language."
        : "\nThis session is in English, so write every candidate in English.";

    /// <summary>The backstop: a candidate in a script the session doesn't use (Chinese, Cyrillic…) never reaches the inbox.</summary>
    public static bool InSessionLanguage(ExtractedCandidate c, string transcript) =>
        !Foreign.IsMatch(c.Title + c.BodyMd) || Foreign.IsMatch(transcript);
}
