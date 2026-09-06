using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class NotificationsWidget(NotificationService service, Theme theme)
{
    public const int WIDTH = CalendarWidget.WIDTH + 12 + WeatherWidget.WIDTH + 12 + WorldClocksWidget.WIDTH;
    private readonly Ref<float> _doNotDisturbSwitchAnimation = new(service.Snapshot.DoNotDisturb ? 1.0f : 0.0f);
    private readonly Dictionary<uint, NotificationCard.State> _cardStates = new();
    private readonly ModulesCommon.BoxState _clearButtonState = new();
    private bool _clearButtonInitialized;
    private HistoryDateRange _dateRange;
    private DropdownNode? _dateDropdown;

    public Node Draw(NotificationsSnapshot snapshot)
    {
        RemoveExpiredCardStates(snapshot.Items);
        var filteredItems = snapshot.Items
            .Where(notification => notification.StoreInHistory)
            .Where(notification => HistoryDateFilter.Includes(_dateRange, notification.ReceivedAt))
            .ToArray();

        return new BoxNode(WIDTH)
        {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Center,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = Style.Spacer,
        Children =
        [
            new BoxNode(new Style(), ItemsAlignment.Spread, ItemsAlignment.Center)
            {
                new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                {
                    new ImageNode(snapshot.DoNotDisturb ? Icons.BellOff : Icons.Bell, 22, 22, theme.Text),
                    new TextNode("Notifications", 22, theme.Text),
                },

                new BoxNode(new Style { Spacing = 16 }, verticalAlignment: ItemsAlignment.Center)
                {
                    BuildDateDropdown(),
                    new BoxNode(2, 18) { Style = new Style { BackgroundColor = theme.Border } },
                    BuildDoNotDisturbToggle(snapshot.DoNotDisturb),
                    new BoxNode(2, 18) { Style = new Style { BackgroundColor = theme.Border } },
                    new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                    {
                        new TextNode($"{filteredItems.Length}", 22, theme.Text),
                        BuildClearButton(snapshot.Count),
                    }
                }
            },
            ..BuildRows(filteredItems),
        ],
        };
    }

    private BoxNode BuildDoNotDisturbToggle(bool enabled) => new(height: 48)
    {
        HorizontalAlignment = ItemsAlignment.Spread,
        VerticalAlignment = ItemsAlignment.Center,
        OnClick = service.ToggleDoNotDisturb,
        Style = new Style { Spacing = 8 },
        Children =
        [
            new ImageNode(enabled ? Icons.BellOff : Icons.Bell, 18, 18, theme.Text),
            new SwitchNode(enabled, _doNotDisturbSwitchAnimation)
            {
                OffTrackColor = theme.Text.MutedColor,
                OnTrackColor = theme.Active,
                KnobColor = theme.Text,
            },
        ],
    };

    private IEnumerable<Node> BuildRows(IReadOnlyList<NotificationSnapshot> notifications)
    {
        if (notifications.Count == 0)
        {
            yield return new BoxNode(height: 64)
            {
                HorizontalAlignment = ItemsAlignment.Center,
                VerticalAlignment = ItemsAlignment.Center,
                Children = [new TextNode("No notifications", 18, theme.Text.MutedColor)]
            };
        }

        foreach (var notification in notifications.Take(5))
        {
            if (!_cardStates.TryGetValue(notification.Id, out var state))
            {
                state = new NotificationCard.State();
                _cardStates[notification.Id] = state;
            }

            yield return NotificationCard.Draw(notification, service, theme, state);
        }
    }

    private DropdownNode BuildDateDropdown()
    {
        _dateDropdown ??= new DropdownNode(
            140,
            HistoryDateFilter.Labels,
            (int)_dateRange,
            Icons.ChevronDown,
            Icons.Check,
            selected => _dateRange = (HistoryDateRange)selected)
        {
            FontSize = theme.Text,
            BackgroundColor = theme.Panel,
            HoverColor = Color.Lighten(theme.Panel, 0.18f),
            SelectedColor = theme.Active,
            BorderColor = theme.Border,
            BorderWidth = theme.Border.Width,
            BorderRadius = 8,
            TextColor = theme.Text,
        };
        _dateDropdown.SelectedIndex = (int)_dateRange;
        return _dateDropdown;
    }

    private void RemoveExpiredCardStates(IReadOnlyList<NotificationSnapshot> notifications)
    {
        var activeIds = notifications.Select(notification => notification.Id).ToHashSet();
        foreach (var id in _cardStates.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            _cardStates.Remove(id);
        }
    }

    private BoxNode BuildClearButton(int count)
    {
        if (!_clearButtonInitialized)
        {
            _clearButtonState.Background = theme.Text.MutedColor;
            _clearButtonInitialized = true;
        }
        _clearButtonState.UpdateColor(theme.Text.MutedColor);

        return new BoxNode
        {
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = count > 0 ? service.Clear : null,
            IsHovered = count > 0 ? _clearButtonState.Hovered : null,
            Opacity = count > 0 ? 1 : 0.45f,
            Style = new Style
            {
                BackgroundColor = _clearButtonState.Background,
                BorderRadius = 7,
                Padding = new Insets(8, 5),
                Spacing = 6,
            },
            Children =
            [
                new ImageNode(Icons.Trash, 18, 18, theme.Text),
                new TextNode("Clear", theme.Text, theme.Text),
            ],
        };
    }
}
