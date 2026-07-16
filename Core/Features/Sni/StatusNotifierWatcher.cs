using System.Xml.Linq;
using HyprNetShell.Core.Logging;
using Tmds.DBus.Protocol;

namespace HyprNetShell.Core.Features.Sni;

// This is a direct C# port of status-bar/internal/services/sni_watcher.go.
internal sealed class StatusNotifierWatcher : IPathMethodHandler, IDisposable
{
    private const string WATCHER_NAME = "org.kde.StatusNotifierWatcher";
    private const string WATCHER_PATH = "/StatusNotifierWatcher";
    private const string WATCHER_INTERFACE = "org.kde.StatusNotifierWatcher";
    private const string DEFAULT_ITEM_PATH = "/StatusNotifierItem";
    private const int MAX_INTROSPECTION_NODES = 128;
    private const uint NAME_FLAG_ALLOW_REPLACEMENT = 1;
    private const uint NAME_FLAG_REPLACE_EXISTING = 2;
    private const uint NAME_FLAG_DO_NOT_QUEUE = 4;
    private const uint REQUEST_NAME_REPLY_PRIMARY_OWNER = 1;

    private readonly object _gate = new();
    private readonly DBusConnection _connection = new(Dbus.SessionAddress);
    private readonly Dictionary<string, SniEntry> _items = [];
    private readonly Dictionary<string, SniEntry> _hosts = [];
    private IDisposable? _nameOwnerSubscription;
    private bool _ownsName;

    public string Path => WATCHER_PATH;
    public bool HandlesChildPaths => false;

    private StatusNotifierWatcher(string preRegisteredHostName)
    {
        if (!string.IsNullOrWhiteSpace(preRegisteredHostName))
        {
            var entry = new SniEntry(preRegisteredHostName, "/StatusNotifierHost");
            _hosts[Key(entry.BusName, entry.ObjectPath)] = entry;
        }
    }

    public static async Task<StatusNotifierWatcher?> TryStartAsync(string preRegisteredHostName)
    {
        var watcher = new StatusNotifierWatcher(preRegisteredHostName);
        try
        {
            await watcher._connection.ConnectAsync();
            watcher._connection.AddMethodHandler(watcher); // Export before claiming the name.
            var reply = await Dbus.RequestNameAsync(
                watcher._connection,
                WATCHER_NAME,
                NAME_FLAG_DO_NOT_QUEUE | NAME_FLAG_ALLOW_REPLACEMENT | NAME_FLAG_REPLACE_EXISTING);
            if (reply != REQUEST_NAME_REPLY_PRIMARY_OWNER)
            {
                watcher.Dispose();
                return null;
            }

            watcher._ownsName = true;
            watcher._nameOwnerSubscription = await watcher._connection.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Interface = Dbus.BUS_INTERFACE,
                    Member = "NameOwnerChanged",
                },
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    return (reader.ReadString(), reader.ReadString(), reader.ReadString());
                },
                static notification =>
                {
                    if (notification.HasValue)
                    {
                        var change = notification.Value;
                        _ = ((StatusNotifierWatcher)notification.State!).HandleNameOwnerChangedAsync(change.Item1, change.Item3);
                    }
                },
                false,
                ObserverFlags.None,
                watcher);

            watcher.EmitHostSignal("StatusNotifierHostRegistered");
            _ = watcher.DiscoverExistingItemsAsync();
            return watcher;
        }
        catch (Exception exception)
        {
            AppLogger.Error("TrayWatcher", "Could not start embedded watcher", exception);
            watcher.Dispose();
            return null;
        }
    }

    public async ValueTask HandleMethodAsync(MethodContext context)
    {
        try
        {
            var request = context.Request;
            var member = request.MemberAsString ?? "";
            var @interface = request.InterfaceAsString ?? "";
            if (@interface == WATCHER_INTERFACE && member == "RegisterStatusNotifierItem")
            {
                var service = request.GetBodyReader().ReadString();
                var (busName, objectPath) = await ParseItemServiceAsync(request.SenderAsString ?? "", service);
                await RegisterItemAsync(busName, objectPath);
                ReplyEmpty(context);
                return;
            }
            if (@interface == WATCHER_INTERFACE && member == "RegisterStatusNotifierHost")
            {
                RegisterHost(request.SenderAsString ?? "", request.GetBodyReader().ReadString());
                ReplyEmpty(context);
                return;
            }
            if (@interface == "org.freedesktop.DBus.Properties")
            {
                HandleProperties(context, member);
                return;
            }
            if (@interface == "org.freedesktop.DBus.Introspectable" && member == "Introspect")
            {
                using var writer = context.CreateReplyWriter("s");
                writer.WriteString(INTROSPECTION_XML);
                context.Reply(writer.CreateMessage());
                return;
            }

            context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", "Unknown StatusNotifierWatcher method");
        }
        catch (Exception exception)
        {
            context.ReplyError("org.freedesktop.DBus.Error.Failed", exception.Message);
        }
    }

    private async Task<(string BusName, string ObjectPath)> ParseItemServiceAsync(string sender, string service)
    {
        if (service.StartsWith('/')) return (sender, service);
        if (service.StartsWith(':')) return (service, DEFAULT_ITEM_PATH);
        return (await Dbus.GetNameOwnerAsync(_connection, service), DEFAULT_ITEM_PATH);
    }

    private async Task<bool> RegisterItemAsync(string busName, string objectPath)
    {
        if (string.IsNullOrWhiteSpace(busName) || !IsObjectPath(objectPath) ||
            !await HasItemInterfaceAsync(busName, objectPath))
        {
            return false;
        }

        var entry = new SniEntry(busName, objectPath);
        lock (_gate)
        {
            if (!_items.TryAdd(Key(busName, objectPath), entry)) return false;
        }
        EmitItemSignal("StatusNotifierItemRegistered", busName + objectPath);
        EmitPropertiesChanged();
        return true;
    }

    private void RegisterHost(string sender, string service)
    {
        var objectPath = service.StartsWith('/') ? service : "/StatusNotifierHost";
        var entry = new SniEntry(sender, objectPath);
        lock (_gate)
        {
            if (!_hosts.TryAdd(Key(sender, objectPath), entry)) return;
        }
        EmitPropertiesChanged();
        EmitHostSignal("StatusNotifierHostRegistered");
    }

    private void HandleProperties(MethodContext context, string member)
    {
        var reader = context.Request.GetBodyReader();
        var requestedInterface = reader.ReadString();
        if (requestedInterface != WATCHER_INTERFACE)
        {
            context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "Unknown interface");
            return;
        }

        if (member == "Get")
        {
            var property = reader.ReadString();
            using var writer = context.CreateReplyWriter("v");
            switch (property)
            {
                case "RegisteredStatusNotifierItems": writer.WriteVariant(VariantValue.Array(RegisteredItems())); break;
                case "IsStatusNotifierHostRegistered": writer.WriteVariantBool(true); break;
                case "ProtocolVersion": writer.WriteVariantInt32(0); break;
                default:
                    context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "Unknown property");
                    return;
            }
            context.Reply(writer.CreateMessage());
            return;
        }

        if (member == "GetAll")
        {
            using var writer = context.CreateReplyWriter("a{sv}");
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["RegisteredStatusNotifierItems"] = VariantValue.Array(RegisteredItems()),
                ["IsStatusNotifierHostRegistered"] = VariantValue.Bool(true),
                ["ProtocolVersion"] = VariantValue.Int32(0),
            });
            context.Reply(writer.CreateMessage());
            return;
        }

        if (member == "Set")
        {
            ReplyEmpty(context);
            return;
        }
        context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", "Unknown Properties method");
    }

    private string[] RegisteredItems()
    {
        lock (_gate) return _items.Values.Select(entry => entry.BusName + entry.ObjectPath).ToArray();
    }

    private async Task DiscoverExistingItemsAsync()
    {
        string[] names;
        try { names = await _connection.ListServicesAsync(); }
        catch { return; }
        foreach (var name in names.Where(name => name.StartsWith(':')))
        {
            await DiscoverBusNameAsync(name);
        }
    }

    private async Task DiscoverBusNameAsync(string busName)
    {
        if (!busName.StartsWith(':')) return;
        foreach (var objectPath in await DiscoverItemPathsAsync(busName))
        {
            await RegisterItemAsync(busName, objectPath);
        }
    }

    private async Task<IReadOnlyList<string>> DiscoverItemPathsAsync(string busName)
    {
        var paths = new List<string>();
        foreach (var candidate in new[] { DEFAULT_ITEM_PATH, "/org/ayatana/NotificationItem" })
        {
            if (await HasItemInterfaceAsync(busName, candidate)) paths.Add(candidate);
        }
        if (paths.Count > 0) return paths;

        var queue = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { "/" };
        queue.Enqueue("/");
        while (queue.Count > 0 && visited.Count <= MAX_INTROSPECTION_NODES)
        {
            var current = queue.Dequeue();
            var xml = await TryIntrospectAsync(busName, current);
            if (xml is null) continue;
            XDocument document;
            try { document = XDocument.Parse(xml); } catch { continue; }
            if (document.Descendants("interface").Any(IsItemInterface))
            {
                paths.Add(current);
                continue;
            }
            foreach (var node in document.Root?.Elements("node") ?? [])
            {
                var name = node.Attribute("name")?.Value?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var child = name.StartsWith('/') ? name : current.TrimEnd('/') + "/" + name;
                if (IsObjectPath(child) && visited.Add(child)) queue.Enqueue(child);
            }
        }
        return paths;
    }

    private async Task<bool> HasItemInterfaceAsync(string busName, string objectPath)
    {
        var xml = await TryIntrospectAsync(busName, objectPath);
        if (xml is not null)
        {
            try
            {
                if (XDocument.Parse(xml).Descendants("interface").Any(IsItemInterface)) return true;
            }
            catch { }
        }

        // Electron/Chromium tray items can expose the SNI properties while
        // returning empty or incomplete introspection XML. Probe GetAll just
        // like the reference implementation so those items are not rejected.
        foreach (var itemInterface in new[] { "org.kde.StatusNotifierItem", "org.ayatana.NotificationItem" })
        {
            try
            {
                var properties = await Dbus.CallAsync(
                    _connection, busName, objectPath, "org.freedesktop.DBus.Properties", "GetAll",
                    reader => reader.ReadDictionaryOfStringToVariantValue(),
                    "s", (ref MessageWriter writer) => writer.WriteString(itemInterface))
                    .WaitAsync(TimeSpan.FromSeconds(2));
                if (properties.Count > 0) return true;
            }
            catch { }
        }
        return false;
    }

    private static bool IsItemInterface(XElement element) =>
        element.Attribute("name")?.Value is "org.kde.StatusNotifierItem" or "org.ayatana.NotificationItem";

    private async Task<string?> TryIntrospectAsync(string busName, string objectPath)
    {
        try
        {
            return await Dbus.CallAsync(
                _connection, busName, objectPath, "org.freedesktop.DBus.Introspectable", "Introspect",
                reader => reader.ReadString()).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { return null; }
    }

    private async Task HandleNameOwnerChangedAsync(string name, string newOwner)
    {
        if (string.IsNullOrEmpty(newOwner) && name.StartsWith(':')) RemoveBusName(name);
        else if (!string.IsNullOrEmpty(newOwner) && name.StartsWith(':')) await DiscoverBusNameAsync(name);
        else if (!string.IsNullOrEmpty(newOwner) &&
                 (name.StartsWith("org.kde.StatusNotifierItem") || name.StartsWith("org.ayatana.NotificationItem")))
            await DiscoverBusNameAsync(newOwner);
    }

    private void RemoveBusName(string uniqueName)
    {
        List<SniEntry> removedItems;
        var removedHost = false;
        lock (_gate)
        {
            removedItems = _items.Values.Where(entry => entry.BusName == uniqueName).ToList();
            foreach (var entry in removedItems) _items.Remove(Key(entry.BusName, entry.ObjectPath));
            foreach (var entry in _hosts.Values.Where(entry => entry.BusName == uniqueName).ToArray())
            {
                _hosts.Remove(Key(entry.BusName, entry.ObjectPath));
                removedHost = true;
            }
        }
        foreach (var entry in removedItems) EmitItemSignal("StatusNotifierItemUnregistered", entry.BusName + entry.ObjectPath);
        if (removedHost) EmitHostSignal("StatusNotifierHostUnregistered");
        if (removedItems.Count > 0 || removedHost) EmitPropertiesChanged();
    }

    private void EmitItemSignal(string member, string value) =>
        EmitSignal(member, "s", (ref MessageWriter writer) => writer.WriteString(value));
    private void EmitHostSignal(string member) => EmitSignal(member, "", null);

    private void EmitPropertiesChanged()
    {
        EmitSignal(
            "PropertiesChanged",
            "sa{sv}as",
            (ref MessageWriter writer) =>
            {
                writer.WriteString(WATCHER_INTERFACE);
                writer.WriteDictionary(new Dictionary<string, VariantValue>
                {
                    ["RegisteredStatusNotifierItems"] = VariantValue.Array(RegisteredItems()),
                    ["IsStatusNotifierHostRegistered"] = VariantValue.Bool(true),
                });
                writer.WriteArray(Array.Empty<string>());
            },
            "org.freedesktop.DBus.Properties");
    }

    private void EmitSignal(string member, string signature, MessageBodyWriter? body, string? @interface = null)
    {
        if (!_ownsName) return;
        var writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteSignalHeader(null!, WATCHER_PATH, @interface ?? WATCHER_INTERFACE, member, signature);
            body?.Invoke(ref writer);
            _connection.TrySendMessage(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void ReplyEmpty(MethodContext context)
    {
        using var writer = context.CreateReplyWriter("");
        context.Reply(writer.CreateMessage());
    }

    private static string Key(string busName, string objectPath) => busName + "|" + objectPath;
    private static bool IsObjectPath(string value) => value.StartsWith('/') && !value.Contains("//", StringComparison.Ordinal);

    public void Dispose()
    {
        _nameOwnerSubscription?.Dispose();
        _connection.Dispose();
    }

    private readonly record struct SniEntry(string BusName, string ObjectPath);

    private const string INTROSPECTION_XML = """
        <!DOCTYPE node PUBLIC "-//freedesktop//DTD D-BUS Object Introspection 1.0//EN" "http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd">
        <node name="/StatusNotifierWatcher">
          <interface name="org.kde.StatusNotifierWatcher">
            <method name="RegisterStatusNotifierItem"><arg name="service" type="s" direction="in"/></method>
            <method name="RegisterStatusNotifierHost"><arg name="service" type="s" direction="in"/></method>
            <property name="RegisteredStatusNotifierItems" type="as" access="read"/>
            <property name="IsStatusNotifierHostRegistered" type="b" access="read"/>
            <property name="ProtocolVersion" type="i" access="read"/>
            <signal name="StatusNotifierItemRegistered"><arg name="service" type="s"/></signal>
            <signal name="StatusNotifierItemUnregistered"><arg name="service" type="s"/></signal>
            <signal name="StatusNotifierHostRegistered"/><signal name="StatusNotifierHostUnregistered"/>
          </interface>
          <interface name="org.freedesktop.DBus.Properties">
            <method name="Get"><arg type="s" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="out"/></method>
            <method name="GetAll"><arg type="s" direction="in"/><arg type="a{sv}" direction="out"/></method>
            <method name="Set"><arg type="s" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="in"/></method>
          </interface>
          <interface name="org.freedesktop.DBus.Introspectable"><method name="Introspect"><arg type="s" direction="out"/></method></interface>
        </node>
        """;
}
