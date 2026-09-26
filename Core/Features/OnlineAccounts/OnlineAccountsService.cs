using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyprNetShell.Core.Configuration;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Platform;

namespace HyprNetShell.Core.Features.OnlineAccounts;

internal enum OnlineAccountProvider
{
    Google,
    Spotify,
    ChatGpt,
}

internal sealed record OnlineAccountSnapshot(
    OnlineAccountProvider Provider,
    string Name,
    string Description,
    bool Configured,
    bool Connected,
    string? AccountName,
    string ConfiguredClientId,
    string? ClientIdSource,
    string? Status);

internal sealed class OnlineAccountsService : IDisposable
{
    private const string GoogleClientIdVariable = "HYPRNETSHELL_GOOGLE_CLIENT_ID";
    private const string GoogleClientSecretVariable = "HYPRNETSHELL_GOOGLE_CLIENT_SECRET";
    private const string SpotifyClientIdVariable = "HYPRNETSHELL_SPOTIFY_CLIENT_ID";
    private const string OpenAiClientIdVariable = "HYPRNETSHELL_OPENAI_CLIENT_ID";
    private const string GoogleClientIdMetadata = "HyprNetShellGoogleClientId";
    private const string GoogleClientSecretMetadata = "HyprNetShellGoogleClientSecret";
    private const string SpotifyClientIdMetadata = "HyprNetShellSpotifyClientId";
    private const string OpenAiClientIdMetadata = "HyprNetShellOpenAiClientId";
    private const int SpotifyCallbackPort = 5543;
    private const string GoogleCalendarScopes = "https://www.googleapis.com/auth/calendar.readonly";
    private const string SpotifyPlaybackScopes =
        "user-read-playback-state user-read-currently-playing user-modify-playback-state";

    private static readonly IReadOnlyDictionary<string, string> EmbeddedClientIds =
        typeof(OnlineAccountsService).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Value is { Length: > 0 })
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value!, StringComparer.Ordinal);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly Lock _stateLock = new();
    private readonly AppConfigurationStore _configuration = AppConfigurationStore.Shared;
    private readonly ICredentialStore _credentialStore;
    private readonly UrlLauncher _urlLauncher;
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly Dictionary<OnlineAccountProvider, StoredAccountCredential?> _credentials = [];
    private readonly Dictionary<OnlineAccountProvider, string?> _statuses = [];
    private Task? _initializationTask;
    private OnlineAccountProvider? _busyProvider;
    private bool _disposed;

    internal event Action? AuthorizationCallbackReceived;
    internal event Action<OnlineAccountProvider>? AccountChanged;

    internal OnlineAccountsService(UrlLauncher urlLauncher, ICredentialStore? credentialStore = null)
    {
        _urlLauncher = urlLauncher;
        _credentialStore = credentialStore ?? new SecretServiceCredentialStore();
        foreach (var provider in Enum.GetValues<OnlineAccountProvider>())
        {
            _credentials[provider] = null;
            _statuses[provider] = null;
        }
    }

    internal OnlineAccountProvider? BusyProvider
    {
        get
        {
            lock (_stateLock)
            {
                return _busyProvider;
            }
        }
    }

    internal IReadOnlyList<OnlineAccountSnapshot> Snapshot
    {
        get
        {
            lock (_stateLock)
            {
                return
                [
                    BuildSnapshotLocked(OnlineAccountProvider.Google),
                    BuildSnapshotLocked(OnlineAccountProvider.Spotify),
                    BuildSnapshotLocked(OnlineAccountProvider.ChatGpt),
                ];
            }
        }
    }

    internal void EnsureInitialized()
    {
        lock (_stateLock)
        {
            if (_initializationTask is null)
            {
                _initializationTask = InitializeAsync(_lifetime.Token);
            }
        }
    }

    internal async Task<GoogleCalendarAccessCredential?> GetGoogleCalendarAccessCredentialAsync(
        CancellationToken cancellationToken)
    {
        var credential = await GetAccessCredentialAsync(
            OnlineAccountProvider.Google,
            GoogleCalendarScopes,
            cancellationToken);
        return credential is null
            ? null
            : new GoogleCalendarAccessCredential(credential.AccessToken, credential.AccountId);
    }

    internal async Task<string?> GetSpotifyAccessTokenAsync(CancellationToken cancellationToken) =>
        (await GetAccessCredentialAsync(OnlineAccountProvider.Spotify, SpotifyPlaybackScopes, cancellationToken))
        ?.AccessToken;

    internal async Task<ChatGptAccessCredential?> GetChatGptAccessCredentialAsync(
        CancellationToken cancellationToken)
    {
        var credential = await GetAccessCredentialAsync(OnlineAccountProvider.ChatGpt, null, cancellationToken);
        return credential is null
            ? null
            : new ChatGptAccessCredential(credential.AccessToken, credential.AccountId);
    }

    private async Task<StoredAccountCredential?> GetAccessCredentialAsync(
        OnlineAccountProvider provider,
        string? requiredScopes,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        Task? initializationTask;
        lock (_stateLock)
        {
            initializationTask = _initializationTask;
        }

        if (initializationTask is not null)
        {
            await initializationTask.WaitAsync(cancellationToken);
        }

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            StoredAccountCredential? credential;
            lock (_stateLock)
            {
                credential = _credentials[provider];
            }

            if (credential is null ||
                requiredScopes is { Length: > 0 } && !HasScopes(credential.Scopes, requiredScopes))
            {
                return null;
            }

            if (credential.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return credential;
            }

            var definition = CreateDefinition(provider);
            if (string.IsNullOrWhiteSpace(credential.RefreshToken) || string.IsNullOrWhiteSpace(definition.ClientId))
            {
                return null;
            }

            var parameters = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = definition.ClientId,
                ["refresh_token"] = credential.RefreshToken,
            };
            AddClientSecret(parameters, definition.ClientSecret);
            using var request = new HttpRequestMessage(HttpMethod.Post, definition.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(parameters),
            };
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Warning(
                    "OnlineAccounts",
                    $"{definition.Name} token refresh failed ({(int)response.StatusCode})");
                return null;
            }

            var token = await response.Content.ReadFromJsonAsync(
                OnlineAccountsJsonContext.Default.OAuthTokenResponse,
                cancellationToken);
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                AppLogger.Warning("OnlineAccounts", $"{definition.Name} token refresh returned no access token");
                return null;
            }

            var updated = credential with
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken ?? credential.RefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn)),
                Scopes = token.Scope ?? credential.Scopes,
            };
            var json = JsonSerializer.Serialize(updated, OnlineAccountsJsonContext.Default.StoredAccountCredential);
            await _credentialStore.WriteAsync(StorageKey(provider), json, cancellationToken);
            lock (_stateLock)
            {
                _credentials[provider] = updated;
            }

            return updated;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    internal void SetConfiguredClientId(OnlineAccountProvider provider, string clientId)
    {
        clientId = clientId.Trim();
        _configuration.Update(configuration =>
        {
            var accounts = configuration.OnlineAccounts;
            switch (provider)
            {
                case OnlineAccountProvider.Google:
                    accounts.GoogleClientId = clientId;
                    break;
                case OnlineAccountProvider.Spotify:
                    accounts.SpotifyClientId = clientId;
                    break;
                case OnlineAccountProvider.ChatGpt:
                    accounts.OpenAiClientId = clientId;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(provider));
            }
        });

        lock (_stateLock)
        {
            var (_, source) = ResolveClientId(provider);
            _statuses[provider] = source switch
            {
                "Configuration" => "Client ID saved.",
                null => "Configured client ID cleared.",
                _ => $"Client ID saved; the {source.ToLowerInvariant()} value still takes precedence.",
            };
        }
    }

    internal void Connect(OnlineAccountProvider provider)
    {
        lock (_stateLock)
        {
            if (_busyProvider is not null)
            {
                return;
            }

            _busyProvider = provider;
            _statuses[provider] = "Waiting for browser sign-in…";
        }

        _ = ConnectAsync(provider, _lifetime.Token);
    }

    internal void Disconnect(OnlineAccountProvider provider)
    {
        lock (_stateLock)
        {
            if (_busyProvider is not null)
            {
                return;
            }

            _busyProvider = provider;
            _statuses[provider] = "Removing saved credentials…";
        }

        _ = DisconnectAsync(provider, _lifetime.Token);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var loadedProviders = new List<OnlineAccountProvider>();
        foreach (var provider in Enum.GetValues<OnlineAccountProvider>())
        {
            try
            {
                var json = await _credentialStore.ReadAsync(StorageKey(provider), cancellationToken);
                var credential = json is null
                    ? null
                    : JsonSerializer.Deserialize(json, OnlineAccountsJsonContext.Default.StoredAccountCredential);
                lock (_stateLock)
                {
                    _credentials[provider] = credential;
                    _statuses[provider] = null;
                }
                if (credential is not null)
                {
                    loadedProviders.Add(provider);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                AppLogger.Warning("OnlineAccounts", $"Could not read {provider} credentials from Secret Service", exception);
                lock (_stateLock)
                {
                    _statuses[provider] = FriendlySecretServiceError(exception);
                }
            }
        }

        foreach (var provider in loadedProviders)
        {
            AccountChanged?.Invoke(provider);
        }
    }

    private async Task ConnectAsync(OnlineAccountProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            var definition = CreateDefinition(provider);
            if (string.IsNullOrWhiteSpace(definition.ClientId))
            {
                throw new InvalidOperationException(
                    "Configure a client ID in settings, the environment, or the published build first.");
            }

            var credential = await AuthorizeAsync(definition, cancellationToken);
            var json = JsonSerializer.Serialize(credential, OnlineAccountsJsonContext.Default.StoredAccountCredential);
            await _credentialStore.WriteAsync(StorageKey(provider), json, cancellationToken);

            lock (_stateLock)
            {
                _credentials[provider] = credential;
                _statuses[provider] = $"Connected as {credential.AccountName}.";
            }
            AccountChanged?.Invoke(provider);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the service shuts down.
        }
        catch (Exception exception)
        {
            AppLogger.Warning("OnlineAccounts", $"Could not connect {provider} account", exception);
            lock (_stateLock)
            {
                _statuses[provider] = exception.Message;
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _busyProvider = null;
            }
        }
    }

    private async Task DisconnectAsync(OnlineAccountProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            await _credentialStore.DeleteAsync(StorageKey(provider), cancellationToken);
            lock (_stateLock)
            {
                _credentials[provider] = null;
                _statuses[provider] = "Disconnected.";
            }
            AccountChanged?.Invoke(provider);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the service shuts down.
        }
        catch (Exception exception)
        {
            AppLogger.Warning("OnlineAccounts", $"Could not disconnect {provider} account", exception);
            lock (_stateLock)
            {
                _statuses[provider] = exception.Message;
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _busyProvider = null;
            }
        }
    }

    private async Task<StoredAccountCredential> AuthorizeAsync(
        OAuthProviderDefinition definition,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LoginTimeout);
        await using var callback = await OAuthCallbackListener.StartAsync(
            definition.FixedCallbackPorts,
            definition.CallbackHost,
            timeout.Token);

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(24));
        var authorizationUrl = BuildAuthorizationUrl(
            definition,
            callback.RedirectUri,
            state,
            nonce,
            challenge);

        _urlLauncher.Open(authorizationUrl);
        var code = await callback.WaitForCodeAsync(
            state,
            () => AuthorizationCallbackReceived?.Invoke(),
            timeout.Token);
        var token = await ExchangeCodeAsync(definition, callback.RedirectUri, code, verifier, timeout.Token);
        var identity = await ReadIdentityAsync(definition.Provider, token, timeout.Token);

        return new StoredAccountCredential(
            definition.Provider.ToString(),
            identity.AccountId,
            identity.AccountName,
            token.AccessToken,
            token.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn)),
            token.Scope ?? definition.Scopes);
    }

    private async Task<OAuthTokenResponse> ExchangeCodeAsync(
        OAuthProviderDefinition definition,
        string redirectUri,
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = definition.ClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        };
        AddClientSecret(parameters, definition.ClientSecret);
        using var request = new HttpRequestMessage(HttpMethod.Post, definition.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(parameters),
        };
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var providerError = ReadOAuthError(responseBody);
            if (definition.Provider == OnlineAccountProvider.Google &&
                definition.ClientSecret is null &&
                providerError?.Contains("client_secret is missing", StringComparison.OrdinalIgnoreCase) == true)
            {
                providerError =
                    $"client_secret is missing — set {GoogleClientSecretVariable} from the Desktop app credential JSON";
            }

            var detail = providerError is null ? "" : $": {providerError.TrimEnd().TrimEnd('.')}";
            throw new InvalidOperationException(
                $"{definition.Name} rejected the sign-in ({(int)response.StatusCode}){detail}.");
        }

        var token = await response.Content.ReadFromJsonAsync(
            OnlineAccountsJsonContext.Default.OAuthTokenResponse,
            cancellationToken);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException($"{definition.Name} returned an invalid token response.");
        }

        return token;
    }

    private async Task<AccountIdentity> ReadIdentityAsync(
        OnlineAccountProvider provider,
        OAuthTokenResponse token,
        CancellationToken cancellationToken)
    {
        if (provider == OnlineAccountProvider.ChatGpt)
        {
            return ReadOpenAiIdentity(token.IdToken);
        }

        var endpoint = provider == OnlineAccountProvider.Google
            ? "https://openidconnect.googleapis.com/v1/userinfo"
            : "https://api.spotify.com/v1/me";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Could not read the connected account ({(int)response.StatusCode}).");
        }

        if (provider == OnlineAccountProvider.Google)
        {
            var profile = await response.Content.ReadFromJsonAsync(
                OnlineAccountsJsonContext.Default.GoogleUserInfo,
                cancellationToken);
            if (profile is null || string.IsNullOrWhiteSpace(profile.Subject))
            {
                throw new InvalidOperationException("Google returned an invalid account profile.");
            }

            return new AccountIdentity(profile.Subject, profile.Email ?? profile.Name ?? "Google account");
        }

        var spotify = await response.Content.ReadFromJsonAsync(
            OnlineAccountsJsonContext.Default.SpotifyUserInfo,
            cancellationToken);
        var spotifyId = spotify?.AccountId ?? spotify?.Id;
        if (string.IsNullOrWhiteSpace(spotifyId))
        {
            throw new InvalidOperationException("Spotify returned an invalid account profile.");
        }

        return new AccountIdentity(spotifyId, spotify?.DisplayName ?? "Spotify account");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    }

    private OnlineAccountSnapshot BuildSnapshotLocked(OnlineAccountProvider provider)
    {
        var definition = CreateDefinition(provider);
        var credential = _credentials[provider];
        var clientId = ResolveClientId(provider);
        return new OnlineAccountSnapshot(
            provider,
            definition.Name,
            definition.Description,
            !string.IsNullOrWhiteSpace(definition.ClientId),
            credential is not null,
            credential?.AccountName,
            ConfiguredClientId(provider),
            clientId.Source,
            _statuses[provider]);
    }

    private OAuthProviderDefinition CreateDefinition(OnlineAccountProvider provider) => provider switch
    {
        OnlineAccountProvider.Google => new(
            provider,
            "Google",
            "Connect Google Calendar with read-only access.",
            ResolveClientId(provider).ClientId,
            ResolveGoogleClientSecret(),
            "https://accounts.google.com/o/oauth2/v2/auth",
            "https://oauth2.googleapis.com/token",
            $"openid email profile {GoogleCalendarScopes}",
            [],
            "127.0.0.1",
            new Dictionary<string, string>
            {
                ["access_type"] = "offline",
                ["prompt"] = "consent",
            }),
        OnlineAccountProvider.Spotify => new(
            provider,
            "Spotify",
            "Connect Spotify playback to show the queue and control shuffle and repeat.",
            ResolveClientId(provider).ClientId,
            null,
            "https://accounts.spotify.com/authorize",
            "https://accounts.spotify.com/api/token",
            SpotifyPlaybackScopes,
            [SpotifyCallbackPort],
            "127.0.0.1",
            new Dictionary<string, string>()),
        OnlineAccountProvider.ChatGpt => new(
            provider,
            "ChatGPT",
            "Connect a ChatGPT subscription using the same OAuth flow currently used by Zed.",
            ResolveClientId(provider).ClientId,
            null,
            "https://auth.openai.com/oauth/authorize",
            "https://auth.openai.com/oauth/token",
            "openid profile email offline_access",
            [1455, 1457],
            "localhost",
            new Dictionary<string, string>
            {
                ["id_token_add_organizations"] = "true",
                ["codex_cli_simplified_flow"] = "true",
                ["originator"] = "hyprnetshell",
            }),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private (string ClientId, string? Source) ResolveClientId(OnlineAccountProvider provider)
    {
        var metadataName = provider switch
        {
            OnlineAccountProvider.Google => GoogleClientIdMetadata,
            OnlineAccountProvider.Spotify => SpotifyClientIdMetadata,
            OnlineAccountProvider.ChatGpt => OpenAiClientIdMetadata,
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        if (EmbeddedClientIds.GetValueOrDefault(metadataName)?.Trim() is { Length: > 0 } embedded)
        {
            return (embedded, "Embedded");
        }

        var variableName = provider switch
        {
            OnlineAccountProvider.Google => GoogleClientIdVariable,
            OnlineAccountProvider.Spotify => SpotifyClientIdVariable,
            OnlineAccountProvider.ChatGpt => OpenAiClientIdVariable,
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        if (Environment.GetEnvironmentVariable(variableName)?.Trim() is { Length: > 0 } environment)
        {
            return (environment, "Environment");
        }

        var configured = ConfiguredClientId(provider);
        return configured.Length > 0 ? (configured, "Configuration") : ("", null);
    }

    private static string? ResolveGoogleClientSecret()
    {
        if (EmbeddedClientIds.GetValueOrDefault(GoogleClientSecretMetadata)?.Trim() is { Length: > 0 } embedded)
        {
            return embedded;
        }

        return Environment.GetEnvironmentVariable(GoogleClientSecretVariable)?.Trim() is { Length: > 0 } environment
            ? environment
            : null;
    }

    private string ConfiguredClientId(OnlineAccountProvider provider)
    {
        var accounts = _configuration.Snapshot.OnlineAccounts;
        return (provider switch
        {
            OnlineAccountProvider.Google => accounts.GoogleClientId,
            OnlineAccountProvider.Spotify => accounts.SpotifyClientId,
            OnlineAccountProvider.ChatGpt => accounts.OpenAiClientId,
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        }).Trim();
    }

    private static string BuildAuthorizationUrl(
        OAuthProviderDefinition definition,
        string redirectUri,
        string state,
        string nonce,
        string challenge)
    {
        var parameters = new Dictionary<string, string>(definition.AdditionalAuthorizationParameters)
        {
            ["client_id"] = definition.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };
        if (definition.Scopes.Length > 0)
        {
            parameters["scope"] = definition.Scopes;
        }
        if (definition.Provider == OnlineAccountProvider.Google)
        {
            parameters["nonce"] = nonce;
        }

        return definition.AuthorizationEndpoint + "?" + string.Join(
            "&",
            parameters.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static void AddClientSecret(Dictionary<string, string> parameters, string? clientSecret)
    {
        if (!string.IsNullOrEmpty(clientSecret))
        {
            parameters["client_secret"] = clientSecret;
        }
    }

    private static string? ReadOAuthError(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var error = root.TryGetProperty("error", out var errorProperty)
                ? ReadOAuthErrorValue(errorProperty)
                : null;
            var description = root.TryGetProperty("error_description", out var descriptionProperty)
                ? ReadOAuthErrorValue(descriptionProperty)
                : null;
            return (error, description) switch
            {
                ({ Length: > 0 }, { Length: > 0 }) => $"{error} — {description}",
                ({ Length: > 0 }, _) => error,
                (_, { Length: > 0 }) => description,
                _ => null,
            };
        }
        catch (JsonException exception)
        {
            AppLogger.Warning("OnlineAccounts", "OAuth provider returned a malformed error response", exception);
            return null;
        }
    }

    private static string? ReadOAuthErrorValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? null
                : value.GetRawText();
        }

        var message = value.TryGetProperty("message", out var messageProperty)
            ? ReadOAuthErrorValue(messageProperty)
            : null;
        var code = value.TryGetProperty("code", out var codeProperty)
            ? ReadOAuthErrorValue(codeProperty)
            : value.TryGetProperty("status", out var statusProperty)
                ? ReadOAuthErrorValue(statusProperty)
                : null;
        return (code, message) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{code}: {message}",
            (_, { Length: > 0 }) => message,
            ({ Length: > 0 }, _) => code,
            _ => value.GetRawText(),
        };
    }

    private static AccountIdentity ReadOpenAiIdentity(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return new AccountIdentity("chatgpt", "ChatGPT account");
        }

        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2)
            {
                throw new FormatException();
            }

            using var document = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var root = document.RootElement;
            var email = root.TryGetProperty("email", out var emailProperty) ? emailProperty.GetString() : null;
            string? accountId = null;
            if (root.TryGetProperty("https://api.openai.com/auth", out var auth) &&
                auth.TryGetProperty("chatgpt_account_id", out var accountProperty))
            {
                accountId = accountProperty.GetString();
            }

            return new AccountIdentity(accountId ?? email ?? "chatgpt", email ?? "ChatGPT account");
        }
        catch (Exception exception)
        {
            AppLogger.Warning("OnlineAccounts", "Could not read the ChatGPT identity token", exception);
            return new AccountIdentity("chatgpt", "ChatGPT account");
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static bool HasScopes(string grantedScopes, string requiredScopes)
    {
        var granted = grantedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return requiredScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(granted.Contains);
    }

    private static string StorageKey(OnlineAccountProvider provider) => provider switch
    {
        OnlineAccountProvider.Google => "google",
        OnlineAccountProvider.Spotify => "spotify",
        OnlineAccountProvider.ChatGpt => "chatgpt",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static string FriendlySecretServiceError(Exception exception) =>
        exception is InvalidOperationException
            ? exception.Message
            : "Secret Service is unavailable. Start a desktop keyring and reopen settings.";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _httpClient.Dispose();
        _tokenGate.Dispose();
        _lifetime.Dispose();
    }

    private sealed record OAuthProviderDefinition(
        OnlineAccountProvider Provider,
        string Name,
        string Description,
        string ClientId,
        string? ClientSecret,
        string AuthorizationEndpoint,
        string TokenEndpoint,
        string Scopes,
        int[] FixedCallbackPorts,
        string CallbackHost,
        IReadOnlyDictionary<string, string> AdditionalAuthorizationParameters);

    private sealed record AccountIdentity(string AccountId, string AccountName);
}

internal sealed record GoogleCalendarAccessCredential(string AccessToken, string AccountId);

internal sealed record ChatGptAccessCredential(string AccessToken, string AccountId);

internal sealed record StoredAccountCredential(
    string Provider,
    string AccountId,
    string AccountName,
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    string Scopes);

internal sealed record OAuthTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("id_token")] string? IdToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn = 3600,
    [property: JsonPropertyName("scope")] string? Scope = null);

internal sealed record GoogleUserInfo(
    [property: JsonPropertyName("sub")] string Subject,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record SpotifyUserInfo(
    [property: JsonPropertyName("account_id")] string? AccountId,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("display_name")] string? DisplayName);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(StoredAccountCredential))]
[JsonSerializable(typeof(OAuthTokenResponse))]
[JsonSerializable(typeof(GoogleUserInfo))]
[JsonSerializable(typeof(SpotifyUserInfo))]
internal sealed partial class OnlineAccountsJsonContext : JsonSerializerContext;
