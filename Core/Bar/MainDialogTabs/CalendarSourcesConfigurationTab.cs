using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Platform;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class CalendarSourcesConfigurationTab(CalendarService calendar, Theme theme) : IMainDialogTab
{
    private const int URL_MAX_LENGTH = 2048;
    private const int URL_TEXT_MAX_WIDTH = 780;

    private readonly Dictionary<string, ModulesCommon.BoxState> _buttonStates = [];
    private string _url = "";
    private string _message = "";
    private bool _messageIsError;
    private bool _isEditing;

    public string Id => "calendar-sources";
    public string Title => "Calendar sources";
    public SvgAsset Icon => Icons.Calendar;

    public void Activate()
    {
        _isEditing = true;
    }

    public bool HandleKey(DialogKey key)
    {
        if (key != DialogKey.PhysicalV || !_isEditing)
        {
            return false;
        }

        _ = PasteUrlAsync();
        return true;
    }

    public void HandleTextInput(string text)
    {
        if (!_isEditing || string.IsNullOrEmpty(text) || _url.Length >= URL_MAX_LENGTH)
        {
            return;
        }

        var remainingLength = URL_MAX_LENGTH - _url.Length;
        _url += text.Length <= remainingLength ? text : text[..remainingLength];
        ClearMessage();
    }

    public void HandleBackspace()
    {
        if (!_isEditing || _url.Length == 0)
        {
            return;
        }

        _url = MainDialogTabUi.RemoveLastTextElement(_url);
        ClearMessage();
    }

    public bool HandleEscape()
    {
        if (!_isEditing && _url.Length == 0)
        {
            return false;
        }

        _url = "";
        _isEditing = false;
        ClearMessage();
        return true;
    }

    public void MoveSelection(SelectionDirection direction)
    {
    }

    public void ActivateSelection()
    {
        if (_isEditing)
        {
            AddUrl();
        }
    }

    public Node Draw()
    {
        var urls = calendar.Urls;
        var status = calendar.IsRefreshing
            ? "Refreshing…"
            : calendar.Status ?? $"{urls.Count} configured";

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
                urls.Count == 0
                    ? MainDialogTabUi.BuildMessage(theme, "No calendar sources configured.")
                    : new BoxNode
                    {
                        Direction = Direction.Vertical,
                        HorizontalAlignment = ItemsAlignment.Stretch,
                        Style = new Style { Spacing = 8 },
                        Children = [..urls.Select(BuildUrlRow)],
                    },
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

    private Node BuildUrlEditor()
    {
        var caret = _isEditing && Math.Sin(Environment.TickCount64 / 200.0) > 0 ? "|" : "";
        var displayedValue = _url.Length == 0 ? "https://example.com/calendar.ics" : _url + caret;

        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Spacing = 8 },
            Children =
            [
                new BoxNode(width: 820, height: 52)
                {
                    VerticalAlignment = ItemsAlignment.Center,
                    OnClick = () => _isEditing = true,
                    Style = ModulesCommon.ModuleStyle(theme, _isEditing ? theme.Active : theme.Panel) with
                    {
                        Padding = new Insets(14, 8),
                        BorderRadius = 8,
                        BorderWidth = _isEditing ? theme.Border.Width : 0,
                    },
                    Children =
                    [
                        new TextNode(
                            displayedValue,
                            16,
                            _url.Length == 0 ? theme.Text.MutedColor : theme.Text,
                            maxWidth: URL_TEXT_MAX_WIDTH,
                            wrapping: TextWrapping.Ellipsis),
                    ],
                },
                BuildActionButton(
                    "Add",
                    Icons.Add,
                    "add",
                    string.IsNullOrWhiteSpace(_url) ? null : AddUrl),
            ],
        };
    }

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

    private async Task PasteUrlAsync()
    {
        var text = await CommandRunner.TryReadAsync(
            "wl-paste",
            "--no-newline --type text",
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        if (string.IsNullOrWhiteSpace(text))
        {
            SetMessage("Clipboard does not contain text, or no clipboard reader is available.", isError: true);
            return;
        }

        _url = text.Trim();
        if (_url.Length > URL_MAX_LENGTH)
        {
            _url = _url[..URL_MAX_LENGTH];
        }

        ClearMessage();
    }

    private void AddUrl()
    {
        var url = _url.Trim();
        if (!calendar.AddUrl(url, out var error))
        {
            SetMessage(error, isError: true);
            return;
        }

        _url = "";
        SetMessage("Calendar source added.", isError: false);
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
