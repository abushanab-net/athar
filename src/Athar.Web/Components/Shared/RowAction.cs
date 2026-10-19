namespace Athar.Web.Components.Shared;

/// <summary>
/// One action on a table row or list item (see <see cref="RowActions"/>). <paramref name="Confirm"/> asks first, in the
/// row's own words; a <paramref name="Danger"/> action is shown in the error color and always sits in the menu.
/// <paramref name="Tooltip"/> explains the action, or why it is disabled.
/// </summary>
public sealed record RowAction(
    string Label,
    string Icon,
    Func<Task>? OnClick = null,
    string? Href = null,
    bool Danger = false,
    string? Confirm = null,
    bool Disabled = false,
    string? Tooltip = null);
