namespace HyprNetShell.Core.Models;

internal sealed record ChatGptLimitWindow(
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt);

internal sealed record ChatGptUsageSnapshot(
    bool Connected,
    string? Plan,
    IReadOnlyList<ChatGptLimitWindow> Windows,
    DateTimeOffset? UpdatedAt,
    string? Status)
{
    internal static ChatGptUsageSnapshot Disconnected
    {
        get;
    } = new(
        false,
        null,
        [],
        null,
        "Connect ChatGPT in Settings");
}
