using Athar.Web.Mcp;

namespace Athar.Web.Services;

/// <summary>
/// The work-waiting counts in the sidebar and the Review tabs, one per circuit (T150). The layout loads them, pages that
/// change them (accept, discard, approve, reject) call <see cref="RefreshAsync"/>, and the bell's 30 s poll refreshes them too,
/// so a capture by an agent or a decision by another owner shows up without a reload.
/// </summary>
public sealed class ShellCounts(IServiceScopeFactory scopes)
{
    private Actor? actor;

    public int Inbox { get; private set; }
    /// <summary>Open proposals: the queue a knowledge owner reviews, or the person's own still pending.</summary>
    public int Proposals { get; private set; }
    /// <summary>Open conflicts nobody has resolved yet, on memories the person can read both of.</summary>
    public int Conflicts { get; private set; }
    public int Review => Inbox + Proposals + Conflicts;

    public event Action? Changed;

    public async Task RefreshAsync(Actor? who = null, CancellationToken ct = default)
    {
        actor = who ?? actor;
        if (actor is null) return;
        // Own scope: the page under the layout queries at the same time, and a circuit's DbContext takes one query at a time.
        await using var scope = scopes.CreateAsyncScope();
        var memories = scope.ServiceProvider.GetRequiredService<MemoryService>();
        int inbox, proposals, conflicts;
        try
        {
            inbox = (await memories.InboxAsync(actor, ct)).Count;
            proposals = (await memories.ProposalsAsync(actor, null, ct)).Count(p => p.Status is "pending" or "needs_rereview");
            conflicts = (await memories.ConflictsAsync(actor, false, ct)).Count(c => c.Resolution is null);
        }
        catch (AtharError) { inbox = proposals = conflicts = 0; }
        if (inbox == Inbox && proposals == Proposals && conflicts == Conflicts) return;
        (Inbox, Proposals, Conflicts) = (inbox, proposals, conflicts);
        Changed?.Invoke();
    }
}
