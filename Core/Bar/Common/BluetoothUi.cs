using HyprNetShell.Core.Assets;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Bar.Common;

internal static class BluetoothUi
{
    internal static SvgAsset DeviceIcon(string? icon) => icon?.ToLowerInvariant() switch
    {
        "audio-headphones" => Icons.Headphones,
        "audio-headset" => Icons.Headset,
        "audio-speakers" or "audio-card" => Icons.Speaker,
        "audio-input-microphone" => Icons.Microphone,
        "input-keyboard" => Icons.Keyboard,
        "input-mouse" => Icons.Mouse,
        "input-gaming" => Icons.Gamepad,
        "input-tablet" => Icons.Tablet,
        "phone" => Icons.Smartphone,
        "computer" => Icons.Laptop,
        "multimedia-player" => Icons.Monitor,
        "watch" => Icons.Watch,
        "camera-photo" or "camera-video" => Icons.Camera,
        "printer" or "scanner" => Icons.Printer,
        _ => Icons.Bluetooth,
    };
}
