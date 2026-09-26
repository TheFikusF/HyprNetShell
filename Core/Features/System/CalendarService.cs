using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using HyprNetShell.Core.Configuration;
using HyprNetShell.Core.Features.OnlineAccounts;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Services;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

namespace HyprNetShell.Core.Features.System;

internal sealed class CalendarService : IBarDataService, IDisposable
{
    private sealed record SourceData(
        CalendarOccurrence[] Occurrences,
        CalendarSourceStatus Status);

    private sealed record SourceRefreshResult(
        string SourceKey,
        CalendarOccurrence[]? Occurrences,
        string? Error,
        DateTime CompletedAt);

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private const int MaximumOccurrencesPerSource = 50_000;

    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AppConfigurationStore _configuration = AppConfigurationStore.Shared;
    private readonly OnlineAccountsService _accounts;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, SourceData> _sourceData = new(StringComparer.Ordinal);

    private string[] _urls;
    private string? _googleAccountId;
    private string[]? _selectedGoogleCalendarIds;
    private GoogleCalendarInfo[] _googleCalendars = [];
    private string? _googleError;
    private CalendarSnapshot _snapshot = CalendarSnapshot.Empty;
    private DateTime _nextRefreshUtc = DateTime.MinValue;
    private int _activeRefreshes;
    private volatile bool _disposed;

    public CalendarService(OnlineAccountsService accounts)
    {
        _accounts = accounts;
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 16 * 1024 * 1024,
        };
        var configuration = _configuration.Snapshot.Calendars;
        _urls = NormalizeConfiguredUrls(configuration?.Urls);
        _accounts.AccountChanged += HandleAccountChanged;
        PublishSnapshotLocked(null, DateTime.MinValue, DateTime.MinValue);
    }

    public CalendarSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public IReadOnlyList<string> Urls
    {
        get
        {
            lock (_stateLock)
            {
                return _urls.ToArray();
            }
        }
    }

    public IReadOnlyList<GoogleCalendarSource> GoogleCalendars
    {
        get
        {
            lock (_stateLock)
            {
                var enabled = (_selectedGoogleCalendarIds ?? []).ToHashSet(StringComparer.Ordinal);
                return _googleCalendars
                    .Select(calendar => new GoogleCalendarSource(
                        calendar.Id,
                        calendar.Name,
                        calendar.Primary,
                        calendar.Hidden,
                        enabled.Contains(calendar.Id)))
                    .ToArray();
            }
        }
    }

    public bool GoogleAccountConnected => _accounts.Snapshot
        .First(snapshot => snapshot.Provider == OnlineAccountProvider.Google)
        .Connected;

    public int ConfiguredSourceCount
    {
        get
        {
            lock (_stateLock)
            {
                var availableIds = _googleCalendars.Select(calendar => calendar.Id).ToHashSet(StringComparer.Ordinal);
                var enabledGoogleCount = (_selectedGoogleCalendarIds ?? []).Count(availableIds.Contains);
                return _urls.Length + enabledGoogleCount;
            }
        }
    }

    public bool IsRefreshing => Volatile.Read(ref _activeRefreshes) > 0;

    public string? Status
    {
        get
        {
            var snapshot = Snapshot;
            string? googleError;
            lock (_stateLock)
            {
                googleError = _googleError;
            }
            if (googleError is not null)
            {
                return $"Google Calendar failed: {googleError}";
            }

            var failedSources = snapshot.Sources.Count(source => source.State == CalendarSourceState.Error);
            if (failedSources > 0)
            {
                var firstError = snapshot.Sources.First(source => source.State == CalendarSourceState.Error).Error;
                return $"{failedSources} calendar source{(failedSources == 1 ? "" : "s")} failed: {firstError}";
            }

            return snapshot.UpdatedAt is { } updatedAt ? $"Updated {updatedAt:t}" : null;
        }
    }

    public ValueTask RefreshAsync(CancellationToken cancellationToken) =>
        new(TrackRefreshAsync(force: false, cancellationToken));

    public Task ForceRefreshAsync(CancellationToken cancellationToken = default) =>
        TrackRefreshAsync(force: true, cancellationToken);

    public bool AddUrl(string url, [NotNullWhen(false)] out string? error) => TryAddUrl(url, out error);

    public bool TryAddUrl(string url, [NotNullWhen(false)] out string? error)
    {
        if (!TryNormalizeUrl(url, out var normalizedUrl, out error))
        {
            return false;
        }

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_urls.Contains(normalizedUrl, StringComparer.Ordinal))
            {
                error = "Calendar URL is already configured.";
                return false;
            }

            _urls = [.. _urls, normalizedUrl];
            PersistUrlsLocked();
            PublishSnapshotLocked(_snapshot.UpdatedAt, _snapshot.RangeStart, _snapshot.RangeEnd);
        }

        return true;
    }

    public bool SetGoogleCalendarEnabled(string calendarId, bool enabled)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_googleCalendars.Any(calendar => calendar.Id == calendarId))
            {
                return false;
            }

            var selected = (_selectedGoogleCalendarIds ?? []).ToHashSet(StringComparer.Ordinal);
            if (!enabled)
            {
                selected.Remove(calendarId);
                _sourceData.Remove(GoogleCalendarClient.SourceKey(calendarId));
            }
            else
            {
                selected.Add(calendarId);
            }

            _selectedGoogleCalendarIds = selected.ToArray();
            PersistGoogleCalendarsLocked();
            PublishSnapshotLocked(_snapshot.UpdatedAt, _snapshot.RangeStart, _snapshot.RangeEnd);
        }

        if (enabled)
        {
            _ = RefreshAfterAccountChangedAsync();
        }
        return true;
    }

    public bool RemoveUrl(string url)
    {
        if (!TryNormalizeUrl(url, out var normalizedUrl, out _))
        {
            return false;
        }

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var remainingUrls = _urls.Where(configured => configured != normalizedUrl).ToArray();
            if (remainingUrls.Length == _urls.Length)
            {
                return false;
            }

            _urls = remainingUrls;
            _sourceData.Remove(normalizedUrl);
            PersistUrlsLocked();
            PublishSnapshotLocked(_snapshot.UpdatedAt, _snapshot.RangeStart, _snapshot.RangeEnd);
            return true;
        }
    }

    public bool TryReplaceUrls(IEnumerable<string> urls, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(urls);

        var normalizedUrls = new List<string>();
        foreach (var url in urls)
        {
            if (!TryNormalizeUrl(url, out var normalizedUrl, out error))
            {
                return false;
            }

            if (!normalizedUrls.Contains(normalizedUrl, StringComparer.Ordinal))
            {
                normalizedUrls.Add(normalizedUrl);
            }
        }

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _urls = normalizedUrls.ToArray();
            var configuredUrls = _urls.ToHashSet(StringComparer.Ordinal);
            foreach (var removedUrl in _sourceData.Keys.Where(url => !configuredUrls.Contains(url)).ToArray())
            {
                _sourceData.Remove(removedUrl);
            }

            PersistUrlsLocked();
            PublishSnapshotLocked(_snapshot.UpdatedAt, _snapshot.RangeStart, _snapshot.RangeEnd);
        }

        error = null;
        return true;
    }

    private async Task TrackRefreshAsync(bool force, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeRefreshes);
        try
        {
            await RefreshCoreAsync(force, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _activeRefreshes);
        }
    }

    private async Task RefreshCoreAsync(bool force, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var token = linkedCancellation.Token;

        await _refreshGate.WaitAsync(token);
        try
        {
            string[] urls;
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!force && DateTime.UtcNow < _nextRefreshUtc)
                {
                    return;
                }

                _nextRefreshUtc = DateTime.UtcNow + RefreshInterval;
                urls = _urls;
            }

            var now = DateTime.Now;
            var rangeStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Local).AddMonths(-12);
            var rangeEnd = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Local).AddMonths(19);
            var urlResultsTask = Task.WhenAll(
                urls.Select(url => RefreshSourceAsync(url, rangeStart, rangeEnd, token)));
            var googleResultsTask = RefreshGoogleSourcesAsync(rangeStart, rangeEnd, token);

            var urlResults = await urlResultsTask;
            ApplyRefreshResults(urlResults, rangeStart, rangeEnd);

            var googleResults = await googleResultsTask;
            ApplyRefreshResults(googleResults, rangeStart, rangeEnd);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<SourceRefreshResult[]> RefreshGoogleSourcesAsync(
        DateTime rangeStart,
        DateTime rangeEnd,
        CancellationToken cancellationToken)
    {
        if (!GoogleAccountConnected)
        {
            ClearGoogleAccountState();
            return [];
        }

        try
        {
            using var credentialCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            credentialCancellation.CancelAfter(RequestTimeout);
            var credential = await _accounts.GetGoogleCalendarAccessCredentialAsync(
                credentialCancellation.Token);
            if (credential is null)
            {
                SetGoogleError("Could not obtain a Google Calendar access token.");
                return [];
            }

            var calendars = await RefreshGoogleCalendarsAsync(credential, cancellationToken);
            return await Task.WhenAll(calendars.Select(calendar =>
                RefreshGoogleSourceAsync(
                    credential.AccessToken,
                    calendar,
                    rangeStart,
                    rangeEnd,
                    cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            SetGoogleError($"Token request timed out after {RequestTimeout.TotalSeconds:0} seconds.");
            return [];
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Calendar", "Could not obtain a Google Calendar access token", exception);
            SetGoogleError(exception.Message);
            return [];
        }
    }

    private void ApplyRefreshResults(
        IReadOnlyList<SourceRefreshResult> results,
        DateTime rangeStart,
        DateTime rangeEnd)
    {
        var completedAt = DateTime.Now;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            var currentlyConfigured = _urls
                .Concat((_selectedGoogleCalendarIds ?? []).Select(GoogleCalendarClient.SourceKey))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var result in results.Where(result => currentlyConfigured.Contains(result.SourceKey)))
            {
                if (result.Occurrences is not null)
                {
                    _sourceData[result.SourceKey] = new SourceData(
                        result.Occurrences,
                        new CalendarSourceStatus(
                            result.SourceKey,
                            CalendarSourceState.Success,
                            null,
                            result.CompletedAt));
                }
                else
                {
                    var previous = _sourceData.GetValueOrDefault(result.SourceKey);
                    _sourceData[result.SourceKey] = new SourceData(
                        previous?.Occurrences ?? [],
                        new CalendarSourceStatus(
                            result.SourceKey,
                            CalendarSourceState.Error,
                            result.Error,
                            previous?.Status.UpdatedAt));
                }
            }

            PublishSnapshotLocked(completedAt, rangeStart, rangeEnd);
        }
    }

    private async Task<GoogleCalendarInfo[]> RefreshGoogleCalendarsAsync(
        GoogleCalendarAccessCredential credential,
        CancellationToken cancellationToken)
    {
        try
        {
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCancellation.CancelAfter(RequestTimeout);
            var calendars = await GoogleCalendarClient.GetCalendarsAsync(
                _httpClient,
                credential.AccessToken,
                requestCancellation.Token);
            lock (_stateLock)
            {
                _googleCalendars = calendars;
                _googleError = null;
                if (_googleAccountId != credential.AccountId)
                {
                    RemoveGoogleSourceDataLocked([]);
                    _googleAccountId = credential.AccountId;
                    var configuredByAccount = _configuration.Snapshot.Calendars.GoogleCalendarIdsByAccount;
                    _selectedGoogleCalendarIds = configuredByAccount.TryGetValue(credential.AccountId, out var configured)
                        ? configured.Distinct(StringComparer.Ordinal).ToArray()
                        : calendars.Where(calendar => !calendar.Hidden).Select(calendar => calendar.Id).ToArray();
                    if (!configuredByAccount.ContainsKey(credential.AccountId))
                    {
                        PersistGoogleCalendarsLocked();
                    }
                }

                var enabled = (_selectedGoogleCalendarIds ?? []).ToHashSet(StringComparer.Ordinal);
                var enabledCalendars = calendars.Where(calendar => enabled.Contains(calendar.Id)).ToArray();
                RemoveGoogleSourceDataLocked(enabledCalendars.Select(calendar => calendar.Id));
                return enabledCalendars;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var error = exception is OperationCanceledException
                ? $"Request timed out after {RequestTimeout.TotalSeconds:0} seconds."
                : exception.Message;
            AppLogger.Warning("Calendar", "Could not list Google calendars", exception);
            lock (_stateLock)
            {
                _googleError = error;
                var enabled = (_selectedGoogleCalendarIds ?? []).ToHashSet(StringComparer.Ordinal);
                return _googleCalendars.Where(calendar => enabled.Contains(calendar.Id)).ToArray();
            }
        }
    }

    private async Task<SourceRefreshResult> RefreshSourceAsync(
        string sourceUrl,
        DateTime rangeStart,
        DateTime rangeEnd,
        CancellationToken cancellationToken)
    {
        try
        {
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCancellation.CancelAfter(RequestTimeout);
            var calendarText = await _httpClient.GetStringAsync(sourceUrl, requestCancellation.Token);
            cancellationToken.ThrowIfCancellationRequested();
            var occurrences = await Task.Run(
                () => ParseOccurrences(calendarText, sourceUrl, rangeStart, rangeEnd, cancellationToken),
                cancellationToken);
            return new SourceRefreshResult(sourceUrl, occurrences, null, DateTime.Now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var error = exception is OperationCanceledException
                ? $"Request timed out after {RequestTimeout.TotalSeconds:0} seconds."
                : exception.Message;
            AppLogger.Warning("Calendar", $"Could not refresh calendar source at {GetLogLabel(sourceUrl)}", exception);
            return new SourceRefreshResult(sourceUrl, null, error, DateTime.Now);
        }
    }

    private async Task<SourceRefreshResult> RefreshGoogleSourceAsync(
        string accessToken,
        GoogleCalendarInfo calendar,
        DateTime rangeStart,
        DateTime rangeEnd,
        CancellationToken cancellationToken)
    {
        var sourceKey = GoogleCalendarClient.SourceKey(calendar.Id);
        try
        {
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCancellation.CancelAfter(RequestTimeout);
            var occurrences = await GoogleCalendarClient.GetOccurrencesAsync(
                _httpClient,
                accessToken,
                calendar,
                rangeStart,
                rangeEnd,
                MaximumOccurrencesPerSource,
                requestCancellation.Token);
            return new SourceRefreshResult(sourceKey, occurrences, null, DateTime.Now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var error = exception is OperationCanceledException
                ? $"Request timed out after {RequestTimeout.TotalSeconds:0} seconds."
                : exception.Message;
            AppLogger.Warning("Calendar", $"Could not refresh Google calendar '{calendar.Name}'", exception);
            return new SourceRefreshResult(sourceKey, null, error, DateTime.Now);
        }
    }

    private static CalendarOccurrence[] ParseOccurrences(
        string calendarText,
        string sourceUrl,
        DateTime rangeStart,
        DateTime rangeEnd,
        CancellationToken cancellationToken)
    {
        var calendars = CalendarCollection.Load(calendarText);
        if (calendars.Count == 0)
        {
            throw new InvalidDataException("The response did not contain an iCalendar.");
        }

        var queryStart = rangeStart.AddDays(-31).ToUniversalTime();
        var evaluationOptions = new EvaluationOptions { MaxUnmatchedIncrementsLimit = 100_000 };
        var occurrences = new List<CalendarOccurrence>();
        foreach (var occurrence in calendars.GetOccurrences<CalendarEvent>(new CalDateTime(queryStart), evaluationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var calendarEvent = (CalendarEvent)occurrence.Source;
            if (!calendarEvent.IsActive)
            {
                continue;
            }

            var start = ToLocalDateTime(occurrence.Period.StartTime);
            if (start >= rangeEnd)
            {
                break;
            }

            var end = occurrence.Period.EffectiveEndTime is { } effectiveEnd
                ? ToLocalDateTime(effectiveEnd)
                : start;
            if (end < rangeStart || (end == rangeStart && start != end))
            {
                continue;
            }

            occurrences.Add(new CalendarOccurrence(
                string.IsNullOrWhiteSpace(calendarEvent.Summary) ? "Untitled event" : calendarEvent.Summary.Trim(),
                start,
                end,
                calendarEvent.IsAllDay,
                string.IsNullOrWhiteSpace(calendarEvent.Location) ? null : calendarEvent.Location.Trim(),
                sourceUrl));
            if (occurrences.Count >= MaximumOccurrencesPerSource)
            {
                throw new InvalidDataException(
                    $"Calendar contains more than {MaximumOccurrencesPerSource:N0} occurrences in the supported range.");
            }
        }

        return occurrences
            .OrderBy(occurrence => occurrence.Start)
            .ThenBy(occurrence => occurrence.End)
            .ThenBy(occurrence => occurrence.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DateTime ToLocalDateTime(CalDateTime value)
    {
        if (!value.HasTime || value.IsFloating)
        {
            return DateTime.SpecifyKind(value.Value, DateTimeKind.Local);
        }

        return value.AsUtc.ToLocalTime();
    }

    private void PersistUrlsLocked()
    {
        var persistedUrls = _urls.ToList();
        _configuration.Update(configuration =>
        {
            configuration.Calendars ??= new CalendarConfiguration();
            configuration.Calendars.Urls = persistedUrls;
        });
    }

    private void PersistGoogleCalendarsLocked()
    {
        if (_googleAccountId is not { } accountId || _selectedGoogleCalendarIds is not { } selectedIds)
        {
            return;
        }

        var persistedIds = selectedIds.ToList();
        _configuration.Update(configuration =>
        {
            configuration.Calendars ??= new CalendarConfiguration();
            configuration.Calendars.GoogleCalendarIdsByAccount ??= [];
            configuration.Calendars.GoogleCalendarIdsByAccount[accountId] = persistedIds;
        });
    }

    private void SetGoogleError(string error)
    {
        lock (_stateLock)
        {
            _googleError = error;
        }
    }

    private void ClearGoogleAccountState()
    {
        lock (_stateLock)
        {
            _googleAccountId = null;
            _selectedGoogleCalendarIds = null;
            _googleCalendars = [];
            _googleError = null;
            RemoveGoogleSourceDataLocked([]);
        }
    }

    private void RemoveGoogleSourceDataLocked(IEnumerable<string> selectedIds)
    {
        var selectedKeys = selectedIds.Select(GoogleCalendarClient.SourceKey).ToHashSet(StringComparer.Ordinal);
        foreach (var sourceKey in _sourceData.Keys
                     .Where(key => key.StartsWith("google:", StringComparison.Ordinal) && !selectedKeys.Contains(key))
                     .ToArray())
        {
            _sourceData.Remove(sourceKey);
        }
    }

    private void PublishSnapshotLocked(DateTime? updatedAt, DateTime rangeStart, DateTime rangeEnd)
    {
        var availableGoogleIds = _googleCalendars
            .Select(calendar => calendar.Id)
            .ToHashSet(StringComparer.Ordinal);
        var sourceKeys = _urls
            .Concat((_selectedGoogleCalendarIds ?? [])
                .Where(availableGoogleIds.Contains)
                .Select(GoogleCalendarClient.SourceKey))
            .ToArray();
        var sources = new CalendarSourceStatus[sourceKeys.Length];
        var occurrences = new List<CalendarOccurrence>();
        for (var index = 0; index < sourceKeys.Length; index++)
        {
            var sourceKey = sourceKeys[index];
            if (_sourceData.TryGetValue(sourceKey, out var source))
            {
                sources[index] = source.Status;
                occurrences.AddRange(source.Occurrences);
            }
            else
            {
                sources[index] = new CalendarSourceStatus(sourceKey, CalendarSourceState.Pending, null, null);
            }
        }

        Volatile.Write(ref _snapshot, new CalendarSnapshot(
            occurrences
                .OrderBy(occurrence => occurrence.Start)
                .ThenBy(occurrence => occurrence.End)
                .ThenBy(occurrence => occurrence.Title, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray(),
            sources.ToImmutableArray(),
            rangeStart,
            rangeEnd,
            updatedAt));
    }

    private static string[] NormalizeConfiguredUrls(IEnumerable<string>? urls) =>
        urls?.Select(url => TryNormalizeUrl(url, out var normalized, out _) ? normalized : null)
            .Where(url => url is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

    private static string GetLogLabel(string sourceUrl)
    {
        var uri = new Uri(sourceUrl);
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static bool TryNormalizeUrl(
        string? url,
        out string normalizedUrl,
        [NotNullWhen(false)] out string? error)
    {
        normalizedUrl = "";
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Calendar URL must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        error = null;
        return true;
    }

    private void HandleAccountChanged(OnlineAccountProvider provider)
    {
        if (provider == OnlineAccountProvider.Google && !_disposed)
        {
            _ = RefreshAfterAccountChangedAsync();
        }
    }

    private async Task RefreshAfterAccountChangedAsync()
    {
        try
        {
            await ForceRefreshAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Calendar", "Could not refresh calendars after the Google account changed", exception);
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _accounts.AccountChanged -= HandleAccountChanged;
        _lifetime.Cancel();
        _httpClient.Dispose();
        _lifetime.Dispose();
    }
}
