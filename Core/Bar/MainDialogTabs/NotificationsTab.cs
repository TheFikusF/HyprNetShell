using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Modules.CenterWidgets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class NotificationsTab(NotificationService service, TextInputCoordinator inputs) : IMainDialogTab
{
    private readonly NotificationsWidget _widget = new(service);
    private readonly TextInputCoordinator.Input _queryInput = inputs.Create(
        "", "", "Search notifications...", 4096, alwaysActive: true);
    private string _query = "";

    public string Id => "notifications";
    public string Title => "Notifications";
    public SvgAsset Icon => Icons.Bell;
    public bool HandleScroll => false;

    public void Activate()
    {
        inputs.Configure(_queryInput, query =>
        {
            _query = query.Trim();
            _widget.ResetViewport();
        });
        inputs.Activate(_queryInput);
    }

    public void MoveSelection(SelectionDirection direction)
    {
        if (direction is SelectionDirection.Up or SelectionDirection.Down)
        {
            _widget.MoveViewport(direction == SelectionDirection.Up ? -1 : 1);
        }
    }

    public void ActivateSelection() { }

    public Node Draw() => new BoxNode {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = Style.Spacer,
        Children =
        [
            inputs.Build(_queryInput),
            _widget.Draw(service.Snapshot, _query, constrainWidth: false),
        ],
    };
}
