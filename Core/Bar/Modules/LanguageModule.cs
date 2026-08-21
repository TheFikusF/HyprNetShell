using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class LanguageModule : IDrawableModule
{
    private const int WIDTH = 100;

    private static readonly TimeSpan ChangePopupDuration = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyDictionary<string, string> _aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["English (US)"] = "🇺🇸🦅🗽",
            ["Ukrainian"] = "🇺🇦 УКР",
            ["Russian"] = "🛡 РДК"
        };

    private readonly Dictionary<string, ModulesCommon.BoxState> _languagesRowStates = [];

    private readonly HyprlandService _hyprland;
    private readonly IHyprctl _hyprctl;
    private readonly Theme _theme;
    private readonly ModulesCommon.NodeWithPopup _node;

    private string _lastLayoutName = "";
    private DateTime _showUntil = DateTime.MinValue;

    public bool IsShown => _showUntil > DateTime.UtcNow;

    public LanguageModule(
            HyprlandService hyprland,
            IHyprctl hyprctl,
            Theme theme,
            ModulesCommon.PopupCoordinator popupCoordinator)
    {
        _hyprland = hyprland;
        _hyprctl = hyprctl;
        _theme = theme;
        _node = new(popupCoordinator, "language_module", ignorePopupQueue: true)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            GetShouldShowPopup = hovered => hovered || DateTime.UtcNow < _showUntil,
        };
    }

    public Node Draw()
    {
        var snapshot = _hyprland.Snapshot;
        var keyboardName = snapshot.KeyboardName ?? string.Empty;
        var layoutName = NormalizeLayoutName(snapshot.LayoutName);
        var alias = _aliases
            .FirstOrDefault(entry => string.Equals(entry.Key, layoutName, StringComparison.OrdinalIgnoreCase))
            .Value ?? layoutName;

        if (string.Equals(_lastLayoutName, layoutName, StringComparison.Ordinal) == false)
        {
            _lastLayoutName = layoutName;
            _showUntil = DateTime.UtcNow + ChangePopupDuration;
        }

        return _node.Draw([
            new BoxNode(WIDTH, 18 + 6 * 2 + 3 * 2)
            {
                Direction = Direction.Vertical,
                VerticalAlignment = ItemsAlignment.Center,
                HorizontalAlignment = ItemsAlignment.Center,
                OnClick = () => _ = _hyprctl.SwitchKeyboardLayoutAsync(keyboardName),
                OnScroll = delta => ScrollLanguage(keyboardName, layoutName, delta),
                Style = ModulesCommon.ModuleStyle(_theme, ModulesCommon.ToBackground(_theme, Color.FromHex("#0CC665"))),
                Children = [new TextNode(alias, _theme.TextSize, _theme.Text)]
            }
        ], () => BuildPopup(keyboardName));
    }

    private static string NormalizeLayoutName(string? layoutName) =>
        string.IsNullOrWhiteSpace(layoutName) ? "Unknown" : layoutName.Trim();

    private void ScrollLanguage(string keyboardName, string currentLayout, float scrollDelta)
    {
        var layouts = _aliases.Keys.ToArray();
        var currentIndex = Array.FindIndex(
            layouts,
            layout => string.Equals(layout, currentLayout, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0)
        {
            currentIndex = scrollDelta > 0.0f ? -1 : 0;
        }

        var direction = scrollDelta > 0.0f ? 1 : -1;
        var targetIndex = (currentIndex + direction + layouts.Length) % layouts.Length;
        _ = _hyprctl.SwitchKeyboardLayoutAsync(keyboardName, targetIndex);
    }

    private BoxNode BuildPopup(string keyboardName) => new(WIDTH + 30)
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(_theme),
        Children = [.._aliases.Keys.Select((layout, index) => BuildPopupRow(layout, keyboardName, index))]
    };

    private BoxNode BuildPopupRow(string text, string keyboardName, int index)
    {
        var alias = _aliases[text];
        var normal = text == _lastLayoutName ? _theme.Active : _theme.Panel;
        var fontSize = text == _lastLayoutName ? 20.0f : _theme.TextSize;
        var state = _languagesRowStates.GetState(text, normal).UpdateColor(normal);
        return new BoxNode
        {
            Direction = Direction.Horizontal,
            IsHovered = state.Hovered,
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Center,
            OnClick = () => _ = _hyprctl.SwitchKeyboardLayoutAsync(keyboardName, index),
            Style = ModulesCommon.ModuleStyle(_theme, state.Background) with
            {
                BorderRadius = 8,
                BorderWidth = text == _lastLayoutName ? _theme.BorderWidth : 0,
                Padding = new Insets(0, text == _lastLayoutName ? 12 : 8)
            },
            Children = [new TextNode(alias, fontSize, _theme.Text)]
        };
    }
}
