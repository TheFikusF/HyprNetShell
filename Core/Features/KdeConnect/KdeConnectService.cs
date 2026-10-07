using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HyprNetShell.Core.Features.KdeConnect.Plugins;
using HyprNetShell.Core.Features.KdeConnect.Protocol;
using HyprNetShell.Core.Features.KdeConnect.Transport;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Features.KdeConnect;

internal sealed class KdeConnectService : IDisposable
{
    private const string LOG_CATEGORY = "KdeConnect";

    private static readonly TimeSpan PairingTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StableConnectionDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectionWarningInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, KdeConnectDevice> _devices = new(StringComparer.Ordinal);
    private readonly List<Task> _backgroundTasks = [];
    private readonly HashSet<Task> _ownedTasks = [];
    private readonly SemaphoreSlim _handshakeSlots = new(16, 16);
    private readonly ClipboardHistoryService _clipboard;
    private readonly ClipboardPlugin _clipboardPlugin;
    private readonly IKdeConnectPlugin[] _plugins;

    private KdeConnectStateStore? _state;
    private TcpListener? _listener;
    private Socket? _discovery;
    private int _tcpPort;
    private KdeConnectSnapshot _snapshot = KdeConnectSnapshot.Empty;
    private int _clipboardVersion;
    private bool _disposed;

    internal KdeConnectSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal KdeConnectService(ClipboardHistoryService clipboard)
    {
        _clipboard = clipboard;
        _clipboardPlugin = new ClipboardPlugin(clipboard);
        _plugins = [new BatteryPlugin(OnPluginStateChanged), _clipboardPlugin];

        if (string.Equals(
                Environment.GetEnvironmentVariable("HYPRNETSHELL_KDECONNECT_DISABLED"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _state = new KdeConnectStateStore();
            lock (_gate)
            {
                foreach (var paired in _state.PairedDevices)
                {
                    _devices[paired.Id] = new KdeConnectDevice(paired.Id, paired.Name, paired.DeviceType) {
                        IsPaired = true,
                        PairingState = KdeConnectPairingState.Paired,
                    };
                }
                PublishSnapshotLocked();
            }

            _listener = BindTcpListener(out _tcpPort);
            _discovery = BindDiscoverySocket();
            _backgroundTasks.Add(Task.Run(() => AcceptLoopAsync(_lifetime.Token)));
            _backgroundTasks.Add(Task.Run(() => DiscoveryReceiveLoopAsync(_lifetime.Token)));
            _backgroundTasks.Add(Task.Run(() => BroadcastLoopAsync(_lifetime.Token)));
            _backgroundTasks.Add(Task.Run(() => MaintenanceLoopAsync(_lifetime.Token)));
        }
        catch (Exception exception)
        {
            AppLogger.Warning(LOG_CATEGORY, "KDE Connect could not start; the shell will continue without it", exception);
            StopNetworking();
        }
    }

    internal async Task<KdeConnectOperationResult> RequestPairingAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (!TryGetDevice(deviceId, out var device, out var error))
        {
            return KdeConnectOperationResult.Failed(error!);
        }

        var channel = await EnsureChannelAsync(device, cancellationToken);
        if (channel is null)
        {
            return KdeConnectOperationResult.Failed("The device is not reachable.");
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            if (device.IsPaired)
            {
                return KdeConnectOperationResult.Succeeded;
            }
            BeginPairingTimeoutLocked(device);
            device.PairingTimestamp = timestamp;
            device.VerificationCode = CreateVerificationCode(channel, timestamp);
            device.PairingState = KdeConnectPairingState.OutgoingRequest;
            PublishSnapshotLocked();
        }

        try
        {
            await channel.SendAsync(CreatePairRequestPacket(timestamp), cancellationToken);
            return KdeConnectOperationResult.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ResetPendingPairing(device);
            throw;
        }
        catch (Exception exception)
        {
            ResetPendingPairing(device);
            AppLogger.Warning(LOG_CATEGORY, $"Could not request pairing with '{device.Name}'", exception);
            return KdeConnectOperationResult.Failed("Could not send the pairing request.");
        }
    }

    internal Task<KdeConnectOperationResult> AcceptPairingAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        RespondToPairingAsync(deviceId, accept: true, cancellationToken);

    internal Task<KdeConnectOperationResult> RejectPairingAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        RespondToPairingAsync(deviceId, accept: false, cancellationToken);

    internal async Task<KdeConnectOperationResult> UnpairAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (!TryGetDevice(deviceId, out var device, out var error))
        {
            return KdeConnectOperationResult.Failed(error!);
        }

        var channel = GetChannel(device);
        if (channel is not null)
        {
            try
            {
                await channel.SendAsync(CreatePairResponsePacket(false), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                AppLogger.Warning(LOG_CATEGORY, $"Could not notify '{device.Name}' that it was unpaired", exception);
            }
        }

        try
        {
            _state?.RemovePaired(device.Id);
        }
        catch (Exception exception)
        {
            AppLogger.Warning(LOG_CATEGORY, $"Could not persist unpairing for '{device.Name}'", exception);
            return KdeConnectOperationResult.Failed("Could not update paired device state.");
        }

        lock (_gate)
        {
            SetUnpairedLocked(device);
            PublishSnapshotLocked();
        }
        return KdeConnectOperationResult.Succeeded;
    }

    internal async Task<KdeConnectOperationResult> SendClipboardTextAsync(
        string content,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(content))
        {
            return KdeConnectOperationResult.Failed("The clipboard entry is empty.");
        }

        (string Name, KdeConnectChannel Channel)[] devices;
        lock (_gate)
        {
            devices = _devices.Values
                .Where(static device => device.IsPaired && device.Channel is not null)
                .Select(static device => (Name: device.Name, Channel: device.Channel!))
                .DistinctBy(static device => device.Channel)
                .ToArray();
        }
        if (devices.Length == 0)
        {
            return KdeConnectOperationResult.Failed("No paired KDE Connect device is connected.");
        }

        var packet = ClipboardPlugin.CreateClipboardPacket(content);
        var sent = false;
        foreach (var device in devices)
        {
            try
            {
                await device.Channel.SendAsync(packet, cancellationToken);
                sent = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                AppLogger.Warning(LOG_CATEGORY, $"Could not send a clipboard entry to '{device.Name}'", exception);
            }
        }

        return sent
            ? KdeConnectOperationResult.Succeeded
            : KdeConnectOperationResult.Failed("Could not send the clipboard entry.");
    }

    internal async Task<KdeConnectOperationResult> SendClipboardAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (!TryGetDevice(deviceId, out var device, out var error))
        {
            return KdeConnectOperationResult.Failed(error!);
        }
        if (!device.IsPaired)
        {
            return KdeConnectOperationResult.Failed("The device is not paired.");
        }

        var channel = await EnsureChannelAsync(device, cancellationToken);
        if (channel is null)
        {
            return KdeConnectOperationResult.Failed("The device is not reachable.");
        }

        try
        {
            var content = await _clipboard.ReadTextAsync(cancellationToken);
            if (content is null)
            {
                return KdeConnectOperationResult.Failed("The clipboard does not contain text.");
            }
            if (_clipboardPlugin.ShouldSuppressOutbound(content))
            {
                return KdeConnectOperationResult.Succeeded;
            }

            await channel.SendAsync(ClipboardPlugin.CreateClipboardPacket(content), cancellationToken);
            return KdeConnectOperationResult.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLogger.Warning(LOG_CATEGORY, $"Could not send the clipboard to '{device.Name}'", exception);
            return KdeConnectOperationResult.Failed("Could not send the clipboard.");
        }
    }

    private async Task<KdeConnectOperationResult> RespondToPairingAsync(
        string deviceId,
        bool accept,
        CancellationToken cancellationToken)
    {
        if (!TryGetDevice(deviceId, out var device, out var error))
        {
            return KdeConnectOperationResult.Failed(error!);
        }

        KdeConnectChannel? channel;
        lock (_gate)
        {
            if (device.PairingState != KdeConnectPairingState.IncomingRequest)
            {
                return KdeConnectOperationResult.Failed("There is no incoming pairing request for this device.");
            }
            channel = device.Channel;
        }
        if (channel is null)
        {
            return KdeConnectOperationResult.Failed("The device is no longer connected.");
        }

        try
        {
            await channel.SendAsync(CreatePairResponsePacket(accept), cancellationToken);
            if (accept)
            {
                PersistPair(device, channel);
                await SendBatteryRequestAsync(channel, cancellationToken);
            }
            else
            {
                lock (_gate)
                {
                    SetUnpairedLocked(device);
                    PublishSnapshotLocked();
                }
            }
            return KdeConnectOperationResult.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLogger.Warning(LOG_CATEGORY, $"Could not {(accept ? "accept" : "reject")} pairing with '{device.Name}'", exception);
            return KdeConnectOperationResult.Failed($"Could not {(accept ? "accept" : "reject")} the pairing request.");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                if (!await _handshakeSlots.WaitAsync(0, cancellationToken))
                {
                    client.Dispose();
                    continue;
                }
                TrackTask(Task.Run(() => EstablishInboundAsync(client, cancellationToken), CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Warning(LOG_CATEGORY, "The KDE Connect TCP listener stopped", exception);
        }
    }

    private async Task EstablishInboundAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            var state = _state;
            if (state is null)
            {
                client.Dispose();
                return;
            }
            var channel = await KdeConnectChannel.AcceptAsync(client, state, cancellationToken);
            AttachChannel(channel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
        }
        catch (Exception exception)
        {
            client.Dispose();
            AppLogger.Warning(LOG_CATEGORY, "Rejected an incoming KDE Connect connection", exception);
        }
        finally
        {
            _handshakeSlots.Release();
        }
    }

    private async Task DiscoveryReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[KdeConnectProtocol.MaximumIdentityBytes];
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _discovery!.ReceiveFromAsync(buffer, SocketFlags.None, sender, cancellationToken);
                if (result.RemoteEndPoint is not IPEndPoint remote)
                {
                    continue;
                }

                var length = result.ReceivedBytes;
                if (length > 0 && buffer[length - 1] == (byte)'\n')
                {
                    length--;
                }
                if (!KdeConnectProtocol.TryParse(buffer.AsSpan(0, length), out var packet))
                {
                    continue;
                }
                using (packet)
                {
                    if (!KdeConnectProtocol.TryReadIdentity(packet!, out var identity) ||
                        identity.ProtocolVersion != KdeConnectProtocol.Version ||
                        string.Equals(identity.DeviceId, _state?.DeviceId, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    await ProcessDiscoveryAsync(identity, remote.Address, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Warning(LOG_CATEGORY, "The KDE Connect discovery listener stopped", exception);
        }
    }

    private async Task ProcessDiscoveryAsync(
        KdeConnectIdentity identity,
        IPAddress address,
        CancellationToken cancellationToken)
    {
        KdeConnectDevice device;
        var sendDirectedResponse = false;
        var shouldConnect = false;
        lock (_gate)
        {
            if (!_devices.TryGetValue(identity.DeviceId, out device!))
            {
                if (_devices.Values.Count(static candidate => !candidate.IsPaired) >= 128)
                {
                    return;
                }
                device = new KdeConnectDevice(identity.DeviceId, identity.DeviceName, identity.DeviceType);
                var paired = _state?.GetPaired(identity.DeviceId);
                if (paired is not null)
                {
                    device.IsPaired = true;
                    device.PairingState = KdeConnectPairingState.Paired;
                }
                _devices.Add(device.Id, device);
                sendDirectedResponse = true;
            }
            else if (DateTimeOffset.UtcNow - device.LastSeen > TimeSpan.FromSeconds(30))
            {
                sendDirectedResponse = true;
            }

            device.Name = identity.DeviceName;
            device.DeviceType = identity.DeviceType;
            var discoveredAddress = address.ToString();
            if (!string.Equals(device.Address, discoveredAddress, StringComparison.Ordinal) ||
                device.TcpPort != identity.TcpPort)
            {
                device.NextReconnectAt = DateTimeOffset.MinValue;
                device.ReconnectAttempts = 0;
                device.ConnectionFailed = false;
                device.LastConnectionFailure = null;
                device.NextConnectionWarningAt = DateTimeOffset.MinValue;
            }
            device.Address = discoveredAddress;
            device.TcpPort = identity.TcpPort;
            device.LastSeen = DateTimeOffset.UtcNow;
            shouldConnect = identity.TcpPort is >= KdeConnectProtocol.DiscoveryPort and <= KdeConnectProtocol.LastPort &&
                device.Channel is null && !device.Connecting &&
                DateTimeOffset.UtcNow >= device.NextReconnectAt &&
                string.CompareOrdinal(_state?.DeviceId, device.Id) < 0;
            PublishSnapshotLocked();
        }

        if (sendDirectedResponse)
        {
            await SendIdentityAsync(new IPEndPoint(address, KdeConnectProtocol.DiscoveryPort), cancellationToken);
        }
        if (shouldConnect)
        {
            TrackTask(EnsureChannelAsync(device, cancellationToken));
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await SendIdentityAsync(
                    new IPEndPoint(IPAddress.Broadcast, KdeConnectProtocol.DiscoveryPort),
                    cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Warning(LOG_CATEGORY, "KDE Connect identity broadcasts stopped", exception);
        }
    }

    private async Task SendIdentityAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var state = _state;
        var discovery = _discovery;
        if (state is null || discovery is null)
        {
            return;
        }
        var identity = KdeConnectProtocol.CreateDiscoveryIdentity(state.DeviceId, state.DeviceName, _tcpPort);
        try
        {
            await discovery.SendToAsync(identity, SocketFlags.None, endpoint, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLogger.Warning(LOG_CATEGORY, $"Could not send a KDE Connect identity to {endpoint}", exception);
        }
    }

    private async Task<KdeConnectChannel?> EnsureChannelAsync(
        KdeConnectDevice device,
        CancellationToken cancellationToken)
    {
        string? address;
        int port;
        lock (_gate)
        {
            if (device.Channel is not null)
            {
                return device.Channel;
            }
            if (device.Connecting || DateTimeOffset.UtcNow < device.NextReconnectAt ||
                string.IsNullOrWhiteSpace(device.Address) ||
                device.TcpPort is < KdeConnectProtocol.DiscoveryPort or > KdeConnectProtocol.LastPort)
            {
                return null;
            }
            device.Connecting = true;
            address = device.Address;
            port = device.TcpPort;
        }

        try
        {
            var state = _state;
            if (state is null)
            {
                return null;
            }
            var expected = new KdeConnectIdentity(device.Id, device.Name, device.DeviceType, KdeConnectProtocol.Version, port);
            var channel = await KdeConnectChannel.ConnectAsync(address, port, expected, state, cancellationToken);
            AttachChannel(channel);
            return channel;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                // A discovery update or inbound connection may have superseded this attempt.
                if (device.Channel is not null ||
                    !string.Equals(device.Address, address, StringComparison.Ordinal) || device.TcpPort != port)
                {
                    return null;
                }

                device.ConnectionFailed = true;
                ScheduleReconnectLocked(device, afterConnectionFailure: true);
                var socketException = FindSocketException(exception);
                var failure = socketException is not null
                    ? $"{socketException.SocketErrorCode}: {socketException.Message}"
                    : $"{exception.GetType().Name}: {exception.Message}";
                var now = DateTimeOffset.UtcNow;
                if (!string.Equals(device.LastConnectionFailure, failure, StringComparison.Ordinal) || now >= device.NextConnectionWarningAt)
                {
                    device.LastConnectionFailure = failure;
                    device.NextConnectionWarningAt = now + ConnectionWarningInterval;
                    var retrySeconds = Math.Max(0, (int)Math.Ceiling((device.NextReconnectAt - now).TotalSeconds));
                    AppLogger.Warning(LOG_CATEGORY,
                        $"Could not connect to KDE Connect device '{device.Name}' ({device.Id}) at {address}:{port}: " +
                        $"{failure}. Next connection attempt allowed in {retrySeconds}s with backoff capped at 60s; " +
                        "unchanged failures are logged at most once every 5 minutes. " +
                        "Check that the device is awake on the same reachable network, VPN routes, " +
                        "Wi-Fi client isolation, and firewall access to KDE Connect TCP/UDP ports 1714-1764.",
                        exception);
                }
                PublishSnapshotLocked();
            }
            return null;
        }
        finally
        {
            lock (_gate)
            {
                device.Connecting = false;
            }
        }
    }

    private void AttachChannel(KdeConnectChannel channel)
    {
        KdeConnectChannel? replaced = null;
        KdeConnectDevice device;
        var discard = false;
        lock (_gate)
        {
            if (_disposed)
            {
                channel.Dispose();
                return;
            }

            if (!_devices.TryGetValue(channel.RemoteIdentity.DeviceId, out device!))
            {
                if (_devices.Values.Count(static candidate => !candidate.IsPaired) >= 128)
                {
                    channel.Dispose();
                    return;
                }
                device = new KdeConnectDevice(
                    channel.RemoteIdentity.DeviceId,
                    channel.RemoteIdentity.DeviceName,
                    channel.RemoteIdentity.DeviceType);
                _devices.Add(device.Id, device);
            }
            device.Name = channel.RemoteIdentity.DeviceName;
            device.DeviceType = channel.RemoteIdentity.DeviceType;
            device.LastSeen = DateTimeOffset.UtcNow;

            var existing = device.Channel;
            var preferOutbound = string.CompareOrdinal(_state?.DeviceId, device.Id) < 0;
            if (existing is not null && !existing.Completion.IsCompleted &&
                (existing.IsOutbound == preferOutbound || channel.IsOutbound != preferOutbound))
            {
                discard = true;
            }
            else
            {
                replaced = existing;
                device.Channel = channel;
                device.ChannelConnectedAt = DateTimeOffset.UtcNow;
                device.NextReconnectAt = DateTimeOffset.MaxValue;
                device.ConnectionFailed = false;
                device.LastConnectionFailure = null;
                device.NextConnectionWarningAt = DateTimeOffset.MinValue;
                var paired = _state?.GetPaired(device.Id);
                device.IsPaired = paired is not null;
                device.PairingState = device.IsPaired
                    ? KdeConnectPairingState.Paired
                    : KdeConnectPairingState.Unpaired;
            }
            PublishSnapshotLocked();
        }

        if (discard)
        {
            channel.Dispose();
            return;
        }
        replaced?.Dispose();
        channel.Start(HandlePacketAsync);
        TrackTask(ObserveChannelAsync(device, channel));
        if (device.IsPaired)
        {
            TrackTask(ActivatePluginsSafeAsync(device, channel, _lifetime.Token));
        }
    }

    private async Task ObserveChannelAsync(KdeConnectDevice device, KdeConnectChannel channel)
    {
        try
        {
            await channel.Completion;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
            AppLogger.Info(LOG_CATEGORY, $"KDE Connect device '{device.Name}' disconnected");
        }
        catch (Exception exception)
        {
            AppLogger.Warning(LOG_CATEGORY, $"KDE Connect connection to '{device.Name}' ended", exception);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(device.Channel, channel))
                {
                    device.Channel = null;
                    device.ConnectionFailed = true;
                    if (DateTimeOffset.UtcNow - device.ChannelConnectedAt >= StableConnectionDuration)
                    {
                        device.ReconnectAttempts = 0;
                    }
                    ScheduleReconnectLocked(device);
                    PublishSnapshotLocked();
                }
            }
            channel.Dispose();
        }
    }

    private async Task HandlePacketAsync(
        KdeConnectChannel channel,
        KdeConnectPacket packet,
        CancellationToken cancellationToken)
    {
        KdeConnectDevice? device;
        lock (_gate)
        {
            _devices.TryGetValue(channel.RemoteIdentity.DeviceId, out device);
        }
        if (device is null || !ReferenceEquals(GetChannel(device), channel))
        {
            return;
        }

        if (string.Equals(packet.Type, KdeConnectProtocol.PairType, StringComparison.Ordinal))
        {
            await HandlePairPacketAsync(device, channel, packet, cancellationToken);
            return;
        }
        if (!device.IsPaired)
        {
            return;
        }

        foreach (var plugin in _plugins)
        {
            if (plugin.CanHandle(packet.Type))
            {
                await plugin.HandleAsync(device, channel, packet, cancellationToken);
                return;
            }
        }
    }

    private async Task HandlePairPacketAsync(
        KdeConnectDevice device,
        KdeConnectChannel channel,
        KdeConnectPacket packet,
        CancellationToken cancellationToken)
    {
        if (!packet.Body.TryGetProperty("pair", out var pairElement) ||
            pairElement.ValueKind is not (global::System.Text.Json.JsonValueKind.True or global::System.Text.Json.JsonValueKind.False))
        {
            return;
        }

        if (!pairElement.GetBoolean())
        {
            try
            {
                _state?.RemovePaired(device.Id);
            }
            catch (Exception exception)
            {
                AppLogger.Warning(LOG_CATEGORY, $"Could not persist remote unpairing for '{device.Name}'", exception);
            }
            lock (_gate)
            {
                SetUnpairedLocked(device);
                PublishSnapshotLocked();
            }
            return;
        }

        long timestampSeconds = 0;
        var hasTimestamp = packet.Body.TryGetProperty("timestamp", out var timestampElement) &&
            timestampElement.TryGetInt64(out timestampSeconds);
        bool awaitingOutgoing;
        lock (_gate)
        {
            awaitingOutgoing = device.PairingState == KdeConnectPairingState.OutgoingRequest;
        }
        if ((!awaitingOutgoing && !hasTimestamp) ||
            (hasTimestamp && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestampSeconds) > 1800))
        {
            await channel.SendAsync(CreatePairResponsePacket(false), cancellationToken);
            AppLogger.Warning(LOG_CATEGORY, $"Rejected invalid or stale pairing request from '{device.Name}'");
            return;
        }

        if (!awaitingOutgoing && device.IsPaired)
        {
            _state?.RemovePaired(device.Id);
            lock (_gate)
            {
                SetUnpairedLocked(device);
            }
        }

        var confirmsOutgoing = false;
        lock (_gate)
        {
            if (device.PairingState == KdeConnectPairingState.OutgoingRequest)
            {
                confirmsOutgoing = true;
            }
            else
            {
                BeginPairingTimeoutLocked(device);
                device.PairingTimestamp = timestampSeconds;
                device.VerificationCode = CreateVerificationCode(channel, timestampSeconds);
                device.PairingState = KdeConnectPairingState.IncomingRequest;
                PublishSnapshotLocked();
            }
        }

        if (confirmsOutgoing)
        {
            PersistPair(device, channel);
            await SendBatteryRequestAsync(channel, cancellationToken);
        }
    }

    private void PersistPair(KdeConnectDevice device, KdeConnectChannel channel)
    {
        var state = _state ?? throw new InvalidOperationException("KDE Connect persistent state is unavailable.");
        state.SavePaired(new PairedDevice(device.Id, device.Name, device.DeviceType, channel.RemoteCertificateDer));
        lock (_gate)
        {
            device.PairingTimeout?.Cancel();
            device.PairingTimeout?.Dispose();
            device.PairingTimeout = null;
            device.IsPaired = true;
            device.PairingState = KdeConnectPairingState.Paired;
            device.VerificationCode = null;
            device.PairingTimestamp = null;
            PublishSnapshotLocked();
        }
    }

    private void BeginPairingTimeoutLocked(KdeConnectDevice device)
    {
        device.PairingTimeout?.Cancel();
        device.PairingTimeout?.Dispose();
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        device.PairingTimeout = timeout;
        TrackTask(ExpirePairingAsync(device, timeout));
    }

    private async Task ExpirePairingAsync(KdeConnectDevice device, CancellationTokenSource timeout)
    {
        try
        {
            await Task.Delay(PairingTimeout, timeout.Token);
            KdeConnectChannel? channel;
            lock (_gate)
            {
                if (!ReferenceEquals(device.PairingTimeout, timeout) || device.IsPaired)
                {
                    return;
                }
                device.PairingTimeout = null;
                device.PairingState = KdeConnectPairingState.Unpaired;
                device.VerificationCode = null;
                device.PairingTimestamp = null;
                channel = device.Channel;
                PublishSnapshotLocked();
            }
            if (channel is not null)
            {
                await channel.SendAsync(CreatePairResponsePacket(false), _lifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Warning(LOG_CATEGORY, $"Could not expire pairing with '{device.Name}' cleanly", exception);
        }
        finally
        {
            timeout.Dispose();
        }
    }

    private void ResetPendingPairing(KdeConnectDevice device)
    {
        lock (_gate)
        {
            if (!device.IsPaired)
            {
                device.PairingTimeout?.Cancel();
                device.PairingTimeout = null;
                device.PairingState = KdeConnectPairingState.Unpaired;
                device.VerificationCode = null;
                device.PairingTimestamp = null;
                PublishSnapshotLocked();
            }
        }
    }

    private static byte[] CreatePairRequestPacket(long timestamp) => KdeConnectProtocol.CreatePacket(
        KdeConnectProtocol.PairType,
        writer =>
        {
            writer.WriteBoolean("pair", true);
            writer.WriteNumber("timestamp", timestamp);
        });

    private static byte[] CreatePairResponsePacket(bool pair) => KdeConnectProtocol.CreatePacket(
        KdeConnectProtocol.PairType,
        writer => writer.WriteBoolean("pair", pair));

    private string CreateVerificationCode(KdeConnectChannel channel, long timestamp)
    {
        var state = _state ?? throw new InvalidOperationException("KDE Connect persistent state is unavailable.");
        using var remote = X509CertificateLoader.LoadCertificate(channel.RemoteCertificateDer);
        var localKey = ExportPublicKey(state.Certificate);
        var remoteKey = ExportPublicKey(remote);
        var first = localKey.AsSpan().SequenceCompareTo(remoteKey) >= 0 ? localKey : remoteKey;
        var second = ReferenceEquals(first, localKey) ? remoteKey : localKey;
        var timestampBytes = Encoding.ASCII.GetBytes(timestamp.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
        var input = new byte[first.Length + second.Length + timestampBytes.Length];
        first.CopyTo(input, 0);
        second.CopyTo(input, first.Length);
        timestampBytes.CopyTo(input, first.Length + second.Length);
        return Convert.ToHexString(SHA256.HashData(input))[..8];
    }

    private static byte[] ExportPublicKey(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null)
        {
            return rsa.ExportSubjectPublicKeyInfo();
        }
        using var ecdsa = certificate.GetECDsaPublicKey();
        if (ecdsa is not null)
        {
            return ecdsa.ExportSubjectPublicKeyInfo();
        }
        using var dsa = certificate.GetDSAPublicKey();
        if (dsa is not null)
        {
            return dsa.ExportSubjectPublicKeyInfo();
        }
        throw new CryptographicException("Unsupported KDE Connect certificate public key type.");
    }

    private static Task SendBatteryRequestAsync(KdeConnectChannel channel, CancellationToken cancellationToken) =>
        channel.SendAsync(KdeConnectProtocol.CreatePacket(KdeConnectProtocol.BatteryRequestType), cancellationToken);

    private async Task ActivatePluginsSafeAsync(
        KdeConnectDevice device,
        KdeConnectChannel channel,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendBatteryRequestAsync(channel, cancellationToken);
            var content = await _clipboard.ReadTextAsync(cancellationToken);
            if (content is not null && !_clipboardPlugin.ShouldSuppressOutbound(content))
            {
                await channel.SendAsync(
                    ClipboardPlugin.CreateConnectPacket(content, _clipboard.LastChangeTimestamp),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (!ReferenceEquals(GetChannel(device), channel))
        {
        }
        catch (Exception exception)
        {
            AppLogger.Warning(LOG_CATEGORY, $"Could not activate KDE Connect plugins for '{device.Name}'", exception);
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                var clipboardVersion = _clipboard.Version;
                if (clipboardVersion != _clipboardVersion)
                {
                    _clipboardVersion = clipboardVersion;
                    await BroadcastClipboardAsync(cancellationToken);
                }
                KdeConnectDevice[] reconnectDevices;
                lock (_gate)
                {
                    var now = DateTimeOffset.UtcNow;
                    var staleIds = _devices.Values
                        .Where(device => !device.IsPaired && device.Channel is null &&
                            now - device.LastSeen > TimeSpan.FromMinutes(5))
                        .Select(static device => device.Id)
                        .ToArray();
                    foreach (var id in staleIds)
                    {
                        _devices.Remove(id);
                    }

                    reconnectDevices = _devices.Values
                        .Where(device => device.IsPaired && device.Channel is null && !device.Connecting &&
                            !string.IsNullOrWhiteSpace(device.Address) &&
                            device.TcpPort is >= KdeConnectProtocol.DiscoveryPort and <= KdeConnectProtocol.LastPort &&
                            now >= device.NextReconnectAt)
                        .ToArray();
                    PublishSnapshotLocked();
                }
                foreach (var device in reconnectDevices)
                {
                    TrackTask(EnsureChannelAsync(device, cancellationToken));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task BroadcastClipboardAsync(CancellationToken cancellationToken)
    {
        var content = await _clipboard.ReadTextAsync(cancellationToken);
        if (content is null || _clipboardPlugin.ShouldSuppressOutbound(content))
        {
            return;
        }
        KdeConnectChannel[] channels;
        lock (_gate)
        {
            channels = _devices.Values
                .Where(static device => device.IsPaired && device.Channel is not null)
                .Select(static device => device.Channel!)
                .Distinct()
                .ToArray();
        }
        var packet = ClipboardPlugin.CreateClipboardPacket(content);
        foreach (var channel in channels)
        {
            try
            {
                await channel.SendAsync(packet, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AppLogger.Warning(LOG_CATEGORY, "Could not synchronize a local clipboard change", exception);
            }
        }
    }

    private void OnPluginStateChanged(KdeConnectDevice device)
    {
        lock (_gate)
        {
            if (_devices.ContainsKey(device.Id))
            {
                PublishSnapshotLocked();
            }
        }
    }

    private static SocketException? FindSocketException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socketException)
            {
                return socketException;
            }
        }
        return null;
    }

    private static void ScheduleReconnectLocked(KdeConnectDevice device, bool afterConnectionFailure = false)
    {
        if (!device.IsPaired && !afterConnectionFailure)
        {
            device.NextReconnectAt = DateTimeOffset.MinValue;
            device.ReconnectAttempts = 0;
            return;
        }

        var exponent = Math.Min(device.ReconnectAttempts, 5);
        var delaySeconds = Math.Min(60, 2 * (1 << exponent));
        device.ReconnectAttempts = Math.Min(device.ReconnectAttempts + 1, 6);
        device.NextReconnectAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
    }

    private bool TryGetDevice(string deviceId, out KdeConnectDevice device, out string? error)
    {
        device = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            error = "A device id is required.";
            return false;
        }
        if (_disposed)
        {
            error = "KDE Connect has been stopped.";
            return false;
        }
        if (_state is null)
        {
            error = "KDE Connect is unavailable.";
            return false;
        }
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out device!))
            {
                error = "The device is unknown.";
                return false;
            }
        }
        return true;
    }

    private KdeConnectChannel? GetChannel(KdeConnectDevice device)
    {
        lock (_gate)
        {
            return device.Channel;
        }
    }

    private void SetUnpairedLocked(KdeConnectDevice device)
    {
        device.PairingTimeout?.Cancel();
        device.PairingTimeout = null;
        device.IsPaired = false;
        device.PairingState = KdeConnectPairingState.Unpaired;
        device.VerificationCode = null;
        device.PairingTimestamp = null;
        device.BatteryLevel = null;
        device.IsCharging = null;
        device.NextReconnectAt = DateTimeOffset.MinValue;
        device.ReconnectAttempts = 0;
    }

    private void PublishSnapshotLocked()
    {
        var devices = _devices.Values
            .OrderByDescending(static device => device.IsReachable)
            .ThenBy(static device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(static device => device.ToSnapshot())
            .ToArray();
        Volatile.Write(ref _snapshot, new KdeConnectSnapshot(devices));
    }

    private static TcpListener BindTcpListener(out int port)
    {
        for (var candidate = KdeConnectProtocol.DiscoveryPort; candidate <= KdeConnectProtocol.LastPort; candidate++)
        {
            var listener = new TcpListener(IPAddress.Any, candidate);
            try
            {
                listener.Start(16);
                port = candidate;
                return listener;
            }
            catch (SocketException)
            {
                listener.Stop();
            }
        }
        throw new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    private static Socket BindDiscoverySocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, KdeConnectProtocol.DiscoveryPort));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void TrackTask(Task task)
    {
        lock (_gate)
        {
            _ownedTasks.Add(task);
        }
        _ = task.ContinueWith(
            completed =>
            {
                lock (_gate)
                {
                    _ownedTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void StopNetworking()
    {
        _lifetime.Cancel();
        _listener?.Stop();
        _discovery?.Dispose();
        _listener = null;
        _discovery = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        StopNetworking();

        KdeConnectChannel[] channels;
        lock (_gate)
        {
            channels = _devices.Values
                .Select(static device => device.Channel)
                .OfType<KdeConnectChannel>()
                .Distinct()
                .ToArray();
            foreach (var device in _devices.Values)
            {
                device.PairingTimeout?.Cancel();
                device.PairingTimeout?.Dispose();
                device.PairingTimeout = null;
                device.Channel = null;
            }
        }
        foreach (var channel in channels)
        {
            channel.Dispose();
        }

        Task[] tasks;
        lock (_gate)
        {
            tasks = [.. _backgroundTasks, .. _ownedTasks];
        }
        var allTasks = Task.WhenAll(tasks);
        var stopped = tasks.Length == 0;
        try
        {
            stopped = stopped || allTasks.Wait(DisposeTimeout);
            if (!stopped)
            {
                AppLogger.Warning(LOG_CATEGORY, "KDE Connect background tasks did not stop before shutdown");
            }
        }
        catch (Exception exception)
        {
            stopped = allTasks.IsCompleted;
            if (!stopped)
            {
                AppLogger.Warning(LOG_CATEGORY, "KDE Connect did not stop cleanly", exception);
            }
        }

        if (stopped)
        {
            DisposeTaskResources();
        }
        else
        {
            _ = allTasks.ContinueWith(
                _ => DisposeTaskResources(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void DisposeTaskResources()
    {
        _state?.Dispose();
        _handshakeSlots.Dispose();
        _lifetime.Dispose();
    }
}
