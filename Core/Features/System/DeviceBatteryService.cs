using HyprNetShell.Core.Features.KdeConnect;
using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Features.System;

internal sealed class DeviceBatteryService(
    BatteryModuleService laptopBattery,
    BluetoothModuleService bluetooth,
    KdeConnectService kdeConnect,
    NotificationService notifications)
{
    internal const int LOW_BATTERY_PERCENTAGE = 15;

    private readonly Dictionary<string, DeviceState> _states = new(StringComparer.Ordinal);
    private DeviceBatteriesSnapshot _snapshot = DeviceBatteriesSnapshot.Empty;
    private readonly List<DeviceBatterySnapshot> _devicesBuffer = [];
    private BatterySnapshot? _lastLaptopSnapshot;
    private BluetoothSnapshot? _lastBluetoothSnapshot;
    private KdeConnectSnapshot? _lastKdeConnectSnapshot;

    internal DeviceBatteriesSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal void Refresh()
    {
        var laptop = laptopBattery.Snapshot;
        var bluetoothSnapshot = bluetooth.Snapshot;
        var kdeConnectSnapshot = kdeConnect.Snapshot;
        if (Equals(laptop, _lastLaptopSnapshot) &&
            ReferenceEquals(bluetoothSnapshot, _lastBluetoothSnapshot) &&
            ReferenceEquals(kdeConnectSnapshot, _lastKdeConnectSnapshot))
        {
            return;
        }

        var devices = ReadDevices(laptop, bluetoothSnapshot, kdeConnectSnapshot);
        foreach (var device in devices)
        {
            CheckNotifications(device);
        }

        Volatile.Write(ref _snapshot, new DeviceBatteriesSnapshot(devices));
        _lastLaptopSnapshot = laptop;
        _lastBluetoothSnapshot = bluetoothSnapshot;
        _lastKdeConnectSnapshot = kdeConnectSnapshot;
    }

    private DeviceBatterySnapshot[] ReadDevices(
        BatterySnapshot laptop,
        BluetoothSnapshot bluetoothSnapshot,
        KdeConnectSnapshot kdeConnectSnapshot)
    {
        var devices = _devicesBuffer;
        devices.Clear();
        if (laptop.Available)
        {
            devices.Add(new DeviceBatterySnapshot(
                $"laptop:{laptop.Device}",
                "Laptop",
                DeviceBatterySource.Laptop,
                Math.Clamp(laptop.Percentage, 0, 100),
                laptop.IsCharging));
        }

        devices.AddRange(bluetoothSnapshot.Devices
            .Where(static device => device.Connected && device.BatteryPercentage.HasValue)
            .Select(static device => new DeviceBatterySnapshot(
                $"bluetooth:{device.Address}",
                device.Name,
                DeviceBatterySource.Bluetooth,
                Math.Clamp(device.BatteryPercentage!.Value, 0, 100),
                null)));

        devices.AddRange(kdeConnectSnapshot.Devices
            .Where(static device => device.IsPaired && device.IsReachable && device.BatteryLevel.HasValue)
            .Select(static device => new DeviceBatterySnapshot(
                $"kdeconnect:{device.Id}",
                device.Name,
                DeviceBatterySource.KdeConnect,
                Math.Clamp(device.BatteryLevel!.Value, 0, 100),
                device.IsCharging)));

        return [.. devices
            .OrderBy(static device => device.Source)
            .ThenBy(static device => device.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private void CheckNotifications(DeviceBatterySnapshot device)
    {
        var isLow = device.Percentage <= LOW_BATTERY_PERCENTAGE && device.IsCharging is not true;
        var isFull = device.Percentage == 100;

        if (!_states.TryGetValue(device.Id, out var previous))
        {
            if (isLow)
            {
                ShowLowBatteryNotification(device);
            }

            _states[device.Id] = new DeviceState(isLow, isFull);
            return;
        }

        if (isLow && !previous.IsLow)
        {
            ShowLowBatteryNotification(device);
        }
        else if (isFull && !previous.IsFull)
        {
            notifications.ShowLocal(
                "Battery fully charged",
                $"{device.Name} is fully charged.",
                "battery-full",
                storeInHistory: false);
        }

        _states[device.Id] = new DeviceState(isLow, isFull);
    }

    private void ShowLowBatteryNotification(DeviceBatterySnapshot device) =>
        notifications.ShowLocal(
            "Low battery",
            $"{device.Name} battery is at {device.Percentage}%.",
            "battery-warning",
            storeInHistory: false);

    private readonly record struct DeviceState(bool IsLow, bool IsFull);
}
