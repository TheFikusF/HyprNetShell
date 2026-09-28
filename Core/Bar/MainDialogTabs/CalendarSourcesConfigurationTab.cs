using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;

using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class CalendarSourcesConfigurationTab(
    CalendarService calendar,
    TextInputCoordinator inputs,
    Theme theme) : IMainDialogTab
{
    private const int URL_MAX_LENGTH = 2048;
    private const int URL_TEXT_MAX_WIDTH = 780;
    private const int VISIBLE_SOURCE_COUNT = 6;

    private readonly Dictionary<string, ModulesCommon.BoxState> _buttonStates = [];
    private readonly Dictionary<string, Ref<float>> _googleSwitchAnimations = [];
    private readonly TextInputCoordinator.Input _urlInput = inputs.Create(
        "Calendar URL",
        "",
        "https://example.com/calendar.ics",
        URL_MAX_LENGTH,
        transform: value => value.Trim(),
        clearOnEscape: true,
        pasteReplacesValue: true);
    private string _message = "";
    private bool _messageIsError;
    private int _firstSourceIndex;

    public string Id => "calendar-sources";
    public string Title => "Calendar sources";
    public SvgAsset Icon => Icons.Calendar;

    public void Activate()
    {
        inputs.Configure(_urlInput, _ => ClearMessage(), AddUrl);
        inputs.Activate(_urlInput);
    }



    public void MoveSelection(SelectionDirection direction)
    {
    }

    public void ActivateSelection()
    {
    }

    public Node Draw()
    {
        var urls = calendar.Urls;
        var googleCalendars = calendar.GoogleCalendars;
        var sourceCount = urls.Count + googleCalendars.Count;
        BoundedListUi.NormalizeViewport(ref _firstSourceIndex, sourceCount, VISIBLE_SOURCE_COUNT);
        var status = calendar.IsRefreshing
            ? "Refreshing…"
            : calendar.Status ?? $"{calendar.ConfiguredSourceCount} enabled";

        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 12 },
            Children =
            [
                BuildHeader(status),
                BuildUrlEditor(),
                BuildStatus(),
                BuildSources(urls, googleCalendars),
            ],
        };
    }

    private Node BuildHeader(string status) => new BoxNode
    {
        HorizontalAlignment = ItemsAlignment.Spread,
        VerticalAlignment = ItemsAlignment.Center,
        Children =
        [
            new BoxNode
            {
                Direction = Direction.Vertical,
                Style = new Style { Spacing = 4 },
                Children =
                [
                    new TextNode("Calendar sources", 22, theme.Text),
                    new TextNode(status, theme.Text, theme.Text.MutedColor),
                ],
            },
            BuildActionButton(
                calendar.IsRefreshing ? "Refreshing…" : "Refresh",
                Icons.Reboot,
                "refresh",
                calendar.IsRefreshing ? null : ForceRefresh),
        ],
    };

    private Node BuildSources(
        IReadOnlyList<string> urls,
        IReadOnlyList<GoogleCalendarSource> googleCalendars)
    {
        var sourceRows = googleCalendars
            .Select(source => (Func<Node>)(() => BuildGoogleCalendarRow(source)))
            .Concat(urls.Select(url => (Func<Node>)(() => BuildUrlRow(url))))
            .ToArray();
        if (sourceRows.Length == 0)
        {
            var message = calendar.GoogleAccountConnected
                ? calendar.IsRefreshing
                    ? "Loading Google calendars…"
                    : "No calendars available. Add a calendar URL or select Refresh."
                : "No calendars available. Connect a Google account or add a calendar URL.";
            return MainDialogTabUi.BuildMessage(theme, message);
        }

        var content = new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 8 },
            Children = sourceRows
                .VisibleItems(_firstSourceIndex, VISIBLE_SOURCE_COUNT)
                .Select(item => item.Item())
                .ToArray(),
        };

        return BoundedListUi.BuildScrollableResults(
            content,
            _firstSourceIndex,
            sourceRows.Length,
            VISIBLE_SOURCE_COUNT,
            theme,
            delta => ScrollSources(delta, sourceRows.Length));
    }

    private Node BuildGoogleCalendarRow(GoogleCalendarSource source)
    {
        if (!_googleSwitchAnimations.TryGetValue(source.Id, out var animation))
        {
            animation = new Ref<float>();
            _googleSwitchAnimations[source.Id] = animation;
        }

        return new BoxNode(height: 58)
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => calendar.SetGoogleCalendarEnabled(source.Id, !source.Enabled),
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                Padding = new Insets(16, 8),
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children =
            [
                new BoxNode
                {
                    Direction = Direction.Vertical,
                    Style = new Style { Spacing = 2 },
                    Children =
                    [
                        new TextNode(source.Name, 16, theme.Text, maxWidth: URL_TEXT_MAX_WIDTH, wrapping: TextWrapping.Ellipsis),
                        new TextNode(
                            source.Primary ? "Primary Google calendar" : source.Hidden ? "Hidden Google calendar" : "Google calendar",
                            12,
                            theme.Text.MutedColor),
                    ],
                },
                new SwitchNode(source.Enabled, animation)
                {
                    OffTrackColor = theme.Text.MutedColor,
                    OnTrackColor = theme.Active,
                    KnobColor = theme.Text,
                },
            ],
        };
    }

    private Node BuildUrlEditor() => new BoxNode
    {
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Center,
        Style = new Style { Spacing = 8 },
        Children =
        [
            inputs.Build(_urlInput),
            BuildActionButton(
                "Add",
                Icons.Add,
                "add",
                string.IsNullOrWhiteSpace(_urlInput.Value) ? null : () => AddUrl()),
        ],
    };

    private Node BuildStatus() => string.IsNullOrWhiteSpace(_message)
        ? new TextNode("Paste or type one HTTP(S) calendar URL, then select Add.", theme.Text, theme.Text.MutedColor)
        : new TextNode(_message, theme.Text, _messageIsError ? theme.Critical : theme.Active, maxWidth: 900);

    private Node BuildUrlRow(string url)
    {
        return new BoxNode(height: 52)
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                Padding = new Insets(16, 8),
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children =
            [
                new TextNode(url, theme.Text, theme.Text, URL_TEXT_MAX_WIDTH, TextWrapping.Ellipsis),
                BuildActionButton("Remove", Icons.Delete, "remove:" + url, () => RemoveUrl(url)),
            ],
        };
    }

    private BoxNode BuildActionButton(
        string label,
        SvgAsset icon,
        string key,
        Action? action)
    {
        if (!_buttonStates.TryGetValue(key, out var state))
        {
            state = new ModulesCommon.BoxState { Background = theme.Panel };
            _buttonStates[key] = state;
        }

        state.UpdateColor(theme.Panel);
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = action is null ? null : state.Hovered,
            OnClick = action,
            Opacity = action is null ? 0.5f : 1.0f,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Padding = new Insets(12, 8),
                BorderRadius = 8,
                BorderWidth = 0,
                Spacing = 8,
            },
            Children =
            [
                new ImageNode(icon, 16, 16, theme.Text),
                new TextNode(label, theme.Text, theme.Text),
            ],
        };
    }


    private bool AddUrl(string? value = null)
    {
        var url = (value ?? _urlInput.Value).Trim();
        if (!calendar.AddUrl(url, out var error))
        {
            SetMessage(error, isError: true);
            return false;
        }

        inputs.SetValue(_urlInput, "");
        SetMessage("Calendar source added.", isError: false);
        return true;
    }

    private void RemoveUrl(string url)
    {
        if (!calendar.RemoveUrl(url))
        {
            SetMessage(calendar.Status ?? "Calendar source could not be removed.", isError: true);
            return;
        }

        SetMessage("Calendar source removed.", isError: false);
    }

    private void ForceRefresh()
    {
        ClearMessage();
        _ = calendar.ForceRefreshAsync();
    }

    private void ScrollSources(float delta, int sourceCount) =>
        BoundedListUi.MoveViewport(
            ref _firstSourceIndex,
            delta > 0 ? 1 : -1,
            sourceCount,
            VISIBLE_SOURCE_COUNT);

    private void SetMessage(string message, bool isError)
    {
        _message = message;
        _messageIsError = isError;
    }

    private void ClearMessage()
    {
        _message = "";
        _messageIsError = false;
    }
}
