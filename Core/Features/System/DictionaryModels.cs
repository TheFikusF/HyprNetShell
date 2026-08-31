namespace HyprNetShell.Core.Features.System;

internal sealed record DictionaryLookupResult(
    string Query,
    IReadOnlyList<DictionaryResultItem> Items,
    IReadOnlyList<string> Errors)
{
    public static DictionaryLookupResult Empty { get; } = new("", [], []);
}

internal sealed record DictionaryResultItem(
    string Source,
    string Heading,
    string Definition,
    string? Example = null,
    string? Attribution = null,
    string? Url = null);
