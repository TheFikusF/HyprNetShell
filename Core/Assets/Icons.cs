using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Assets;

public static partial class Icons
{
    [SvgAsset(
        "assets/icons/lucide/wifi-zero.svg",
        "assets/icons/lucide/wifi-low.svg",
        "assets/icons/lucide/wifi-high.svg",
        "assets/icons/lucide/wifi.svg")]
    public static partial SvgAsset[] WifiStrength { get; }

    [SvgAsset("assets/icons/lucide/wifi-off.svg")]
    public static partial SvgAsset WifiOff { get; }

    [SvgAsset("assets/icons/lucide/ethernet-port.svg")]
    public static partial SvgAsset Ethernet { get; }

    [SvgAsset("assets/icons/lucide/globe.svg")]
    public static partial SvgAsset Globe { get; }

    [SvgAsset("assets/icons/lucide/bluetooth.svg")]
    public static partial SvgAsset Bluetooth { get; }

    [SvgAsset("assets/icons/lucide/bluetooth-connected.svg")]
    public static partial SvgAsset BluetoothConnected { get; }

    [SvgAsset("assets/icons/lucide/bluetooth-off.svg")]
    public static partial SvgAsset BluetoothOff { get; }

    [SvgAsset("assets/icons/lucide/bluetooth-searching.svg")]
    public static partial SvgAsset BluetoothSearching { get; }

    [SvgAsset(
        "assets/icons/lucide/volume.svg",
        "assets/icons/lucide/volume-1.svg",
        "assets/icons/lucide/volume-2.svg")]
    public static partial SvgAsset[] VolumeLevels { get; }

    [SvgAsset("assets/icons/lucide/volume-x.svg")]
    public static partial SvgAsset VolumeMuted { get; }

    [SvgAsset("assets/icons/lucide/volume-off.svg")]
    public static partial SvgAsset VolumeOff { get; }

    [SvgAsset("assets/icons/lucide/mic.svg")]
    public static partial SvgAsset Microphone { get; }

    [SvgAsset("assets/icons/lucide/mic-off.svg")]
    public static partial SvgAsset MicrophoneOff { get; }

    [SvgAsset("assets/icons/lucide/headphones.svg")]
    public static partial SvgAsset Headphones { get; }

    [SvgAsset("assets/icons/lucide/speaker.svg")]
    public static partial SvgAsset Speaker { get; }

    [SvgAsset("assets/icons/lucide/copy.svg")]
    public static partial SvgAsset Copy { get; }

    [SvgAsset("assets/icons/lucide/check.svg")]
    public static partial SvgAsset Check { get; }

    [SvgAsset(
        "assets/icons/lucide/battery.svg",
        "assets/icons/lucide/battery-low.svg",
        "assets/icons/lucide/battery-medium.svg",
        "assets/icons/lucide/battery-full.svg")]
    public static partial SvgAsset[] BatteryLevels { get; }

    [SvgAsset("assets/icons/lucide/battery-warning.svg")]
    public static partial SvgAsset BatteryWarning { get; }

    [SvgAsset("assets/icons/lucide/battery-charging.svg")]
    public static partial SvgAsset BatteryCharging { get; }

    [SvgAsset("assets/icons/lucide/cpu.svg")]
    public static partial SvgAsset Cpu { get; }

    [SvgAsset("assets/icons/lucide/memory-stick.svg")]
    public static partial SvgAsset Memory { get; }

    [SvgAsset("assets/icons/lucide/thermometer.svg")]
    public static partial SvgAsset Temperature { get; }

    [SvgAsset("assets/icons/lucide/sun.svg", "assets/icons/lucide/sun-medium.svg", "assets/icons/lucide/sun-dim.svg")]
    public static partial SvgAsset[] Brightness { get; }

    [SvgAsset("assets/icons/lucide/keyboard.svg")]
    public static partial SvgAsset Keyboard { get; }

    [SvgAsset("assets/icons/lucide/play.svg")]
    public static partial SvgAsset Play { get; }

    [SvgAsset("assets/icons/lucide/pause.svg")]
    public static partial SvgAsset Pause { get; }

    [SvgAsset("assets/icons/lucide/skip-forward.svg")]
    public static partial SvgAsset SkipForward { get; }

    [SvgAsset("assets/icons/lucide/skip-back.svg")]
    public static partial SvgAsset SkipBack { get; }
}
