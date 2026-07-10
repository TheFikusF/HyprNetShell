namespace HyprNetShell.Core.Models;

public sealed record AudioDeviceSnapshot(
    string Id,
    string Name,
    int Volume,
    bool Muted,
    bool Active);

public sealed record AudioSnapshot(
    bool Available,
    IReadOnlyList<AudioDeviceSnapshot> Outputs,
    IReadOnlyList<AudioDeviceSnapshot> Inputs)
{
    public static AudioSnapshot Empty { get; } = new(false, [], []);

    public AudioDeviceSnapshot? ActiveOutput => Outputs.FirstOrDefault(device => device.Active);
    public AudioDeviceSnapshot? ActiveInput => Inputs.FirstOrDefault(device => device.Active);
}
