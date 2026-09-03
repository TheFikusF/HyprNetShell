using System.Globalization;
using System.Net;
using System.Text.Json;
using HyprNetShell.Core.Logging;

namespace HyprNetShell.Core.Features.System;

internal sealed class DictionaryService
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders =
        {
            { "User-Agent", "HyprNetShell/1.0" },
        },
    };

    internal string TranslationLanguage { get; }

    internal DictionaryService()
    {
        var configuredLanguage = Environment.GetEnvironmentVariable("HYPRNETSHELL_TRANSLATION_LANGUAGE")?.Trim();
        TranslationLanguage = configuredLanguage is { Length: > 0 }
            ? configuredLanguage
            : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (TranslationLanguage.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            TranslationLanguage = "es";
        }
    }

    internal async Task<DictionaryLookupResult> LookupAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return DictionaryLookupResult.Empty;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LookupTimeout);

        var tasks = new[]
        {
            FetchProviderAsync("Dictionary", () => FetchStandardDefinitionsAsync(query, timeout.Token), cancellationToken),
            FetchProviderAsync("Urban Dictionary", () => FetchUrbanDefinitionsAsync(query, timeout.Token), cancellationToken),
            FetchProviderAsync("Translation", () => FetchTranslationAsync(query, timeout.Token), cancellationToken),
        };
        var providers = await Task.WhenAll(tasks);

        return new DictionaryLookupResult(
            query,
            providers.SelectMany(provider => provider.Items).ToArray(),
            providers.Where(provider => provider.Error is not null).Select(provider => provider.Error!).ToArray());
    }

    private static async Task<ProviderResult> FetchProviderAsync(
        string provider,
        Func<Task<IReadOnlyList<DictionaryResultItem>>> fetch,
        CancellationToken callerToken)
    {
        try
        {
            return new ProviderResult(await fetch(), null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProviderResult([], $"{provider} timed out");
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Dictionary", $"{provider} lookup failed", exception);
            return new ProviderResult([], $"{provider} unavailable");
        }
    }

    private static async Task<IReadOnlyList<DictionaryResultItem>> FetchStandardDefinitionsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://freedictionaryapi.com/api/v1/entries/en/{Uri.EscapeDataString(query)}");
        using var response = await GetAsync(uri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync(
                         stream,
                         DictionaryJsonContext.Default.FreeDictionaryResponse,
                         cancellationToken)
                     ?? new FreeDictionaryResponse();

        return result.Entries
            .SelectMany(entry => FlattenSenses(entry.Senses).Select(sense => new
            {
                PartOfSpeech = entry.PartOfSpeech,
                Phonetic = entry.Pronunciations
                    .FirstOrDefault(pronunciation => pronunciation.Type == "ipa")?.Text
                    ?? entry.Pronunciations.FirstOrDefault()?.Text,
                sense.Definition,
                Example = sense.Examples.FirstOrDefault(),
            }))
            .Where(item => !string.IsNullOrWhiteSpace(item.Definition))
            .Take(6)
            .Select(item => new DictionaryResultItem(
                "FreeDictionaryAPI.com",
                BuildHeading(result.Word ?? query, item.PartOfSpeech, item.Phonetic),
                item.Definition!,
                item.Example,
                "Wiktionary · CC BY-SA 4.0",
                result.Source?.Url))
            .ToArray();
    }

    private static async Task<IReadOnlyList<DictionaryResultItem>> FetchUrbanDefinitionsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://api.urbandictionary.com/v0/define?term={Uri.EscapeDataString(query)}");
        using var response = await GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync(
                         stream,
                         DictionaryJsonContext.Default.UrbanDictionaryResponse,
                         cancellationToken)
                     ?? new UrbanDictionaryResponse();

        return result.List
            .Where(item => !string.IsNullOrWhiteSpace(item.Definition))
            .OrderByDescending(item => item.ThumbsUp - item.ThumbsDown)
            .Take(4)
            .Select(item => new DictionaryResultItem(
                "Urban Dictionary",
                item.Word ?? query,
                RemoveUrbanLinks(item.Definition) ?? "",
                RemoveUrbanLinks(item.Example),
                item.Author is { Length: > 0 }
                    ? $"{item.Author} · +{item.ThumbsUp}/-{item.ThumbsDown}"
                    : $"+{item.ThumbsUp}/-{item.ThumbsDown}",
                item.Permalink))
            .ToArray();
    }

    private async Task<IReadOnlyList<DictionaryResultItem>> FetchTranslationAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var pair = Uri.EscapeDataString($"en|{TranslationLanguage}");
        var uri = new Uri(
            $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(query)}&langpair={pair}");
        using var response = await GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync(
            stream,
            DictionaryJsonContext.Default.TranslationResponse,
            cancellationToken);
        var translation = result?.ResponseData?.TranslatedText;
        if (string.IsNullOrWhiteSpace(translation))
        {
            return [];
        }

        return
        [
            new DictionaryResultItem(
                "Translation",
                $"English → {TranslationLanguage.ToUpperInvariant()}",
                translation,
                Attribution: "MyMemory Translation Memory"),
        ];
    }

    private static async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response;
    }

    private static IEnumerable<FreeDictionarySense> FlattenSenses(IEnumerable<FreeDictionarySense> senses)
    {
        foreach (var sense in senses)
        {
            yield return sense;
            foreach (var subsense in FlattenSenses(sense.Subsenses))
            {
                yield return subsense;
            }
        }
    }

    private static string BuildHeading(string word, string? partOfSpeech, string? phonetic)
    {
        var details = new[] { partOfSpeech, phonetic }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var suffix = string.Join(" · ", details);
        return suffix.Length == 0 ? word : $"{word} · {suffix}";
    }

    private static string? RemoveUrbanLinks(string? value) =>
        value?.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal);

    private sealed record ProviderResult(IReadOnlyList<DictionaryResultItem> Items, string? Error);
}
