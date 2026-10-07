using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Features.System;

internal static class GoogleCalendarClient
{
    private static readonly Uri CalendarListEndpoint = new(
        "https://www.googleapis.com/calendar/v3/users/me/calendarList");
    private const int MaximumPages = 100;

    internal static async Task<GoogleCalendarInfo[]> GetCalendarsAsync(
        HttpClient httpClient,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var calendars = new List<GoogleCalendarInfo>();
        string? pageToken = null;
        for (var page = 0; page < MaximumPages; page++)
        {
            var parameters = new Dictionary<string, string> {
                ["maxResults"] = "250",
                ["showDeleted"] = "false",
                ["showHidden"] = "true",
            };
            if (pageToken is { Length: > 0 })
            {
                parameters["pageToken"] = pageToken;
            }

            var response = await GetAsync<GoogleCalendarListResponse>(
                httpClient,
                BuildUri(CalendarListEndpoint, parameters),
                accessToken,
                GoogleCalendarJsonContext.Default.GoogleCalendarListResponse,
                cancellationToken);
            foreach (var item in response.Items ?? [])
            {
                if (!string.IsNullOrWhiteSpace(item.Id))
                {
                    calendars.Add(new GoogleCalendarInfo(
                        item.Id,
                        string.IsNullOrWhiteSpace(item.Summary) ? item.Id : item.Summary.Trim(),
                        item.Primary,
                        item.Hidden));
                }
            }

            pageToken = response.NextPageToken;
            if (string.IsNullOrEmpty(pageToken))
            {
                return calendars
                    .OrderByDescending(calendar => calendar.Primary)
                    .ThenBy(calendar => calendar.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }

        throw new InvalidDataException("Google Calendar returned too many pages of calendars.");
    }

    internal static async Task<CalendarOccurrence[]> GetOccurrencesAsync(
        HttpClient httpClient,
        string accessToken,
        GoogleCalendarInfo calendar,
        DateTime rangeStart,
        DateTime rangeEnd,
        int maximumOccurrences,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(
            $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(calendar.Id)}/events");
        var occurrences = new List<CalendarOccurrence>();
        string? pageToken = null;
        for (var page = 0; page < MaximumPages; page++)
        {
            var parameters = new Dictionary<string, string> {
                ["maxResults"] = "2500",
                ["singleEvents"] = "true",
                ["orderBy"] = "startTime",
                ["showDeleted"] = "false",
                ["timeMin"] = new DateTimeOffset(rangeStart).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["timeMax"] = new DateTimeOffset(rangeEnd).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            };
            if (pageToken is { Length: > 0 })
            {
                parameters["pageToken"] = pageToken;
            }

            var response = await GetAsync<GoogleEventListResponse>(
                httpClient,
                BuildUri(endpoint, parameters),
                accessToken,
                GoogleCalendarJsonContext.Default.GoogleEventListResponse,
                cancellationToken);
            foreach (var item in response.Items ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(item.Status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                    !TryReadPeriod(item.Start, item.End, out var start, out var end, out var allDay))
                {
                    continue;
                }

                occurrences.Add(new CalendarOccurrence(
                    string.IsNullOrWhiteSpace(item.Summary) ? "Untitled event" : item.Summary.Trim(),
                    start,
                    end,
                    allDay,
                    string.IsNullOrWhiteSpace(item.Location) ? null : item.Location.Trim(),
                    SourceKey(calendar.Id)));
                if (occurrences.Count > maximumOccurrences)
                {
                    throw new InvalidDataException(
                        $"Calendar contains more than {maximumOccurrences:N0} occurrences in the supported range.");
                }
            }

            pageToken = response.NextPageToken;
            if (string.IsNullOrEmpty(pageToken))
            {
                return occurrences.ToArray();
            }
        }

        throw new InvalidDataException("Google Calendar returned too many pages of events.");
    }

    internal static string SourceKey(string calendarId) => "google:" + calendarId;

    private static bool TryReadPeriod(
        GoogleEventDateTime? startValue,
        GoogleEventDateTime? endValue,
        out DateTime start,
        out DateTime end,
        out bool allDay)
    {
        start = default;
        end = default;
        allDay = false;
        if (startValue is null || endValue is null)
        {
            return false;
        }

        if (startValue.Date is { Length: > 0 } startDate && endValue.Date is { Length: > 0 } endDate)
        {
            if (!DateTime.TryParseExact(
                    startDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out start) ||
                !DateTime.TryParseExact(
                    endDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out end))
            {
                return false;
            }

            allDay = true;
            start = DateTime.SpecifyKind(start, DateTimeKind.Local);
            end = DateTime.SpecifyKind(end, DateTimeKind.Local);
            return end >= start;
        }

        return TryReadDateTime(startValue, out start) &&
            TryReadDateTime(endValue, out end) &&
            end >= start;
    }

    private static bool TryReadDateTime(GoogleEventDateTime value, out DateTime result)
    {
        result = default;
        if (value.DateTime is not { Length: > 0 } text)
        {
            return false;
        }

        var hasExplicitOffset = text.EndsWith('Z') || text.LastIndexOf('+') > 10 || text.LastIndexOf('-') > 10;
        if (hasExplicitOffset && DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var offsetValue))
        {
            result = offsetValue.LocalDateTime;
            return true;
        }

        if (value.TimeZone is not { Length: > 0 } timeZoneId ||
            !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localValue))
        {
            return false;
        }

        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            localValue = DateTime.SpecifyKind(localValue, DateTimeKind.Unspecified);
            if (timeZone.IsInvalidTime(localValue))
            {
                return false;
            }

            result = TimeZoneInfo.ConvertTimeToUtc(localValue, timeZone).ToLocalTime();
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static async Task<T> GetAsync<T>(
        HttpClient httpClient,
        Uri uri,
        string accessToken,
        global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Google Calendar request failed ({(int)response.StatusCode} {response.ReasonPhrase}).",
                null,
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken)
            ?? throw new InvalidDataException("Google Calendar returned an empty response.");
    }

    private static Uri BuildUri(Uri endpoint, IReadOnlyDictionary<string, string> parameters) => new(
        endpoint + "?" + string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
}

internal sealed record GoogleCalendarInfo(string Id, string Name, bool Primary, bool Hidden);

internal sealed record GoogleCalendarListResponse(
    [property: JsonPropertyName("items")] List<GoogleCalendarListEntry>? Items,
    [property: JsonPropertyName("nextPageToken")] string? NextPageToken);

internal sealed record GoogleCalendarListEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("primary")] bool Primary,
    [property: JsonPropertyName("hidden")] bool Hidden);

internal sealed record GoogleEventListResponse(
    [property: JsonPropertyName("items")] List<GoogleCalendarEvent>? Items,
    [property: JsonPropertyName("nextPageToken")] string? NextPageToken);

internal sealed record GoogleCalendarEvent(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("start")] GoogleEventDateTime? Start,
    [property: JsonPropertyName("end")] GoogleEventDateTime? End);

internal sealed record GoogleEventDateTime(
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("dateTime")] string? DateTime,
    [property: JsonPropertyName("timeZone")] string? TimeZone);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GoogleCalendarListResponse))]
[JsonSerializable(typeof(GoogleEventListResponse))]
internal sealed partial class GoogleCalendarJsonContext : JsonSerializerContext;
