using Athar.Data;

namespace Athar.Web.Services;

/// <summary>
/// An optional second check of a drafted answer against the evidence it cites (an edition or plug-in registers one).
/// Without one, the drafted answer is returned after Athar's own evidence and access checks.
/// </summary>
public interface IAnswerVerifier
{
    /// <summary>
    /// Throws <see cref="AtharError"/> when the answer may not be returned. <paramref name="reauthorize"/> confirms the reader may
    /// still see every grounding item; call it before the cited text or the draft is sent anywhere.
    /// </summary>
    Task CheckAsync(AiModelOperation operation, string answer, string[] cited, Func<Task> reauthorize, CancellationToken ct);
}
