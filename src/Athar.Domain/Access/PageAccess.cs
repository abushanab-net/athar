using System.Linq.Expressions;
using Athar.Domain.Model;

namespace Athar.Domain.Access;

/// <summary>
/// Who is asking. <paramref name="Agent"/>: through a credential (PAT or OAuth), so the space AI policy applies (FR-AG-07).
/// <paramref name="External"/>: an external-agent credential, which never sees confidential content (FR-CL-01).
/// </summary>
public sealed record Caller(Guid UserId, bool External = false, bool Agent = false);

public enum PageAction { Read, Comment, Edit }

/// <summary>
/// The rows the predicates read. EF passes its DbSets (query filters keep them in the caller's org); decision methods
/// pass the few rows they loaded for one page. Both run the same expressions, so there is no second copy to drift (AT-54).
/// </summary>
public sealed record AccessSource(
    IQueryable<User> Users, IQueryable<OrgMember> OrgMembers, IQueryable<TeamMember> TeamMembers,
    IQueryable<WorkspaceMember> WorkspaceMembers, IQueryable<Space> Spaces, IQueryable<SpaceMember> SpaceMembers,
    IQueryable<Page> Pages, IQueryable<PageRestriction> Restrictions, IQueryable<PageShare> Shares);

/// <summary>
/// The one page-access implementation (FR-AC-01..02, docs/03 §5). For a page p and an active user u:
/// <list type="bullet">
/// <item>u is a member of p's workspace (prerequisite), an external caller only sees non-confidential spaces, and an agent
/// needs the space's AI policy: read to read, read and write to comment or edit (FR-WS-03, FR-AG-07);</item>
/// <item>grants are unioned: a space role on p's space, or a direct share of exactly p, to u or one of u's teams;</item>
/// <item>restrictions are intersected: every restricted page among p and its ancestors admits u (directly or via a team),
/// and for editing, through an edit-level restriction.</item>
/// </list>
/// Each rule is one expression with every input as a parameter: compiled once for decisions, bound to constants for EF.
/// Roles and levels match by set membership, not comparison, because enums are stored as text.
/// </summary>
public static class PageAccess
{
    private static readonly SpaceRole[][] RolesFor =
        [[SpaceRole.Viewer, SpaceRole.Commenter, SpaceRole.Editor, SpaceRole.Admin], [SpaceRole.Commenter, SpaceRole.Editor, SpaceRole.Admin], [SpaceRole.Editor, SpaceRole.Admin]];
    private static readonly ShareLevel[][] LevelsFor =
        [[ShareLevel.View, ShareLevel.Comment, ShareLevel.Edit], [ShareLevel.Comment, ShareLevel.Edit], [ShareLevel.Edit]];

    // Without drafts: roles, shares and restrictions for the action. CanRule adds the draft rule on top.
    private static readonly Expression<Func<AccessSource, Caller, PageAction, Page, bool>> BaseRule = (s, c, action, p) =>
        s.Users.Any(x => x.Id == c.UserId && x.DeactivatedAt == null) && s.OrgMembers.Any(m => m.UserId == c.UserId)
        && s.Spaces.Any(sp => sp.Id == p.SpaceId && (!c.External || !sp.IsConfidential)
            && (!c.Agent || sp.AiPolicy.Read && (action == PageAction.Read || sp.AiPolicy.Write))
            && s.WorkspaceMembers.Any(w => w.WorkspaceId == sp.WorkspaceId && w.UserId == c.UserId))
        && (s.SpaceMembers.Any(m => m.SpaceId == p.SpaceId
                && (m.UserId == c.UserId || s.TeamMembers.Any(t => t.UserId == c.UserId && (Guid?)t.TeamId == m.TeamId))
                && RolesFor[(int)action].Contains(m.Role))
            || s.Shares.Any(sh => sh.PageId == p.Id
                && (sh.UserId == c.UserId || s.TeamMembers.Any(t => t.UserId == c.UserId && (Guid?)t.TeamId == sh.TeamId))
                && LevelsFor[(int)action].Contains(sh.Level)))
        && !s.Restrictions.Any(r => (r.PageId == p.Id || p.AncestorIds.Contains(r.PageId))
            && !s.Restrictions.Any(a => a.PageId == r.PageId
                && (a.UserId == c.UserId || s.TeamMembers.Any(t => t.UserId == c.UserId && (Guid?)t.TeamId == a.TeamId))
                && (action != PageAction.Edit || a.Level == RestrictionLevel.Edit)));

    /// <summary>
    /// U-1 (Phase U): a draft is visible only to people who may edit it, for every action, and never to agents (MCP,
    /// REST, search, recall). Composed as base(action) && (published || (!agent && base(Edit))), one expression EF translates.
    /// </summary>
    private static readonly Expression<Func<AccessSource, Caller, PageAction, Page, bool>> CanRule = WithDrafts(BaseRule);

    private static Expression<Func<AccessSource, Caller, PageAction, Page, bool>> WithDrafts(Expression<Func<AccessSource, Caller, PageAction, Page, bool>> rule)
    {
        var (s, c, action, p) = (rule.Parameters[0], rule.Parameters[1], rule.Parameters[2], rule.Parameters[3]);
        var edit = Substitute(rule, s, c, Expression.Constant(PageAction.Edit), p);
        var published = Expression.NotEqual(Expression.Property(p, nameof(Page.Status)), Expression.Constant(PageStatus.Draft));
        var person = Expression.Not(Expression.Property(c, nameof(Caller.Agent)));
        return Expression.Lambda<Func<AccessSource, Caller, PageAction, Page, bool>>(
            Expression.AndAlso(rule.Body, Expression.OrElse(published, Expression.AndAlso(person, edit))), s, c, action, p);
    }

    /// <summary>
    /// AT-06: space admins (who are workspace members) and org owners/admins see that a restricted page they can't read
    /// exists, as a placeholder, and may take ownership. Never for external agents.
    /// </summary>
    private static readonly Expression<Func<AccessSource, Caller, Page, bool>> PlaceholderRule = Compose(
        (s, c, p) => !c.External && !c.Agent
            && s.Users.Any(x => x.Id == c.UserId && x.DeactivatedAt == null)
            && (s.OrgMembers.Any(m => m.UserId == c.UserId && (m.Role == OrgRole.Owner || m.Role == OrgRole.Admin))
                || s.Spaces.Any(sp => sp.Id == p.SpaceId && s.WorkspaceMembers.Any(w => w.WorkspaceId == sp.WorkspaceId && w.UserId == c.UserId))
                    && s.SpaceMembers.Any(m => m.SpaceId == p.SpaceId && m.Role == SpaceRole.Admin
                        && (m.UserId == c.UserId || s.TeamMembers.Any(t => t.UserId == c.UserId && (Guid?)t.TeamId == m.TeamId))))
            && s.Restrictions.Any(r => r.PageId == p.Id || p.AncestorIds.Contains(r.PageId)));

    // Decisions run over in-memory rows: the rules are rewritten to Enumerable calls and compiled once.
    private static readonly Func<AccessSource, Caller, PageAction, Page, bool> CanCompiled = InMemory(CanRule).Compile();
    private static readonly Func<AccessSource, Caller, Page, bool> PlaceholderCompiled = InMemory(PlaceholderRule).Compile();

    /// <summary>The filter for EF: pages the caller may act on.</summary>
    public static Expression<Func<Page, bool>> Can(AccessSource s, Caller c, PageAction action) => Bind(CanRule, s, c, action);

    /// <summary>The filter for EF: restricted pages the caller manages but can't read.</summary>
    public static Expression<Func<Page, bool>> SeesPlaceholder(AccessSource s, Caller c) => Bind(PlaceholderRule, s, c);

    /// <summary>The decision: the same rule, evaluated over the rows in <paramref name="s"/>.</summary>
    public static bool Decide(AccessSource s, Caller c, PageAction action, Page p) => CanCompiled(s, c, action, p);

    public static bool DecidePlaceholder(AccessSource s, Caller c, Page p) => PlaceholderCompiled(s, c, p);

    /// <summary>The restricted pages among p and its ancestors that don't admit the caller (what taking ownership must join).</summary>
    public static IQueryable<Guid> BlockingRestrictions(AccessSource s, Caller c, Page p)
    {
        var u = c.UserId;
        var teams = s.TeamMembers.Where(t => t.UserId == u).Select(t => (Guid?)t.TeamId);
        var chain = p.AncestorIds.Append(p.Id).ToArray();
        return s.Restrictions.Where(r => chain.Contains(r.PageId)).Select(r => r.PageId).Distinct()
            .Where(id => !s.Restrictions.Any(a => a.PageId == id && (a.UserId == u || teams.Contains(a.TeamId))));
    }

    // manager(s, c, p) && !CanRule(s, c, Read, p), as one expression EF can translate.
    private static Expression<Func<AccessSource, Caller, Page, bool>> Compose(Expression<Func<AccessSource, Caller, Page, bool>> manager)
    {
        var (s, c, p) = (manager.Parameters[0], manager.Parameters[1], manager.Parameters[2]);
        var canRead = Substitute(CanRule, s, c, Expression.Constant(PageAction.Read), p);
        return Expression.Lambda<Func<AccessSource, Caller, Page, bool>>(Expression.AndAlso(manager.Body, Expression.Not(canRead)), s, c, p);
    }

    private static Expression<Func<Page, bool>> Bind(LambdaExpression rule, params object[] values)
    {
        var page = rule.Parameters[^1];
        var args = values.Select((v, i) => (Expression)Expression.Constant(v, rule.Parameters[i].Type)).Append(page).ToArray();
        return Expression.Lambda<Func<Page, bool>>(Substitute(rule, args), page);
    }

    private static Expression Substitute(LambdaExpression lambda, params Expression[] args) =>
        new Replace(lambda.Parameters.Zip(args).ToDictionary(x => x.First, x => x.Second)).Visit(lambda.Body);

    private static Expression<T> InMemory<T>(Expression<T> rule) => (Expression<T>)new ToEnumerable().Visit(rule);

    /// <summary>
    /// Queryable.X(source, quoted lambda) becomes Enumerable.X(source, lambda). LINQ to Objects over AsQueryable() would
    /// otherwise compile every nested call on every evaluation.
    /// </summary>
    private sealed class ToEnumerable : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType != typeof(Queryable)) return base.VisitMethodCall(node);
            var args = node.Arguments.Select(a => Visit(a is UnaryExpression { NodeType: ExpressionType.Quote } q ? q.Operand : a)).ToArray();
            var generic = node.Method.GetGenericArguments();
            var target = typeof(Enumerable).GetMethods()
                .Where(m => m.Name == node.Method.Name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == generic.Length)
                .Select(m => m.MakeGenericMethod(generic))
                .Single(m => m.GetParameters() is var ps && ps.Length == args.Length && ps.Zip(args).All(x => x.First.ParameterType.IsAssignableFrom(x.Second.Type)));
            return Expression.Call(target, args);
        }
    }

    private sealed class Replace(Dictionary<ParameterExpression, Expression> map) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => map.GetValueOrDefault(node, node);
    }
}

/// <summary>Space-level access, with the same rules as <see cref="PageAccess"/>.</summary>
public static class SpaceAccess
{
    /// <summary>
    /// A space shows in navigation for workspace members with a space role or a direct share of one of its pages (FR-WS-01).
    /// External callers never see confidential spaces.
    /// </summary>
    public static Expression<Func<Space, bool>> CanSee(AccessSource s, Caller c)
    {
        var u = c.UserId;
        var (external, agent) = (c.External, c.Agent);
        var teams = s.TeamMembers.Where(t => t.UserId == u).Select(t => (Guid?)t.TeamId);
        return sp =>
            s.Users.Any(x => x.Id == u && x.DeactivatedAt == null) && s.OrgMembers.Any(m => m.UserId == u)
            && (!external || !sp.IsConfidential)
            && (!agent || sp.AiPolicy.Read)
            && s.WorkspaceMembers.Any(w => w.WorkspaceId == sp.WorkspaceId && w.UserId == u)
            && (s.SpaceMembers.Any(m => m.SpaceId == sp.Id && (m.UserId == u || teams.Contains(m.TeamId)))
                || s.Shares.Any(sh => (sh.UserId == u || teams.Contains(sh.TeamId)) && s.Pages.Any(pg => pg.Id == sh.PageId && pg.SpaceId == sp.Id)));
    }

    /// <summary>The caller's space roles, direct and through teams. The effective role is the highest one.</summary>
    public static IQueryable<SpaceRole> Roles(AccessSource s, Guid userId, Guid spaceId)
    {
        var teams = s.TeamMembers.Where(t => t.UserId == userId).Select(t => (Guid?)t.TeamId);
        return s.SpaceMembers.Where(m => m.SpaceId == spaceId && (m.UserId == userId || teams.Contains(m.TeamId))).Select(m => m.Role);
    }
}
