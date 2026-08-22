using System.Text.RegularExpressions;
using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;
using Tmds.DBus.Protocol;

namespace HyprNetShell.Core.Features.System;

internal sealed partial class BluetoothModuleService : IBarDataService, IDisposable
{
    private const string BLUEZ_BUS_NAME = "org.bluez";
    private const string DBUS_BUS_NAME = "org.freedesktop.DBus";
    private const string DBUS_INTERFACE = "org.freedesktop.DBus";
    private const string OBJECT_MANAGER_INTERFACE = "org.freedesktop.DBus.ObjectManager";
    private const string PROPERTIES_INTERFACE = "org.freedesktop.DBus.Properties";

    private static readonly TimeSpan EventCoalesceDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(60);

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private DBusConnection? _connection;
    private IDisposable? _propertiesSubscription;
    private IDisposable? _interfacesAddedSubscription;
    private IDisposable? _interfacesRemovedSubscription;
    private IDisposable? _nameOwnerSubscription;
    private Task? _eventRefreshTask;
    private DateTime _lastRecoveryUtc = DateTime.MinValue;
    private int _refreshPending;
    private bool _initialReadComplete;
    private bool _disposed;

    public BluetoothSnapshot Snapshot { get; private set; } = BluetoothSnapshot.Empty;

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            if (_initialReadComplete && DateTime.UtcNow - _lastRecoveryUtc < RecoveryInterval)
            {
                return;
            }
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        await _refreshGate.WaitAsync(linkedCancellation.Token);
        try
        {
            lock (_stateLock)
            {
                if (_disposed ||
                    (_initialReadComplete && DateTime.UtcNow - _lastRecoveryUtc < RecoveryInterval))
                {
                    return;
                }
            }

            await EnsureSubscriptionsAsync(linkedCancellation.Token);
            await ReadSnapshotAsync(linkedCancellation.Token);

            lock (_stateLock)
            {
                _initialReadComplete = true;
                _lastRecoveryUtc = DateTime.UtcNow;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task EnsureSubscriptionsAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_disposed || _connection is not null)
            {
                return;
            }
        }

        DBusConnection? connection = null;
        IDisposable? propertiesSubscription = null;
        IDisposable? interfacesAddedSubscription = null;
        IDisposable? interfacesRemovedSubscription = null;
        IDisposable? nameOwnerSubscription = null;
        try
        {
            connection = new DBusConnection(DBusAddress.System!);
            cancellationToken.ThrowIfCancellationRequested();
            await connection.ConnectAsync();
            cancellationToken.ThrowIfCancellationRequested();
            propertiesSubscription = await AddInvalidationMatchAsync(connection, new MatchRule
            {
                Type = MessageType.Signal,
                Sender = BLUEZ_BUS_NAME,
                PathNamespace = "/org/bluez",
                Interface = PROPERTIES_INTERFACE,
                Member = "PropertiesChanged",
            });
            interfacesAddedSubscription = await AddInvalidationMatchAsync(connection, new MatchRule
            {
                Type = MessageType.Signal,
                Sender = BLUEZ_BUS_NAME,
                Path = "/",
                Interface = OBJECT_MANAGER_INTERFACE,
                Member = "InterfacesAdded",
            });
            interfacesRemovedSubscription = await AddInvalidationMatchAsync(connection, new MatchRule
            {
                Type = MessageType.Signal,
                Sender = BLUEZ_BUS_NAME,
                Path = "/",
                Interface = OBJECT_MANAGER_INTERFACE,
                Member = "InterfacesRemoved",
            });
            nameOwnerSubscription = await AddInvalidationMatchAsync(connection, new MatchRule
            {
                Type = MessageType.Signal,
                Sender = DBUS_BUS_NAME,
                Path = "/org/freedesktop/DBus",
                Interface = DBUS_INTERFACE,
                Member = "NameOwnerChanged",
                Arg0 = BLUEZ_BUS_NAME,
            });

            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _connection = connection;
                _propertiesSubscription = propertiesSubscription;
                _interfacesAddedSubscription = interfacesAddedSubscription;
                _interfacesRemovedSubscription = interfacesRemovedSubscription;
                _nameOwnerSubscription = nameOwnerSubscription;
                connection = null;
                propertiesSubscription = null;
                interfacesAddedSubscription = null;
                interfacesRemovedSubscription = null;
                nameOwnerSubscription = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // bluetoothctl remains the snapshot and action fallback when system D-Bus is unavailable.
        }
        finally
        {
            propertiesSubscription?.Dispose();
            interfacesAddedSubscription?.Dispose();
            interfacesRemovedSubscription?.Dispose();
            nameOwnerSubscription?.Dispose();
            connection?.Dispose();
        }
    }

    private ValueTask<IDisposable> AddInvalidationMatchAsync(DBusConnection connection, MatchRule rule) =>
        connection.AddMatchAsync(
            rule,
            static (_, _) => true,
            static notification =>
            {
                var service = (BluetoothModuleService)notification.State!;
                if (!notification.HasValue)
                {
                    service.ResetSubscriptions();
                }
                else if (notification.Value)
                {
                    service.QueueEventRefresh();
                }
            },
            false,
            Dbus.CONNECTION_FAILURE_OBSERVER_FLAGS,
            this);

    private void ResetSubscriptions()
    {
        DBusConnection? connection;
        IDisposable? propertiesSubscription;
        IDisposable? interfacesAddedSubscription;
        IDisposable? interfacesRemovedSubscription;
        IDisposable? nameOwnerSubscription;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            connection = _connection;
            _connection = null;
            propertiesSubscription = _propertiesSubscription;
            _propertiesSubscription = null;
            interfacesAddedSubscription = _interfacesAddedSubscription;
            _interfacesAddedSubscription = null;
            interfacesRemovedSubscription = _interfacesRemovedSubscription;
            _interfacesRemovedSubscription = null;
            nameOwnerSubscription = _nameOwnerSubscription;
            _nameOwnerSubscription = null;
            _lastRecoveryUtc = DateTime.MinValue;
        }

        propertiesSubscription?.Dispose();
        interfacesAddedSubscription?.Dispose();
        interfacesRemovedSubscription?.Dispose();
        nameOwnerSubscription?.Dispose();
        connection?.Dispose();
    }

    private void QueueEventRefresh()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Exchange(ref _refreshPending, 1);
            if (_eventRefreshTask is null)
            {
                _eventRefreshTask = RefreshFromEventsAsync();
            }
        }
    }

    private async Task RefreshFromEventsAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _refreshPending, 0);
                await Task.Delay(EventCoalesceDelay, _lifetime.Token);
                if (Interlocked.Exchange(ref _refreshPending, 0) != 0)
                {
                    continue;
                }

                await _refreshGate.WaitAsync(_lifetime.Token);
                try
                {
                    await ReadSnapshotAsync(_lifetime.Token);
                }
                finally
                {
                    _refreshGate.Release();
                }

                if (Interlocked.Exchange(ref _refreshPending, 0) == 0)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // A transient bluetoothctl failure is corrected by the next signal or recovery read.
        }
        finally
        {
            lock (_stateLock)
            {
                _eventRefreshTask = null;
                if (!_disposed && Interlocked.Exchange(ref _refreshPending, 0) != 0)
                {
                    _eventRefreshTask = RefreshFromEventsAsync();
                }
            }
        }
    }

    private async Task ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var controllerOutput = await CommandRunner.TryReadAsync(
            "bluetoothctl",
            "show",
            TimeSpan.FromMilliseconds(700),
            cancellationToken);

        if (controllerOutput is null)
        {
            Snapshot = BluetoothSnapshot.Empty;
            return;
        }

        var powered = PoweredLine().IsMatch(controllerOutput);
        if (!powered)
        {
            Snapshot = new BluetoothSnapshot(true, false, []);
            return;
        }

        var devicesOutput = await CommandRunner.TryReadAsync(
            "bluetoothctl",
            "devices Paired",
            TimeSpan.FromMilliseconds(700),
            cancellationToken);

        if (devicesOutput is null)
        {
            Snapshot = new BluetoothSnapshot(true, true, []);
            return;
        }

        var devices = await Task.WhenAll(ParsePairedDevices(devicesOutput).Select(async device =>
        {
            var info = await CommandRunner.TryReadAsync(
                "bluetoothctl",
                $"info {device.Address}",
                TimeSpan.FromMilliseconds(500),
                cancellationToken);
            return ParseDeviceInfo(device.Address, device.Name, info);
        }));

        Snapshot = new BluetoothSnapshot(
            true,
            true,
            devices.OrderByDescending(device => device.Connected).ThenBy(device => device.Name).ToArray());
    }

    internal async Task SetPoweredAsync(bool powered)
    {
        await CommandRunner.TryRunAsync(
            "bluetoothctl",
            ["power", powered ? "on" : "off"],
            TimeSpan.FromSeconds(3),
            CancellationToken.None);
        QueueEventRefresh();
    }

    internal async Task SetConnectedAsync(string address, bool connected)
    {
        await CommandRunner.TryRunAsync(
            "bluetoothctl",
            [connected ? "connect" : "disconnect", address],
            TimeSpan.FromSeconds(8),
            CancellationToken.None);
        QueueEventRefresh();
    }

    public void Dispose()
    {
        DBusConnection? connection;
        IDisposable? propertiesSubscription;
        IDisposable? interfacesAddedSubscription;
        IDisposable? interfacesRemovedSubscription;
        IDisposable? nameOwnerSubscription;
        Task? eventRefreshTask;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            connection = _connection;
            propertiesSubscription = _propertiesSubscription;
            interfacesAddedSubscription = _interfacesAddedSubscription;
            interfacesRemovedSubscription = _interfacesRemovedSubscription;
            nameOwnerSubscription = _nameOwnerSubscription;
            eventRefreshTask = _eventRefreshTask;
            _connection = null;
            _propertiesSubscription = null;
            _interfacesAddedSubscription = null;
            _interfacesRemovedSubscription = null;
            _nameOwnerSubscription = null;
        }

        propertiesSubscription?.Dispose();
        interfacesAddedSubscription?.Dispose();
        interfacesRemovedSubscription?.Dispose();
        nameOwnerSubscription?.Dispose();
        connection?.Dispose();

        try
        {
            eventRefreshTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException exception) when (exception.InnerExceptions.All(
                   inner => inner is OperationCanceledException))
        {
        }

        _lifetime.Dispose();
    }

    internal static IReadOnlyList<(string Address, string Name)> ParsePairedDevices(string output)
    {
        var devices = new List<(string, string)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = DeviceLine().Match(line);
            if (match.Success)
            {
                devices.Add((match.Groups["address"].Value, match.Groups["name"].Value.Trim()));
            }
        }

        return devices;
    }

    internal static BluetoothDeviceSnapshot ParseDeviceInfo(string address, string name, string? output)
    {
        var connected = false;
        int? battery = null;
        string? icon = null;
        if (!string.IsNullOrWhiteSpace(output))
        {
            connected = ConnectedLine().IsMatch(output);
            var batteryMatch = BatteryLine().Match(output);
            if (batteryMatch.Success && int.TryParse(batteryMatch.Groups["percentage"].Value, out var percentage))
            {
                battery = Math.Clamp(percentage, 0, 100);
            }

            var iconMatch = IconLine().Match(output);
            if (iconMatch.Success)
            {
                icon = iconMatch.Groups["icon"].Value.Trim();
            }
        }

        return new BluetoothDeviceSnapshot(address, name, connected, battery, icon);
    }

    [GeneratedRegex(@"^Device\s+(?<address>[0-9A-Fa-f:]{17})\s+(?<name>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLine();

    [GeneratedRegex(@"^\s*Connected:\s*yes\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectedLine();

    [GeneratedRegex(@"^\s*Powered:\s*yes\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PoweredLine();

    [GeneratedRegex(@"^\s*Battery Percentage:\s*(?:0x[0-9A-Fa-f]+\s+)?\((?<percentage>\d+)\)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BatteryLine();

    [GeneratedRegex(@"^\s*Icon:\s*(?<icon>\S.*?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IconLine();
}
