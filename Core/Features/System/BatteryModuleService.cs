using System.Globalization;
using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;
using Tmds.DBus.Protocol;

namespace HyprNetShell.Core.Features.System;

internal sealed class BatteryModuleService(string device = "BAT0") : IBarDataService, IDisposable
{
    internal const int MINIMUM_CHARGE_LIMIT = 60;
    internal const int MAXIMUM_CHARGE_LIMIT = 100;
    internal const int CHARGE_LIMIT_STEP = 5;

    private const string PROPERTIES_INTERFACE = "org.freedesktop.DBus.Properties";
    private const string UPOWER_BUS = "org.freedesktop.UPower";
    private const string UPOWER_PATH = "/org/freedesktop/UPower/devices/DisplayDevice";
    private const string UPOWER_INTERFACE = "org.freedesktop.UPower.Device";
    private const string POWER_PROFILES_BUS = "net.hadess.PowerProfiles";
    private const string POWER_PROFILES_PATH = "/net/hadess/PowerProfiles";
    private const string POWER_PROFILES_INTERFACE = "net.hadess.PowerProfiles";

    private static readonly TimeSpan BatteryRecoveryInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PowerProfilesFallbackInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DbusReconnectInterval = TimeSpan.FromSeconds(15);

    private readonly string _chargeLimitConfigPath = GetChargeLimitConfigPath();
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _batteryRecoveryGate = new(1, 1);
    private readonly SemaphoreSlim _powerProfilesRecoveryGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private DBusConnection? _connection;
    private IDisposable? _upowerSubscription;
    private IDisposable? _powerProfilesSubscription;
    private IDisposable? _nameOwnerSubscription;
    private PowerProfileSnapshot _powerProfiles = PowerProfileSnapshot.Empty;
    private DateTime _nextBatteryRecoveryUtc = DateTime.MinValue;
    private DateTime _nextPowerProfilesFallbackUtc = DateTime.MinValue;
    private DateTime _nextDbusAttemptUtc = DateTime.MinValue;
    private int? _savedChargeLimit;
    private int? _chargeLimit;
    private int _percentage;
    private string _status = "Unknown";
    private bool _batteryAvailable;
    private bool _chargeLimitConfigLoaded;
    private bool _startupChargeLimitApplied;
    private Task? _batteryCallbackTask;
    private Task? _powerProfilesCallbackTask;
    private int _batteryRecoveryPending;
    private int _powerProfilesRecoveryPending;
    private bool _dbusInitialized;
    private bool _disposed;

    public BatterySnapshot Snapshot { get; private set; } = BatterySnapshot.Empty;

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed())
        {
            return;
        }

        InitializeChargeLimit();
        await EnsureDbusInitializedAsync(cancellationToken);

        var now = DateTime.UtcNow;
        if (now >= GetNextBatteryRecoveryUtc())
        {
            await RecoverBatteryAsync(cancellationToken);
        }

        if (!GetPowerProfiles().Available && now >= GetNextPowerProfilesFallbackUtc())
        {
            await RecoverPowerProfilesAsync(cancellationToken, allowCommandFallback: true);
        }
    }

    private async Task EnsureDbusInitializedAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            if (_disposed || _dbusInitialized || DateTime.UtcNow < _nextDbusAttemptUtc)
            {
                return;
            }
        }

        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            lock (_stateGate)
            {
                if (_disposed || _dbusInitialized || DateTime.UtcNow < _nextDbusAttemptUtc)
                {
                    return;
                }
            }

            DisposeConnection();
            DBusConnection? connection = null;
            IDisposable? upowerSubscription = null;
            IDisposable? powerProfilesSubscription = null;
            IDisposable? nameOwnerSubscription = null;
            try
            {
                connection = new DBusConnection(
                    DBusAddress.System ?? throw new InvalidOperationException("The system D-Bus address is unavailable"));
                await Dbus.WaitAsync(connection.ConnectAsync().AsTask(), cancellationToken);
                upowerSubscription = await AddPropertiesSubscriptionAsync(connection, UPOWER_PATH, UPOWER_INTERFACE);
                powerProfilesSubscription = await AddPropertiesSubscriptionAsync(
                    connection,
                    POWER_PROFILES_PATH,
                    POWER_PROFILES_INTERFACE);
                nameOwnerSubscription = await AddNameOwnerSubscriptionAsync(connection);

                lock (_stateGate)
                {
                    if (_disposed)
                    {
                        throw new ObjectDisposedException(nameof(BatteryModuleService));
                    }

                    _connection = connection;
                    _upowerSubscription = upowerSubscription;
                    _powerProfilesSubscription = powerProfilesSubscription;
                    _nameOwnerSubscription = nameOwnerSubscription;
                    _dbusInitialized = true;
                    _nextDbusAttemptUtc = DateTime.MaxValue;
                }

                connection = null;
                upowerSubscription = null;
                powerProfilesSubscription = null;
                nameOwnerSubscription = null;

                await RecoverBatteryAsync(cancellationToken);
                await RecoverPowerProfilesAsync(cancellationToken, allowCommandFallback: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                ScheduleDbusReconnect();
            }
            finally
            {
                nameOwnerSubscription?.Dispose();
                powerProfilesSubscription?.Dispose();
                upowerSubscription?.Dispose();
                connection?.Dispose();
            }
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private ValueTask<IDisposable> AddPropertiesSubscriptionAsync(
        DBusConnection connection,
        string path,
        string expectedInterface) =>
        connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Path = path,
                Interface = PROPERTIES_INTERFACE,
                Member = "PropertiesChanged",
            },
            static (message, state) =>
            {
                var reader = message.GetBodyReader();
                return new PropertiesChange(
                    reader.ReadString(),
                    reader.ReadDictionaryOfStringToVariantValue(),
                    reader.ReadArrayOfString());
            },
            static notification =>
            {
                var subscription = ((BatteryModuleService Service, string ExpectedInterface))notification.State!;
                if (!notification.HasValue)
                {
                    subscription.Service.ScheduleDbusReconnect();
                    return;
                }

                var change = notification.Value;
                if (!string.Equals(change.Interface, subscription.ExpectedInterface, StringComparison.Ordinal))
                {
                    return;
                }

                if (subscription.ExpectedInterface == UPOWER_INTERFACE)
                {
                    subscription.Service.ApplyBatteryProperties(change.Changed);
                    if (change.Invalidated.Any(IsBatteryProperty))
                    {
                        subscription.Service.QueueBatteryRecovery();
                    }
                }
                else
                {
                    subscription.Service.ApplyPowerProfileProperties(change.Changed);
                    if (change.Invalidated.Any(IsPowerProfileProperty))
                    {
                        subscription.Service.QueuePowerProfilesRecovery();
                    }
                }
            },
            false,
            Dbus.CONNECTION_FAILURE_OBSERVER_FLAGS,
            (this, expectedInterface));

    private ValueTask<IDisposable> AddNameOwnerSubscriptionAsync(DBusConnection connection) =>
        connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Interface = Dbus.BUS_INTERFACE,
                Member = "NameOwnerChanged",
            },
            static (message, state) =>
            {
                var reader = message.GetBodyReader();
                return new NameOwnerChange(reader.ReadString(), reader.ReadString(), reader.ReadString());
            },
            static notification =>
            {
                var service = (BatteryModuleService)notification.State!;
                if (!notification.HasValue)
                {
                    service.ScheduleDbusReconnect();
                    return;
                }

                var change = notification.Value;
                if (change.Name == UPOWER_BUS)
                {
                    if (string.IsNullOrEmpty(change.NewOwner))
                    {
                        service.ApplyBatteryFromSysfs();
                    }
                    else
                    {
                        service.QueueBatteryRecovery();
                    }
                }
                else if (change.Name == POWER_PROFILES_BUS)
                {
                    if (string.IsNullOrEmpty(change.NewOwner))
                    {
                        service.SetPowerProfiles(PowerProfileSnapshot.Empty);
                    }
                    else
                    {
                        service.QueuePowerProfilesRecovery();
                    }
                }
            },
            false,
            Dbus.CONNECTION_FAILURE_OBSERVER_FLAGS,
            this);

    private async Task RecoverBatteryAsync(CancellationToken cancellationToken)
    {
        await _batteryRecoveryGate.WaitAsync(cancellationToken);

        try
        {
            IReadOnlyDictionary<string, VariantValue>? properties = null;
            var connection = GetConnection();
            if (connection is not null)
            {
                try
                {
                    properties = await Dbus.WaitAsync(
                        GetAllAsync(connection, UPOWER_BUS, UPOWER_PATH, UPOWER_INTERFACE),
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // UPower may be absent even though the system bus is healthy.
                }
            }

            if (properties is not null)
            {
                ApplyBatteryProperties(properties);
            }
            else
            {
                ApplyBatteryFromSysfs();
            }

            lock (_stateGate)
            {
                _nextBatteryRecoveryUtc = DateTime.UtcNow + BatteryRecoveryInterval;
            }
        }
        finally
        {
            _batteryRecoveryGate.Release();
        }
    }



    private void QueueBatteryRecovery()
    {
        Interlocked.Exchange(ref _batteryRecoveryPending, 1);
        lock (_stateGate)
        {
            if (_disposed || _batteryCallbackTask is { IsCompleted: false })
            {
                return;
            }

            _batteryCallbackTask = RecoverPendingBatteryAsync(_lifetime.Token);
        }
    }

    private async Task RecoverPendingBatteryAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (Interlocked.Exchange(ref _batteryRecoveryPending, 0) != 0)
            {
                await RecoverBatteryAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_stateGate)
            {
                _batteryCallbackTask = null;
                if (!_disposed && Volatile.Read(ref _batteryRecoveryPending) != 0)
                {
                    _batteryCallbackTask = RecoverPendingBatteryAsync(_lifetime.Token);
                }
            }
        }
    }

    private void QueuePowerProfilesRecovery()
    {
        Interlocked.Exchange(ref _powerProfilesRecoveryPending, 1);
        lock (_stateGate)
        {
            if (_disposed || _powerProfilesCallbackTask is { IsCompleted: false })
            {
                return;
            }

            _powerProfilesCallbackTask = RecoverPendingPowerProfilesAsync(_lifetime.Token);
        }
    }

    private async Task RecoverPendingPowerProfilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (Interlocked.Exchange(ref _powerProfilesRecoveryPending, 0) != 0)
            {
                await RecoverPowerProfilesAsync(cancellationToken, allowCommandFallback: false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_stateGate)
            {
                _powerProfilesCallbackTask = null;
                if (!_disposed && Volatile.Read(ref _powerProfilesRecoveryPending) != 0)
                {
                    _powerProfilesCallbackTask = RecoverPendingPowerProfilesAsync(_lifetime.Token);
                }
            }
        }
    }

    private void ApplyBatteryFromSysfs()
    {
        var basePath = Path.Combine("/sys/class/power_supply", device);
        var capacityText = Read(Path.Combine(basePath, "capacity"));
        if (!int.TryParse(capacityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity))
        {
            UpdateBattery(false, 0, "Unknown");
            return;
        }

        var status = Read(Path.Combine(basePath, "status"));
        UpdateBattery(
            true,
            Math.Clamp(capacity, 0, 100),
            string.IsNullOrWhiteSpace(status) ? "Unknown" : status);
    }

    private void ApplyBatteryProperties(IReadOnlyDictionary<string, VariantValue> properties)
    {
        var available = TryGetBool(properties, "IsPresent", out var present)
            ? present
            : GetBatteryAvailable();
        var percentage = TryGetDouble(properties, "Percentage", out var rawPercentage)
            ? Math.Clamp((int)Math.Round(rawPercentage, MidpointRounding.AwayFromZero), 0, 100)
            : GetPercentage();
        var status = TryGetUInt32(properties, "State", out var state)
            ? BatteryStateName(state)
            : GetStatus();
        UpdateBattery(available, percentage, status);
    }

    private async Task RecoverPowerProfilesAsync(
        CancellationToken cancellationToken,
        bool allowCommandFallback)
    {
        await _powerProfilesRecoveryGate.WaitAsync(cancellationToken);

        try
        {
            IReadOnlyDictionary<string, VariantValue>? properties = null;
            var connection = GetConnection();
            if (connection is not null)
            {
                try
                {
                    properties = await Dbus.WaitAsync(
                        GetAllAsync(
                            connection,
                            POWER_PROFILES_BUS,
                            POWER_PROFILES_PATH,
                            POWER_PROFILES_INTERFACE),
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // power-profiles-daemon is optional; retain the command fallback.
                }
            }

            if (properties is not null)
            {
                ApplyPowerProfileProperties(properties);
            }
            else if (allowCommandFallback)
            {
                var output = await CommandRunner.TryReadAsync(
                    "powerprofilesctl",
                    "list",
                    TimeSpan.FromMilliseconds(900),
                    cancellationToken);
                SetPowerProfiles(ParsePowerProfiles(output));
            }

            lock (_stateGate)
            {
                _nextPowerProfilesFallbackUtc = DateTime.UtcNow + PowerProfilesFallbackInterval;
            }
        }
        finally
        {
            _powerProfilesRecoveryGate.Release();
        }
    }

    private void ApplyPowerProfileProperties(IReadOnlyDictionary<string, VariantValue> properties)
    {
        var current = GetPowerProfiles();
        var active = TryGetString(properties, "ActiveProfile", out var activeProfile)
            ? activeProfile
            : current.Active;
        var profiles = TryGetProfiles(properties, out var availableProfiles)
            ? availableProfiles
            : current.Profiles;

        if (profiles.Count == 0)
        {
            SetPowerProfiles(PowerProfileSnapshot.Empty);
            return;
        }

        SetPowerProfiles(new PowerProfileSnapshot(true, active, profiles));
    }

    private static Task<IReadOnlyDictionary<string, VariantValue>> GetAllAsync(
        DBusConnection connection,
        string destination,
        string path,
        string targetInterface) =>
        Dbus.CallAsync(
            connection,
            destination,
            path,
            PROPERTIES_INTERFACE,
            "GetAll",
            reader => (IReadOnlyDictionary<string, VariantValue>)reader.ReadDictionaryOfStringToVariantValue(),
            "s",
            (ref MessageWriter writer) => writer.WriteString(targetInterface));

    private void InitializeChargeLimit()
    {
        int? startupChargeLimit = null;
        lock (_stateGate)
        {
            if (_startupChargeLimitApplied)
            {
                return;
            }

            var path = Path.Combine("/sys/class/power_supply", device, "charge_control_end_threshold");
            var chargeLimitText = Read(path);
            _chargeLimit = int.TryParse(
                chargeLimitText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedChargeLimit)
                ? Math.Clamp(parsedChargeLimit, MINIMUM_CHARGE_LIMIT, MAXIMUM_CHARGE_LIMIT)
                : null;
            _startupChargeLimitApplied = true;

            if (_chargeLimit.HasValue)
            {
                EnsureChargeLimitConfigLoaded();
                if (_savedChargeLimit is { } savedChargeLimit)
                {
                    _chargeLimit = savedChargeLimit;
                    startupChargeLimit = savedChargeLimit;
                }
            }

            PublishSnapshotLocked();
        }

        if (startupChargeLimit is { } limitToApply)
        {
            _ = ApplyChargeLimitAsync(limitToApply);
        }
    }

    internal void SetChargeLimit(int chargeLimit)
    {
        lock (_stateGate)
        {
            if (_disposed || _chargeLimit is null)
            {
                return;
            }

            chargeLimit = Math.Clamp(chargeLimit, MINIMUM_CHARGE_LIMIT, MAXIMUM_CHARGE_LIMIT);
            _savedChargeLimit = chargeLimit;
            _chargeLimitConfigLoaded = true;
            _chargeLimit = chargeLimit;
            PublishSnapshotLocked();
        }

        PersistChargeLimit(chargeLimit);
        _ = ApplyChargeLimitAsync(chargeLimit);
    }

    private void EnsureChargeLimitConfigLoaded()
    {
        if (_chargeLimitConfigLoaded)
        {
            return;
        }

        _savedChargeLimit = ReadChargeLimitConfig();
        _chargeLimitConfigLoaded = true;
    }

    private int? ReadChargeLimitConfig()
    {
        try
        {
            var text = File.ReadAllText(_chargeLimitConfigPath).Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chargeLimit)
                ? Math.Clamp(chargeLimit, MINIMUM_CHARGE_LIMIT, MAXIMUM_CHARGE_LIMIT)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void PersistChargeLimit(int chargeLimit)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_chargeLimitConfigPath)!);
            File.WriteAllText(_chargeLimitConfigPath, chargeLimit.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Battery", "Could not save the battery charge limit", exception);
        }
    }

    private async Task ApplyChargeLimitAsync(int chargeLimit)
    {
        var path = Path.Combine("/sys/class/power_supply", device, "charge_control_end_threshold");
        try
        {
            await File.WriteAllTextAsync(path, chargeLimit.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Battery", $"Could not set charge limit through {path}", exception);
        }
    }

    internal void SetPowerProfile(string profile)
    {
        lock (_stateGate)
        {
            if (_disposed ||
                !_powerProfiles.Available ||
                !_powerProfiles.Profiles.Contains(profile, StringComparer.Ordinal))
            {
                return;
            }

            _powerProfiles = _powerProfiles with { Active = profile };
            PublishSnapshotLocked();
        }

        _ = ApplyPowerProfileAsync(profile);
    }

    private async Task ApplyPowerProfileAsync(string profile)
    {
        var appliedThroughDbus = false;
        var connection = GetConnection();
        if (connection is not null)
        {
            try
            {
                await Dbus.CallAsync(
                    connection,
                    POWER_PROFILES_BUS,
                    POWER_PROFILES_PATH,
                    PROPERTIES_INTERFACE,
                    "Set",
                    "ssv",
                    (ref MessageWriter writer) =>
                    {
                        writer.WriteString(POWER_PROFILES_INTERFACE);
                        writer.WriteString("ActiveProfile");
                        writer.WriteVariantString(profile);
                    }).WaitAsync(TimeSpan.FromSeconds(2));
                appliedThroughDbus = true;
            }
            catch
            {
                // Preserve powerprofilesctl support on systems without the daemon API.
            }
        }

        if (!appliedThroughDbus)
        {
            await CommandRunner.TryRunAsync(
                "powerprofilesctl",
                ["set", profile],
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
        }

        lock (_stateGate)
        {
            _nextPowerProfilesFallbackUtc = DateTime.MinValue;
        }
    }

    internal static PowerProfileSnapshot ParsePowerProfiles(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return PowerProfileSnapshot.Empty;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        var active = "";
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            var isActive = trimmed.StartsWith('*');
            var profile = trimmed.TrimStart('*').Trim().TrimEnd(':');
            if (profile is not ("power-saver" or "balanced" or "performance"))
            {
                continue;
            }

            found.Add(profile);
            if (isActive)
            {
                active = profile;
            }
        }

        string[] preferredOrder = ["power-saver", "balanced", "performance"];
        var profiles = preferredOrder.Where(found.Contains).ToArray();
        return profiles.Length == 0
            ? PowerProfileSnapshot.Empty
            : new PowerProfileSnapshot(true, active, profiles);
    }

    private static bool TryGetProfiles(
        IReadOnlyDictionary<string, VariantValue> properties,
        out IReadOnlyList<string> profiles)
    {
        profiles = [];
        if (!properties.TryGetValue("Profiles", out var raw))
        {
            return false;
        }

        var array = raw.Unwrap();
        if (array.Type != VariantValueType.Array)
        {
            return false;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < array.Count; index++)
        {
            var item = array.GetItem(index).Unwrap();
            if (item.Type != VariantValueType.Dictionary)
            {
                continue;
            }

            var values = item.GetDictionary<string, VariantValue>();
            if (TryGetString(values, "Profile", out var profile) &&
                profile is "power-saver" or "balanced" or "performance")
            {
                found.Add(profile);
            }
        }

        string[] preferredOrder = ["power-saver", "balanced", "performance"];
        profiles = preferredOrder.Where(found.Contains).ToArray();
        return true;
    }

    private static bool TryGetBool(
        IReadOnlyDictionary<string, VariantValue> values,
        string key,
        out bool result)
    {
        if (values.TryGetValue(key, out var raw) && raw.Unwrap().Type == VariantValueType.Bool)
        {
            result = raw.Unwrap().GetBool();
            return true;
        }

        result = false;
        return false;
    }

    private static bool TryGetDouble(
        IReadOnlyDictionary<string, VariantValue> values,
        string key,
        out double result)
    {
        if (values.TryGetValue(key, out var raw) && raw.Unwrap().Type == VariantValueType.Double)
        {
            result = raw.Unwrap().GetDouble();
            return true;
        }

        result = 0;
        return false;
    }

    private static bool TryGetUInt32(
        IReadOnlyDictionary<string, VariantValue> values,
        string key,
        out uint result)
    {
        if (values.TryGetValue(key, out var raw) && raw.Unwrap().Type == VariantValueType.UInt32)
        {
            result = raw.Unwrap().GetUInt32();
            return true;
        }

        result = 0;
        return false;
    }

    private static bool TryGetString(
        IReadOnlyDictionary<string, VariantValue> values,
        string key,
        out string result)
    {
        if (values.TryGetValue(key, out var raw) && raw.Unwrap().Type == VariantValueType.String)
        {
            result = raw.Unwrap().GetString();
            return true;
        }

        result = "";
        return false;
    }

    private static string BatteryStateName(uint state) => state switch
    {
        1 => "Charging",
        2 => "Discharging",
        3 => "Empty",
        4 => "Fully charged",
        5 => "Pending charge",
        6 => "Pending discharge",
        _ => "Unknown",
    };

    private static bool IsBatteryProperty(string property) =>
        property is "Percentage" or "State" or "IsPresent";

    private static bool IsPowerProfileProperty(string property) =>
        property is "ActiveProfile" or "Profiles";

    private void UpdateBattery(bool available, int percentage, string status)
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _batteryAvailable = available;
            _percentage = percentage;
            _status = status;
            PublishSnapshotLocked();
        }
    }

    private void SetPowerProfiles(PowerProfileSnapshot powerProfiles)
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _powerProfiles = powerProfiles;
            PublishSnapshotLocked();
        }
    }

    private void PublishSnapshotLocked()
    {
        Snapshot = _batteryAvailable
            ? new BatterySnapshot(true, device, _percentage, _status, _chargeLimit, _powerProfiles)
            : BatterySnapshot.Empty with { ChargeLimit = _chargeLimit, PowerProfiles = _powerProfiles };
    }

    private DBusConnection? GetConnection()
    {
        lock (_stateGate)
        {
            return _disposed || !_dbusInitialized ? null : _connection;
        }
    }

    private PowerProfileSnapshot GetPowerProfiles()
    {
        lock (_stateGate)
        {
            return _powerProfiles;
        }
    }

    private DateTime GetNextBatteryRecoveryUtc()
    {
        lock (_stateGate)
        {
            return _nextBatteryRecoveryUtc;
        }
    }

    private DateTime GetNextPowerProfilesFallbackUtc()
    {
        lock (_stateGate)
        {
            return _nextPowerProfilesFallbackUtc;
        }
    }

    private bool GetBatteryAvailable()
    {
        lock (_stateGate)
        {
            return _batteryAvailable;
        }
    }

    private int GetPercentage()
    {
        lock (_stateGate)
        {
            return _percentage;
        }
    }

    private string GetStatus()
    {
        lock (_stateGate)
        {
            return _status;
        }
    }

    private bool IsDisposed()
    {
        lock (_stateGate)
        {
            return _disposed;
        }
    }

    private void ScheduleDbusReconnect()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _dbusInitialized = false;
            _nextDbusAttemptUtc = DateTime.UtcNow + DbusReconnectInterval;
        }
    }

    private void DisposeConnection()
    {
        IDisposable? nameOwnerSubscription;
        IDisposable? powerProfilesSubscription;
        IDisposable? upowerSubscription;
        DBusConnection? connection;
        lock (_stateGate)
        {
            nameOwnerSubscription = _nameOwnerSubscription;
            _nameOwnerSubscription = null;
            powerProfilesSubscription = _powerProfilesSubscription;
            _powerProfilesSubscription = null;
            upowerSubscription = _upowerSubscription;
            _upowerSubscription = null;
            connection = _connection;
            _connection = null;
            _dbusInitialized = false;
        }

        nameOwnerSubscription?.Dispose();
        powerProfilesSubscription?.Dispose();
        upowerSubscription?.Dispose();
        connection?.Dispose();
    }

    public void Dispose()
    {
        Task? batteryCallbackTask;
        Task? powerProfilesCallbackTask;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            batteryCallbackTask = _batteryCallbackTask;
            powerProfilesCallbackTask = _powerProfilesCallbackTask;
        }

        _lifetime.Cancel();
        DisposeConnection();
        try
        {
            Task.WhenAll(
                    batteryCallbackTask ?? Task.CompletedTask,
                    powerProfilesCallbackTask ?? Task.CompletedTask)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private static string Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private static string GetChargeLimitConfigPath()
    {
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(configRoot, "hyprnetshell", "battery-charge-limit");
    }

    private sealed record PropertiesChange(
        string Interface,
        IReadOnlyDictionary<string, VariantValue> Changed,
        string[] Invalidated);

    private sealed record NameOwnerChange(string Name, string OldOwner, string NewOwner);
}
