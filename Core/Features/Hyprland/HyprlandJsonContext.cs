using System.Text.Json.Serialization;

namespace HyprNetShell.Core.Features.Hyprland;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HyprClient))]
[JsonSerializable(typeof(HyprClient[]))]
[JsonSerializable(typeof(HyprWorkspace[]))]
[JsonSerializable(typeof(HyprMonitor[]))]
[JsonSerializable(typeof(HyprDevices))]
internal sealed partial class HyprlandJsonContext : JsonSerializerContext
{
}

internal sealed record HyprWorkspace(int Id, string? Monitor);

internal sealed record HyprMonitor(string Name, bool Focused, HyprActiveWorkspace? ActiveWorkspace);

internal sealed record HyprActiveWorkspace(int Id);

internal sealed record HyprClient(
    string Address,
    [property: JsonPropertyName("class")] string ClassName,
    [property: JsonPropertyName("initialClass")] string InitialClassName,
    string Title,
    HyprClientWorkspace? Workspace);

internal sealed record HyprClientWorkspace(int Id);

internal sealed record HyprDevices(IReadOnlyList<HyprKeyboard>? Keyboards);

internal sealed record HyprKeyboard(
    string Name,
    [property: JsonPropertyName("active_keymap")]
    string ActiveKeymap,
    bool Main);
