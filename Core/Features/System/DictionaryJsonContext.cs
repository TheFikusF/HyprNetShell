using System.Text.Json.Serialization;

namespace HyprNetShell.Core.Features.System;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(StandardDictionaryEntry[]))]
[JsonSerializable(typeof(UrbanDictionaryResponse))]
[JsonSerializable(typeof(TranslationResponse))]
internal sealed partial class DictionaryJsonContext : JsonSerializerContext;

internal sealed class StandardDictionaryEntry
{
    [JsonPropertyName("word")] public string? Word { get; init; }
    [JsonPropertyName("phonetic")] public string? Phonetic { get; init; }
    [JsonPropertyName("meanings")] public List<StandardDictionaryMeaning> Meanings { get; init; } = [];
}

internal sealed class StandardDictionaryMeaning
{
    [JsonPropertyName("partOfSpeech")] public string? PartOfSpeech { get; init; }
    [JsonPropertyName("definitions")] public List<StandardDictionaryDefinition> Definitions { get; init; } = [];
}

internal sealed class StandardDictionaryDefinition
{
    [JsonPropertyName("definition")] public string? Definition { get; init; }
    [JsonPropertyName("example")] public string? Example { get; init; }
}

internal sealed class UrbanDictionaryResponse
{
    [JsonPropertyName("list")] public List<UrbanDictionaryDefinition> List { get; init; } = [];
}

internal sealed class UrbanDictionaryDefinition
{
    [JsonPropertyName("word")] public string? Word { get; init; }
    [JsonPropertyName("definition")] public string? Definition { get; init; }
    [JsonPropertyName("example")] public string? Example { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("permalink")] public string? Permalink { get; init; }
    [JsonPropertyName("thumbs_up")] public int ThumbsUp { get; init; }
    [JsonPropertyName("thumbs_down")] public int ThumbsDown { get; init; }
}

internal sealed class TranslationResponse
{
    [JsonPropertyName("responseData")] public TranslationResponseData? ResponseData { get; init; }
}

internal sealed class TranslationResponseData
{
    [JsonPropertyName("translatedText")] public string? TranslatedText { get; init; }
    [JsonPropertyName("match")] public double Match { get; init; }
}
