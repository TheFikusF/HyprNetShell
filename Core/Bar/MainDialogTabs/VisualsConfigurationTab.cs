using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Configuration;
using HyprNetShell.GUI;
using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class VisualsConfigurationTab : IMainDialogTab
{
    private readonly AppConfigurationStore _configuration = AppConfigurationStore.Shared;
    private readonly Ref<float> _overviewSwitchAnimation = new(
        AppConfigurationStore.Shared.Snapshot.Visuals?.CrystalOverview != false ? 1 : 0);

    public string Id => "visuals";
    public string Title => "Visuals";
    public SvgAsset Icon => Icons.Settings;

    public void Activate()
    {
    }

    public void MoveSelection(SelectionDirection direction)
    {
    }

    public void ActivateSelection() => ToggleOverview();

    public Node Draw() => new BoxNode {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = new Style { Spacing = 12 },
        Children =
        [
            MainDialogTabUi.BuildSectionHeader("Overview appearance", "Saved automatically"),
            new BoxNode(height: 78) {
                HorizontalAlignment = ItemsAlignment.Spread,
                VerticalAlignment = ItemsAlignment.Center,
                OnClick = ToggleOverview,
                Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with {
                    Padding = new Insets(18, 0),
                    BorderRadius = 8,
                    BorderWidth = 0,
                },
                Children =
                [
                    new BoxNode {
                        Direction = Direction.Vertical,
                        Style = new Style { Spacing = 4 },
                        Children =
                        [
                            new TextNode("Crystal overview", 16),
                            new TextNode("Disable for the classic flat overview", color: ThemeManager.Current.Text.MutedColor),
                        ],
                    },
                    new SwitchNode(_configuration.Snapshot.Visuals?.CrystalOverview != false, _overviewSwitchAnimation) {
                        OffTrackColor = ThemeManager.Current.Text.MutedColor,
                        OnTrackColor = ThemeManager.Current.Active,
                        KnobColor = ThemeManager.Current.Text,
                    },
                ],
            },
        ],
    };

    private void ToggleOverview()
    {
        _configuration.Update(configuration =>
        {
            configuration.Visuals ??= new VisualsConfiguration();
            configuration.Visuals.CrystalOverview = !configuration.Visuals.CrystalOverview;
        });
    }
}
