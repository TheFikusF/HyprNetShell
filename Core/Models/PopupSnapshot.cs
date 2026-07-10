namespace HyprNetShell.Core.Models;

public sealed record PopupSnapshot(
    string Id,
    string Title,
    IReadOnlyList<PopupRowSnapshot> Rows);

public sealed record PopupRowSnapshot(
    string Label,
    PopupRowKind Kind = PopupRowKind.Action,
    bool Enabled = true,
    int? ActionId = null);

public enum PopupRowKind
{
    Action,
    Separator,
    Header,
}
