using System.Net.Http.Headers;
using System.Text.Json;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.OnlineAccounts;

internal sealed class ChatGptUsageService : IBarDataService, IDisposable
{
    private static readonly Uri UsageEndpoint = new("https://chatgpt.com/backend-api/wham/usage");

    private readonly OnlineAccountsService _accounts;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HttpClient _httpClient = new() {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = 256 * 1024,
    };
    private ChatGptUsageSnapshot _snapshot = ChatGptUsageSnapshot.Disconnected;
    private bool _disposed;

    internal ChatGptUsageService(OnlineAccountsService accounts)
    {
        _accounts = accounts;
        _accounts.AccountChanged += HandleAccountChanged;
    }

    internal ChatGptUsageSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var credential = await _accounts.GetChatGptAccessCredentialAsync(cancellationToken);
        if (credential is null)
        {
            Volatile.Write(ref _snapshot, ChatGptUsageSnapshot.Disconnected);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        if (!string.IsNullOrWhiteSpace(credential.AccountId) && credential.AccountId != "chatgpt")
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
        }

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Warning("ChatGptUsage", $"Usage request failed ({(int)response.StatusCode})");
                PublishFailure($"Usage unavailable ({(int)response.StatusCode})");
                return;
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var windows = ReadWindows(root);
            var plan = root.TryGetProperty("plan_type", out var planProperty)
                ? planProperty.GetString()
                : null;
            Volatile.Write(ref _snapshot, new ChatGptUsageSnapshot(
                true,
                plan,
                windows,
                DateTimeOffset.Now,
                windows.Count == 0 ? "No limits returned" : null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLogger.Warning("ChatGptUsage", "Could not refresh ChatGPT limits; keeping the previous values", exception);
            PublishFailure("Usage temporarily unavailable");
        }
    }

    private static IReadOnlyList<ChatGptLimitWindow> ReadWindows(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limit", out var rateLimit) || rateLimit.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var windows = new List<ChatGptLimitWindow>(2);
        AddWindow(windows, rateLimit, "primary_window", "5-hour limit");
        AddWindow(windows, rateLimit, "secondary_window", "Weekly limit");
        return windows;
    }

    private static void AddWindow(
        ICollection<ChatGptLimitWindow> windows,
        JsonElement rateLimit,
        string propertyName,
        string fallbackLabel)
    {
        if (!rateLimit.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("used_percent", out var usedProperty) || !usedProperty.TryGetDouble(out var used))
        {
            return;
        }

        var duration = window.TryGetProperty("limit_window_seconds", out var durationProperty) &&
            durationProperty.TryGetInt64(out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : (TimeSpan?)null;
        var label = duration switch {
            { TotalDays: >= 6 } => "Weekly limit",
            { TotalHours: >= 1 } value => $"{Math.Round(value.TotalHours):0}-hour limit",
            _ => fallbackLabel,
        };
        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("reset_at", out var resetProperty) && resetProperty.TryGetInt64(out var resetUnix))
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetUnix).ToLocalTime();
        }

        windows.Add(new ChatGptLimitWindow(label, Math.Clamp(used, 0, 100), resetsAt));
    }

    private void PublishFailure(string status)
    {
        var current = Snapshot;
        Volatile.Write(ref _snapshot, current with {
            Connected = true,
            Status = status
        });
    }

    private void HandleAccountChanged(OnlineAccountProvider provider)
    {
        if (provider == OnlineAccountProvider.ChatGpt)
        {
            Volatile.Write(ref _snapshot, ChatGptUsageSnapshot.Disconnected);
            _ = RefreshAfterAccountChangedAsync();
        }
    }

    private async Task RefreshAfterAccountChangedAsync()
    {
        try
        {
            await RefreshAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Expected when the service shuts down.
        }
        catch (Exception exception)
        {
            AppLogger.Warning("ChatGptUsage", "Could not refresh usage after the account changed", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _accounts.AccountChanged -= HandleAccountChanged;
        _lifetime.Cancel();
        _httpClient.Dispose();
        _lifetime.Dispose();
    }
}
