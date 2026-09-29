using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.KdeConnect;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class KdeConnectWidget(KdeConnectService service, Theme theme)
{
    public const int WIDTH = 300;

    private readonly ModulesCommon.BoxState _titleState = new();

    public Node Draw(Action openTab)
    {
        var devices = service.Snapshot.Devices;
        var device = devices.FirstOrDefault(candidate => candidate.PairingState == KdeConnectPairingState.IncomingRequest)
            ?? devices.FirstOrDefault(candidate => candidate.IsPaired && candidate.IsReachable)
            ?? devices.FirstOrDefault(candidate => candidate.IsPaired);

        return new BoxNode(WIDTH)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Start,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                BorderRadius = 8,
                Spacing = 8,
            },
            Children =
            [
                ModulesCommon.CentralWidgetHeader(
                    Icons.Smartphone,
                    "KDE Connect",
                    openTab,
                    _titleState,
                    theme),
                BuildSummary(device),
            ],
        };
    }

    private Node BuildSummary(KdeConnectDeviceSnapshot? device)
    {
        if (device is null)
        {
            return new BoxNode(height: 70)
            {
                HorizontalAlignment = ItemsAlignment.Center,
                VerticalAlignment = ItemsAlignment.Center,
                Children = [new TextNode("No paired device", theme.Text, theme.Text.MutedColor)],
            };
        }

        return new BoxNode(height: 70)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Spacing = 6 },
            Children =
            [
                new TextNode(device.Name, theme.Text.HeaderSize, theme.Text, WIDTH - 24, TextWrapping.Ellipsis),
                new TextNode(BuildStatus(device), theme.Text, theme.Text.MutedColor),
            ],
        };
    }

    private static string BuildStatus(KdeConnectDeviceSnapshot device)
    {
        if (device.PairingState == KdeConnectPairingState.IncomingRequest)
        {
            return device.VerificationCode is { Length: > 0 } code
                ? $"Pairing request · Code {code}"
                : "Pairing request";
        }
        if (!device.IsReachable)
        {
            return "Paired offline";
        }

        var details = new List<string> { "Connected" };
        if (device.BatteryLevel is { } battery)
        {
            details.Add($"{battery}%");
        }

        if (device.IsCharging == true)
        {
            details.Add("Charging");
        }

        return string.Join(" · ", details);
    }
}
