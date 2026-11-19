namespace Athar.Web.Components.Layout;

/// <summary>
/// A component that an edition or plug-in adds to a named place in the shell. Register instances in the service
/// collection; the shell renders each one, without parameters, in <see cref="Order"/>. The component decides for itself
/// what the current user may see.
/// </summary>
public sealed record ShellContribution(string Slot, Type Component, int Order = 0)
{
    /// <summary>The main navigation, after Wiki.</summary>
    public const string MainNavigation = "nav.main";

    /// <summary>The administration group, after AI models (administrators only).</summary>
    public const string AdminAiNavigation = "nav.admin.ai";
}
