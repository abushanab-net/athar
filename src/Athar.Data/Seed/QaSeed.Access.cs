using Athar.Domain.Model;

namespace Athar.Data.Seed;

/// <summary>
/// T012 access lab: a space in Engineering for the access matrix (AT-04, 05, 06, 49, 54). Only access tests rely on it.
/// <code>
/// LabRoot
/// ├─ LabRestricted        (restricted: view for LabEditor, edit for LabEditorsTeam)
/// │  └─ LabRestrictedChild (inherits the restriction)
/// └─ LabShared            (shared: comment for LabShareUser, edit for LabOutsider)
///    └─ LabSharedChild    (shares don't extend to children)
/// </code>
/// </summary>
public static partial class QaSeed
{
    public static readonly Guid AccessLab = Id(0x305);

    // Engineering workspace members with one space role each, direct or only through a team.
    public static readonly Guid LabViewer = Id(0x109);
    public static readonly Guid LabCommenter = Id(0x10a);
    public static readonly Guid LabEditor = Id(0x10b);
    public static readonly Guid LabAdmin = Id(0x10c);
    public static readonly Guid LabTeamViewer = Id(0x10d);
    public static readonly Guid LabTeamEditor = Id(0x10e);
    /// <summary>Has an editor role and an edit share, but isn't a workspace member: always denied.</summary>
    public static readonly Guid LabOutsider = Id(0x10f);
    /// <summary>A workspace member with no space role; only a direct share of <see cref="LabShared"/>.</summary>
    public static readonly Guid LabShareUser = Id(0x110);

    /// <summary>AT-08 revokes this PAT (read scope).</summary>
    public const string LabViewerPat = "athar_pat_qa_lab_viewer";
    public static readonly Guid LabViewerPatId = Id(0x407);

    public static readonly Guid LabViewersTeam = Id(0x553);
    public static readonly Guid LabEditorsTeam = Id(0x554);

    public static readonly Guid LabRoot = Id(0x711);
    public static readonly Guid LabRestricted = Id(0x712);
    public static readonly Guid LabRestrictedChild = Id(0x713);
    public static readonly Guid LabShared = Id(0x714);
    public static readonly Guid LabSharedChild = Id(0x715);

    private static void AddAccessLab(AtharDbContext db)
    {
        var n = 0x3000; // rows tests don't refer to
        Guid Next() => Id(n++);

        db.Spaces.Add(new Space { Id = AccessLab, OrgId = Org, WorkspaceId = Engineering, Name = "Access lab", Slug = "access-lab" });
        foreach (var user in new[] { LabViewer, LabCommenter, LabEditor, LabAdmin, LabTeamViewer, LabTeamEditor, LabShareUser })
            db.WorkspaceMembers.Add(new WorkspaceMember { Id = Next(), OrgId = Org, WorkspaceId = Engineering, UserId = user, Role = WorkspaceRole.Member });

        db.Teams.AddRange(
            new Team { Id = LabViewersTeam, OrgId = Org, Name = "Lab viewers" },
            new Team { Id = LabEditorsTeam, OrgId = Org, Name = "Lab editors" });
        db.TeamMembers.AddRange(
            new TeamMember { Id = Next(), OrgId = Org, TeamId = LabViewersTeam, UserId = LabTeamViewer },
            new TeamMember { Id = Next(), OrgId = Org, TeamId = LabEditorsTeam, UserId = LabTeamEditor });

        void Grant(Guid? user, Guid? team, SpaceRole role) =>
            db.SpaceMembers.Add(new SpaceMember { Id = Next(), OrgId = Org, SpaceId = AccessLab, UserId = user, TeamId = team, Role = role });
        Grant(LabViewer, null, SpaceRole.Viewer);
        Grant(LabCommenter, null, SpaceRole.Commenter);
        Grant(LabEditor, null, SpaceRole.Editor);
        Grant(LabAdmin, null, SpaceRole.Admin);
        Grant(null, LabViewersTeam, SpaceRole.Viewer);
        Grant(null, LabEditorsTeam, SpaceRole.Editor);
        Grant(LabOutsider, null, SpaceRole.Editor);

        void Page(Guid id, Guid? parent, string title) =>
            db.Pages.Add(new Page { Id = id, OrgId = Org, SpaceId = AccessLab, ParentId = parent, Position = 1, Title = title, ContentMd = title });
        Page(LabRoot, null, "Lab root");
        Page(LabRestricted, LabRoot, "Lab restricted");
        Page(LabRestrictedChild, LabRestricted, "Lab restricted child");
        Page(LabShared, LabRoot, "Lab shared");
        Page(LabSharedChild, LabShared, "Lab shared child");

        db.PageRestrictions.AddRange(
            new PageRestriction { Id = Next(), OrgId = Org, PageId = LabRestricted, UserId = LabEditor, Level = RestrictionLevel.View },
            new PageRestriction { Id = Next(), OrgId = Org, PageId = LabRestricted, TeamId = LabEditorsTeam, Level = RestrictionLevel.Edit });
        db.PageShares.AddRange(
            new PageShare { Id = Next(), OrgId = Org, PageId = LabShared, UserId = LabShareUser, Level = ShareLevel.Comment },
            new PageShare { Id = Next(), OrgId = Org, PageId = LabShared, UserId = LabOutsider, Level = ShareLevel.Edit });

        db.AccessTokens.Add(new AccessToken { Id = LabViewerPatId, OrgId = Org, UserId = LabViewer, Name = LabViewerPat, Scopes = ["read"], TokenHash = Hash(LabViewerPat) });
    }
}
