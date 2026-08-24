using System.Diagnostics;
using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Tmds.DBus.Protocol;

namespace HyprNetShell.Core.Features.System;

internal sealed class NetworkModuleService : IBarDataService, IDisposable
{
    private const string NetworkManagerBusName = "org.freedesktop.NetworkManager";
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SignalCoalesceDelay = TimeSpan.FromMilliseconds(150);

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private DBusConnection? _connection;
    private IDisposable? _networkManagerSubscription;
    private IDisposable? _nameOwnerSubscription;
    private Task? _callbackTask;
    private DateTime _nextSubscriptionAttemptUtc = DateTime.MinValue;
    private DateTime _nextRecoveryUtc = DateTime.MinValue;
    private int _invalidated;
    private bool _hasSnapshot;
    private bool _disposed;

    public NetworkSnapshot Snapshot { get; private set; } = NetworkSnapshot.Empty;

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        bool recoveryDue;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            var now = DateTime.UtcNow;
            recoveryDue = !_hasSnapshot || now >= _nextRecoveryUtc;
            if (recoveryDue)
            {
                _nextRecoveryUtc = now + RecoveryInterval;
            }
        }

        await EnsureInitializedAsync(cancellationToken);
        if (recoveryDue || Interlocked.Exchange(ref _invalidated, 0) != 0)
        {
            await RefreshSnapshotAsync(cancellationToken);
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_disposed || _connection is not null || DateTime.UtcNow < _nextSubscriptionAttemptUtc)
            {
                return;
            }
        }

        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            lock (_stateLock)
            {
                if (_disposed || _connection is not null || DateTime.UtcNow < _nextSubscriptionAttemptUtc)
                {
                    return;
                }

                _nextSubscriptionAttemptUtc = DateTime.UtcNow + RecoveryInterval;
            }

            DBusConnection? connection = null;
            IDisposable? networkManagerSubscription = null;
            IDisposable? nameOwnerSubscription = null;
            var published = false;
            try
            {
                connection = new DBusConnection(
                    DBusAddress.System ?? throw new InvalidOperationException("The system D-Bus address is unavailable"));
                await connection.ConnectAsync();
                networkManagerSubscription = await connection.AddMatchAsync(
                    new MatchRule
                    {
                        Type = MessageType.Signal,
                        Sender = NetworkManagerBusName,
                    },
                    static (_, _) => true,
                    static notification =>
                    {
                        var service = (NetworkModuleService)notification.State!;
                        if (!notification.HasValue)
                        {
                            service.ResetSubscriptions();
                        }
                        else if (notification.Value)
                        {
                            service.Invalidate();
                        }
                    },
                    false,
                    Dbus.CONNECTION_FAILURE_OBSERVER_FLAGS,
                    this);
                nameOwnerSubscription = await connection.AddMatchAsync(
                    new MatchRule
                    {
                        Type = MessageType.Signal,
                        Interface = "org.freedesktop.DBus",
                        Member = "NameOwnerChanged",
                    },
                    static (message, _) =>
                    {
                        var reader = message.GetBodyReader();
                        return reader.ReadString().Equals(NetworkManagerBusName, StringComparison.Ordinal);
                    },
                    static notification =>
                    {
                        var service = (NetworkModuleService)notification.State!;
                        if (!notification.HasValue)
                        {
                            service.ResetSubscriptions();
                        }
                        else if (notification.Value)
                        {
                            service.Invalidate();
                        }
                    },
                    false,
                    Dbus.CONNECTION_FAILURE_OBSERVER_FLAGS,
                    this);

                lock (_stateLock)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _connection = connection;
                    _networkManagerSubscription = networkManagerSubscription;
                    _nameOwnerSubscription = nameOwnerSubscription;
                    published = true;
                }
            }
            catch
            {
                // nmcli snapshots remain available when the system bus is unavailable.
            }
            finally
            {
                if (!published)
                {
                    nameOwnerSubscription?.Dispose();
                    networkManagerSubscription?.Dispose();
                    connection?.Dispose();
                }
            }
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private void ResetSubscriptions()
    {
        IDisposable? networkManagerSubscription;
        IDisposable? nameOwnerSubscription;
        DBusConnection? connection;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            networkManagerSubscription = _networkManagerSubscription;
            _networkManagerSubscription = null;
            nameOwnerSubscription = _nameOwnerSubscription;
            _nameOwnerSubscription = null;
            connection = _connection;
            _connection = null;
            _nextSubscriptionAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        }

        nameOwnerSubscription?.Dispose();
        networkManagerSubscription?.Dispose();
        connection?.Dispose();
    }

    private void Invalidate()
    {
        Interlocked.Exchange(ref _invalidated, 1);
        lock (_stateLock)
        {
            if (_disposed || _callbackTask is { IsCompleted: false })
            {
                return;
            }

            _callbackTask = RefreshAfterSignalAsync(_lifetime.Token);
        }
    }

    private async Task RefreshAfterSignalAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SignalCoalesceDelay, cancellationToken);
            while (Interlocked.Exchange(ref _invalidated, 0) != 0)
            {
                await RefreshSnapshotAsync(cancellationToken);
                if (Volatile.Read(ref _invalidated) != 0)
                {
                    await Task.Delay(SignalCoalesceDelay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Keep the previous snapshot after transient callback refresh failures.
        }
        finally
        {
            lock (_stateLock)
            {
                _callbackTask = null;
                if (!_disposed && Volatile.Read(ref _invalidated) != 0)
                {
                    _callbackTask = RefreshAfterSignalAsync(_lifetime.Token);
                }
            }
        }
    }

    private async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var radioTask = CommandRunner.TryReadAsync(
                "nmcli",
                "radio wifi",
                TimeSpan.FromMilliseconds(800),
                cancellationToken);
            var devicesTask = CommandRunner.TryReadAsync(
                "nmcli",
                "-t -f DEVICE,TYPE,STATE,CONNECTION device",
                TimeSpan.FromMilliseconds(800),
                cancellationToken);
            await Task.WhenAll(radioTask, devicesTask);

            var radioOutput = await radioTask;
            var output = await devicesTask;
            var wifiAvailable = radioOutput is not null;
            var wifiEnabled = radioOutput?.Trim().Equals("enabled", StringComparison.OrdinalIgnoreCase) == true;

            var snapshot = new NetworkSnapshot(wifiAvailable, wifiEnabled, false, "", "", "", [], null);

            if (!string.IsNullOrWhiteSpace(output))
            {
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = line.Split(':');
                    if (parts.Length < 4)
                    {
                        continue;
                    }

                    var device = parts[0];
                    var type = parts[1];
                    var stateName = parts[2];
                    var connection = parts[3];

                    if (stateName != "connected" || string.IsNullOrWhiteSpace(connection))
                    {
                        continue;
                    }

                    var wifiSignal = type.Equals("wifi", StringComparison.OrdinalIgnoreCase)
                        ? await ReadWifiSignalAsync(device, cancellationToken)
                        : null;

                    snapshot = new NetworkSnapshot(
                        wifiAvailable,
                        wifiEnabled,
                        true,
                        device,
                        type,
                        connection,
                        ReadIpAddresses(device),
                        wifiSignal);

                    break;
                }
            }

            Snapshot = snapshot;
            lock (_stateLock)
            {
                _hasSnapshot = true;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    internal Task<WifiOperationResult> SetWifiEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        RunNmcliAsync(["radio", "wifi", enabled ? "on" : "off"], null, TimeSpan.FromSeconds(4), cancellationToken);

    internal async Task<IReadOnlyList<WifiNetworkSnapshot>> ScanWifiNetworksAsync(CancellationToken cancellationToken)
    {
        var scanTask = CommandRunner.TryReadAsync(
            "nmcli",
            "-t -f ACTIVE,SSID,SIGNAL,SECURITY d wifi list",
            TimeSpan.FromSeconds(3),
            cancellationToken);
        var profilesTask = CommandRunner.TryReadAsync(
            "nmcli",
            "-t -f NAME,UUID,TYPE connection show",
            TimeSpan.FromSeconds(3),
            cancellationToken);
        await Task.WhenAll(scanTask, profilesTask);

        var output = await scanTask;
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var savedProfiles = await ReadSavedWifiProfilesAsync(await profilesTask, cancellationToken);
        var networks = new Dictionary<string, WifiNetworkSnapshot>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = SplitNmcliFields(line);
            if (parts.Length < 4 || string.IsNullOrWhiteSpace(parts[1]))
            {
                continue;
            }

            var active = parts[0].Equals("yes", StringComparison.OrdinalIgnoreCase);
            var savedConnectionName = active && Snapshot.Type.Equals("wifi", StringComparison.OrdinalIgnoreCase)
                ? Snapshot.Connection
                : savedProfiles.GetValueOrDefault(parts[1]);
            var candidate = new WifiNetworkSnapshot(
                parts[1],
                int.TryParse(parts[2], out var signal) ? signal : null,
                parts[3],
                active,
                string.IsNullOrWhiteSpace(savedConnectionName) ? null : savedConnectionName);
            if (!networks.TryGetValue(candidate.Ssid, out var existing) ||
                candidate.Active || !existing.Active && candidate.Signal.GetValueOrDefault() > existing.Signal.GetValueOrDefault())
            {
                networks[candidate.Ssid] = candidate;
            }
        }

        return networks.Values
            .OrderByDescending(x => x.Active)
            .ThenByDescending(x => x.Signal.GetValueOrDefault())
            .ToArray();
    }

    internal Task<WifiOperationResult> ConnectWifiAsync(
        string ssid,
        string? password,
        CancellationToken cancellationToken) =>
        RunNmcliAsync(
            password is null
                ? ["device", "wifi", "connect", ssid]
                : ["--ask", "device", "wifi", "connect", ssid],
            password,
            TimeSpan.FromSeconds(15),
            cancellationToken);

    internal Task<WifiOperationResult> ForgetWifiAsync(
        string connectionName,
        CancellationToken cancellationToken) =>
        RunNmcliAsync(
            ["connection", "delete", "id", connectionName],
            null,
            TimeSpan.FromSeconds(8),
            cancellationToken);

    internal async Task<WifiPasswordResult> ReadWifiPasswordAsync(
        string connectionName,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
            var startInfo = new ProcessStartInfo
            {
                FileName = "nmcli",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
            {
                "--show-secrets",
                "--get-values", "802-11-wireless-security.psk",
                "connection", "show", "id", connectionName,
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo);
            if (process is null)
            {
                return WifiPasswordResult.Failed("Could not start nmcli");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var password = (await outputTask).TrimEnd('\r', '\n');
            var error = (await errorTask).Trim();
            if (process.ExitCode != 0)
            {
                return WifiPasswordResult.Failed(TrimError(error));
            }

            return string.IsNullOrEmpty(password)
                ? WifiPasswordResult.Failed("This connection has no stored password")
                : WifiPasswordResult.Succeeded(password);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            TryKill(process);
            return WifiPasswordResult.Failed("Password lookup was cancelled");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return WifiPasswordResult.Failed("Password lookup timed out");
        }
        catch (Exception exception)
        {
            TryKill(process);
            return WifiPasswordResult.Failed(TrimError(exception.Message));
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task<Dictionary<string, string>> ReadSavedWifiProfilesAsync(
        string? output,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var profileTasks = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SplitNmcliFields)
            .Where(parts => parts.Length >= 3 &&
                parts[2].Equals("802-11-wireless", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(parts[0]) &&
                !string.IsNullOrWhiteSpace(parts[1]))
            .Select(async parts =>
            {
                var ssid = await CommandRunner.TryReadAsync(
                    "nmcli",
                    $"--escape no -g 802-11-wireless.ssid connection show {parts[1]}",
                    TimeSpan.FromSeconds(1),
                    cancellationToken);
                return (Ssid: ssid?.Trim(), ConnectionName: parts[0]);
            })
            .ToArray();
        var profiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var profile in await Task.WhenAll(profileTasks))
        {
            if (!string.IsNullOrWhiteSpace(profile.Ssid))
            {
                profiles.TryAdd(profile.Ssid, profile.ConnectionName);
            }
        }

        return profiles;
    }

    private async Task<WifiOperationResult> RunNmcliAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeoutCts.CancelAfter(timeout);
            var startInfo = new ProcessStartInfo
            {
                FileName = "nmcli",
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo);
            if (process is null)
            {
                return WifiOperationResult.Failed("Could not start nmcli");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            if (standardInput is not null)
            {
                await process.StandardInput.WriteLineAsync(standardInput.AsMemory(), timeoutCts.Token);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(timeoutCts.Token);
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            if (process.ExitCode != 0)
            {
                return WifiOperationResult.Failed(TrimError(string.IsNullOrWhiteSpace(error) ? output : error));
            }

            Invalidate();
            return WifiOperationResult.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            TryKill(process);
            return WifiOperationResult.Failed("The Wi-Fi operation was cancelled");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return WifiOperationResult.Failed("The Wi-Fi operation timed out");
        }
        catch (Exception exception)
        {
            TryKill(process);
            return WifiOperationResult.Failed(TrimError(exception.Message));
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string TrimError(string error) =>
        error.Length <= 180 ? error : error[..177] + "...";

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(true);
            }
        }
        catch
        {
        }
    }

    private static string[] SplitNmcliFields(string line)
    {
        var fields = new List<string>();
        var current = new global::System.Text.StringBuilder();
        var escaped = false;

        foreach (var c in line)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
            }
            else if (c == '\\')
            {
                escaped = true;
            }
            else if (c == ':')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private static async Task<int?> ReadWifiSignalAsync(string device, CancellationToken cancellationToken)
    {
        var output = await CommandRunner.TryReadAsync(
            "nmcli",
            $"-t -f ACTIVE,SIGNAL device wifi list ifname {device}",
            TimeSpan.FromMilliseconds(800),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.LastIndexOf(':');
            if (separator <= 0 ||
                !line[..separator].Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(line[(separator + 1)..], out var signal))
            {
                continue;
            }

            return Math.Clamp(signal, 0, 100);
        }

        return null;
    }

    public void Dispose()
    {
        Task? callbackTask;
        IDisposable? networkManagerSubscription;
        IDisposable? nameOwnerSubscription;
        DBusConnection? connection;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            callbackTask = _callbackTask;
            _callbackTask = null;
            networkManagerSubscription = _networkManagerSubscription;
            _networkManagerSubscription = null;
            nameOwnerSubscription = _nameOwnerSubscription;
            _nameOwnerSubscription = null;
            connection = _connection;
            _connection = null;
        }

        _lifetime.Cancel();
        nameOwnerSubscription?.Dispose();
        networkManagerSubscription?.Dispose();
        connection?.Dispose();
        try
        {
            callbackTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private static IReadOnlyList<string> ReadIpAddresses(string device)
    {
        try
        {
            var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(x => string.Equals(x.Name, device, StringComparison.Ordinal));
            if (networkInterface is null)
            {
                return [];
            }

            return networkInterface.GetIPProperties()
                .UnicastAddresses
                .Where(x => x.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Where(x => !x.Address.IsIPv6LinkLocal)
                .Select(x => x.Address.ToString())
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
