namespace Athar.Web.Services;

/// <summary>
/// What an edition or plug-in adds to the Search page for the evidence kind its <see cref="IEvidenceReader"/> serves.
/// Register it scoped; texts are read per render, in the current UI language.
/// </summary>
public interface ISearchPageExtension
{
    /// <summary>The search kind, as in <see cref="IEvidenceReader.Kind"/>.</summary>
    string Kind { get; }

    /// <summary>The kind's filter label.</summary>
    string Label { get; }

    /// <summary>Replacements for the page's search-field label, empty-state help and result count. Null keeps Athar's text.</summary>
    string? SearchFor => null;
    string? Help => null;
    string? Count(int hits) => null;

    /// <summary>A component shown after one of this kind's answer sources. It receives the hit as the parameter <c>Hit</c>.</summary>
    Type? CitationComponent => null;
}
