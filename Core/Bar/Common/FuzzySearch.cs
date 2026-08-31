using FuzzySharp;

namespace HyprNetShell.Core.Bar.Common;

internal static class FuzzySearch
{
    private const string EnglishKeys = "`qwertyuiop[]asdfghjkl;'zxcvbnm,.";
    private const string RussianKeys = "ёйцукенгшщзхъфывапролджэячсмитьбю";

    private static readonly IReadOnlyDictionary<char, char> KeyboardLayoutMap = BuildKeyboardLayoutMap();

    public static int Score(string query, string candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
        {
            return 0;
        }

        var normalizedQuery = query.Trim();
        var normalizedCandidate = candidate.Trim();
        var translatedQuery = TranslateKeyboardLayout(normalizedQuery);
        var translatedCandidate = TranslateKeyboardLayout(normalizedCandidate);

        return Math.Max(
            Math.Max(ScoreVariant(normalizedQuery, normalizedCandidate), ScoreVariant(translatedQuery, normalizedCandidate)),
            Math.Max(ScoreVariant(normalizedQuery, translatedCandidate), ScoreVariant(translatedQuery, translatedCandidate)));
    }

    public static string TranslateKeyboardLayout(string text)
    {
        var translated = text.ToCharArray();
        for (var index = 0; index < translated.Length; index++)
        {
            if (KeyboardLayoutMap.TryGetValue(translated[index], out var replacement))
            {
                translated[index] = replacement;
            }
        }

        return new string(translated);
    }

    private static int ScoreVariant(string query, string candidate)
    {
        if (string.Equals(query, candidate, StringComparison.CurrentCultureIgnoreCase))
        {
            return 100;
        }

        var substringIndex = candidate.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (substringIndex >= 0)
        {
            var positionPenalty = Math.Min(4, substringIndex / 4);
            var lengthPenalty = Math.Min(5, Math.Max(0, candidate.Length - query.Length) / 12);
            return 98 - positionPenalty - lengthPenalty;
        }

        return Fuzz.WeightedRatio(query, candidate);
    }

    private static IReadOnlyDictionary<char, char> BuildKeyboardLayoutMap()
    {
        var map = new Dictionary<char, char>();
        for (var index = 0; index < EnglishKeys.Length; index++)
        {
            AddPair(map, EnglishKeys[index], RussianKeys[index]);
            AddPair(map, char.ToUpperInvariant(EnglishKeys[index]), char.ToUpperInvariant(RussianKeys[index]));
        }

        return map;
    }

    private static void AddPair(IDictionary<char, char> map, char left, char right)
    {
        map[left] = right;
        map[right] = left;
    }
}
