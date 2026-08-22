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

    internal Task SetWifiEnabledAsync(bool enabled) =>
        CommandRunner.TryRunAsync(
            "nmcli",
            ["radio", "wifi", enabled ? "on" : "off"],
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

    internal async Task<IReadOnlyList<WifiNetworkSnapshot>> ScanWifiNetworksAsync()
    {
        var output = await CommandRunner.TryReadAsync(
            "nmcli",
            "-t -f ACTIVE,SSID,SIGNAL,SECURITY d wifi list",
            TimeSpan.FromSeconds(3),
            CancellationToken.None);
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var networks = new List<WifiNetworkSnapshot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = SplitNmcliFields(line);
            if (parts.Length < 4 || !seen.Add(parts[1]))
            {
                continue;
            }

            networks.Add(new WifiNetworkSnapshot(
                parts[1],
                int.TryParse(parts[2], out var signal) ? signal : null,
                parts[3],
                parts[0].Equals("yes", StringComparison.OrdinalIgnoreCase)));
        }

        return networks
            .OrderByDescending(x => x.Active)
            .ThenByDescending(x => x.Signal.GetValueOrDefault())
            .ToArray();
    }

    internal void ConnectWifi(string ssid)
    {
        Task.Run(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "nmcli",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { "d", "wifi", "connect", ssid },
                });
            }
            catch
            {
                // Ignore transient command failures; the next state refresh will show the result.
            }
        });
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
