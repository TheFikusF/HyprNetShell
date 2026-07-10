using System.Diagnostics;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

internal sealed class LanguageModule : IDrawableModule
{
    private const int WIDTH = 90;

    private static readonly TimeSpan ChangePopupDuration = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyDictionary<string, string> _aliases = new Dictionary<string, string>
    {
        ["English (US)"] = "🇺🇸🦅🗽",
        ["Ukrainian"] = "🇺🇦 UKR",
        ["Russian"] = "🛡 RVC"
    };

    private readonly Dictionary<string, ModulesCommon.BoxState> _languagesRowStates = [];

    private readonly HyprlandService _hyprland;
    private readonly StatusBarTheme _theme;
    private readonly ModulesCommon.NodeWithPopup _node;

    private string _lastLayoutName = "";
    private DateTime _showUntil = DateTime.MinValue;

    public bool IsShown => _showUntil > DateTime.UtcNow;

    public LanguageModule(HyprlandService hyprland, StatusBarTheme theme)
    {
        _hyprland = hyprland;
        _theme = theme;
        _node = new("language_module", ignorePopupQueue: true)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            GetShouldShowPopup = hovered => hovered || DateTime.UtcNow < _showUntil,
        };
    }

    public Node Draw()
    {
        var snapshot = _hyprland.Snapshot;
        var layoutName = string.IsNullOrWhiteSpace(snapshot.LayoutName) ? "Unknown" : snapshot.LayoutName.Trim();
        var alias = _aliases.TryGetValue(layoutName, out var a) ? a : layoutName;

        if (!string.Equals(_lastLayoutName, layoutName, StringComparison.Ordinal))
        {
            _lastLayoutName = layoutName;
            _showUntil = DateTime.UtcNow + ChangePopupDuration;
        }

        return _node.Draw([
            new BoxNode(WIDTH)
            {
                Direction = Direction.Vertical,
                VerticalAlignment = ItemsAlignment.Center,
                HorizontalAlignment = ItemsAlignment.Center,
                OnClick = () => Process.Start(new ProcessStartInfo
                {
                    FileName = "hyprctl",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "switchxkblayout", snapshot.KeyboardName, "next" },
                }),
                Style = new Style
                {
                    BackgroundColor = Color.Lerp(Color.FromHex("#0CC665"), Color.Black, 0.8f) with { A = 0.8f },
                    BorderColor = _theme.Border,
                    BorderRadius = _theme.Radius,
                    BorderWidth = _theme.BorderWidth,
                    Padding = new Insets(8, 6)
                },
                Children =
                [
                    new TextNode(alias, 14.0f, _theme.Text),
                ],
            }
        ], () => BuildPopup(snapshot.KeyboardName));
    }

    private Node BuildPopup(string keyboardName) =>
        new BoxNode(WIDTH + 30)
        {
            IgnoreLayout = true,
            Style = new Style
            {
                Padding = new Insets(30, 0, 0, 0),
            },
            Children =
            [
                new BoxNode(WIDTH + 30)
                {
                    Direction = Direction.Vertical,
                    VerticalAlignment = ItemsAlignment.Start,
                    HorizontalAlignment = ItemsAlignment.Spread,
                    Style = new Style
                    {
                        BackgroundColor = Color.FromRgb(0, 0, 0, 0.9f),
                        BorderColor = _theme.Border,
                        BorderRadius = 8,
                        BorderWidth = 2,
                        Padding = 8,
                        Spacing = 8
                    },
                    Children =
                    [
                        .._aliases.Keys.Select((layout, index) => BuildPopupRow(layout,
                            keyboardName, index))
                    ],
                }
            ]
        };

    private BoxNode BuildPopupRow(string text, string keyboardName, int index)
    {
        var alias = _aliases[text];

        var normal = text == _lastLayoutName ? _theme.Active : _theme.Panel;
        float fontSize = text == _lastLayoutName ? 20.0f : 14.0f;
        var state = GetPopupWorkspaceState(text, normal);
        var target = state.Hovered ? Color.Lighten(normal, text == _lastLayoutName ? 0.18f : 0.12f) : normal;
        state.Background = Color.LerpSmooth(state.Background, target, 18.0f, ModulesCommon.DELTA_TIME);

        return new BoxNode
        {
            Direction = Direction.Horizontal,
            IsHovered = state.Hovered,
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Center,
            OnClick = () => Process.Start(new ProcessStartInfo
            {
                FileName = "hyprctl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "switchxkblayout", keyboardName, index.ToString() },
            }),
            Style = ModulesCommon.ModuleStyle(_theme, state.Background) with
            {
                BorderRadius = 8,
                BorderWidth = text == _lastLayoutName ? _theme.BorderWidth : 0,
            },
            Children =
            [
                new TextNode(alias, fontSize, _theme.Text)
            ]
        };
    }

    private ModulesCommon.BoxState GetPopupWorkspaceState(string layout, Color initialColor)
    {
        if (_languagesRowStates.TryGetValue(layout, out var state))
        {
            return state;
        }

        state = new ModulesCommon.BoxState(initialColor);
        _languagesRowStates[layout] = state;
        return state;
    }
}