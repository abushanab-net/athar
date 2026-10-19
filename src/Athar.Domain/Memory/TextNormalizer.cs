using System.Text;

namespace Athar.Domain.Memory;

/// <summary>
/// The search copy's Arabic normalization (FR-SR-05, AT-37), applied identically at index and query time. Display text
/// and citations keep the original. Code (fenced blocks and inline backticks) is never touched.
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool fenced = false, inline = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '`')
            {
                if (string.CompareOrdinal(text, i, "```", 0, 3) == 0) { fenced = !fenced; sb.Append("```"); i += 2; continue; }
                if (!fenced) inline = !inline;
            }
            if (fenced || inline) { sb.Append(c); continue; }
            switch (c)
            {
                case 'أ' or 'إ' or 'آ' or 'ٱ': sb.Append('ا'); break;   // hamza and madda forms of alef
                case 'ة': sb.Append('ه'); break;                        // ta marbuta
                case 'ـ': break;                                        // tatweel
                case >= 'ً' and <= 'ٟ' or 'ٰ': break;    // harakat, tanwin, shadda, sukun, dagger alef
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
