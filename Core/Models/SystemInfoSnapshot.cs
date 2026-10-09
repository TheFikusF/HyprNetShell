namespace HyprNetShell.Core.Models;

public sealed record SystemInfoEntry(string Label, string Value);

public sealed record SystemInfoSnapshot
{
    public static SystemInfoSnapshot Empty { get; } = new([], false);

    public IReadOnlyList<SystemInfoEntry> Entries
    {
        get;
    }
    public bool IsRefreshing
    {
        get;
    }

    public SystemInfoSnapshot(IEnumerable<SystemInfoEntry> entries, bool isRefreshing)
    {
        Entries = Array.AsReadOnly(entries.ToArray());
        IsRefreshing = isRefreshing;
    }
}
