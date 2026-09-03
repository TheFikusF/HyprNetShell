using System.Text.Json.Serialization;

namespace HyprNetShell.Core.Features.System;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(FreeDictionaryResponse))]
[JsonSerializable(typeof(UrbanDictionaryResponse))]
[JsonSerializable(typeof(TranslationResponse))]
internal sealed partial class DictionaryJsonContext : JsonSerializerContext;

internal sealed class FreeDictionaryResponse
{
    [JsonPropertyName("word")] public string? Word { get; init; }
    [JsonPropertyName("entries")] public List<FreeDictionaryEntry> Entries { get; init; } = [];
    [JsonPropertyName("source")] public FreeDictionarySource? Source { get; init; }
}

internal sealed class FreeDictionaryEntry
{
    [JsonPropertyName("partOfSpeech")] public string? PartOfSpeech { get; init; }
    [JsonPropertyName("pronunciations")] public List<FreeDictionaryPronunciation> Pronunciations { get; init; } = [];
    [JsonPropertyName("senses")] public List<FreeDictionarySense> Senses { get; init; } = [];
}

internal sealed class FreeDictionaryPronunciation
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
}

internal sealed class FreeDictionarySense
{
    [JsonPropertyName("definition")] public string? Definition { get; init; }
    [JsonPropertyName("examples")] public List<string> Examples { get; init; } = [];
    [JsonPropertyName("subsenses")] public List<FreeDictionarySense> Subsenses { get; init; } = [];
}

internal sealed class FreeDictionarySource
{
    [JsonPropertyName("url")] public string? Url { get; init; }
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
