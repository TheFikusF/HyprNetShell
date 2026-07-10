using System.Security.Cryptography;
using System.Text;
using HyprNetShell.Core.Bar;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Services;
using Tmds.DBus.Protocol;

namespace HyprNetShell.Core.Features.Sni;

internal sealed class SniTrayService : IBarDataService, IDisposable
{
    private const string WatcherName = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";
    private const string WatcherInterface = "org.kde.StatusNotifierWatcher";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string MenuInterface = "com.canonical.dbusmenu";
    private const uint NameFlagDoNotQueue = 4;

    private static readonly string[] ItemInterfaces =
        ["org.kde.StatusNotifierItem", "org.ayatana.NotificationItem"];

    private readonly AppIconResolver _iconResolver = new();
    private readonly HashSet<string> _pixmapFiles = [];
    private readonly Dictionary<string, string> _pixmapHashes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private DBusConnection? _connection;
    private StatusNotifierWatcher? _embeddedWatcher;
    private bool _initialized;

    public async ValueTask UpdateAsync(BarStateBuilder state, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (_connection is null) return;

        foreach (var item in await ReadItemsAsync(cancellationToken))
        {
            state.AddTrayItem(item);
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            var hostName = $"org.kde.StatusNotifierHost-{Environment.ProcessId}";
            _embeddedWatcher = await StatusNotifierWatcher.TryStartAsync(hostName);

            var connection = new DBusConnection(Dbus.SessionAddress);
            await connection.ConnectAsync();
            _connection = connection;
            try { await Dbus.RequestNameAsync(connection, hostName, NameFlagDoNotQueue); } catch { }
            try
            {
                await Dbus.CallAsync(
                    connection, WatcherName, WatcherPath, WatcherInterface, "RegisterStatusNotifierHost", "s",
                    (ref MessageWriter writer) => writer.WriteString(hostName));
            }
            catch { }
            _initialized = true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"tray: D-Bus initialization failed: {exception.Message}");
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private async Task<IReadOnlyList<TrayItemSnapshot>> ReadItemsAsync(CancellationToken cancellationToken)
    {
        if (_connection is null) return [];
        string[] registered;
        try
        {
            var property = await Dbus.CallAsync(
                _connection, WatcherName, WatcherPath, PropertiesInterface, "Get",
                reader => reader.ReadVariantValue(),
                "ss",
                (ref MessageWriter writer) =>
                {
                    writer.WriteString(WatcherInterface);
                    writer.WriteString("RegisteredStatusNotifierItems");
                }).WaitAsync(cancellationToken);
            registered = property.Unwrap().GetArray<string>();
        }
        catch { return []; }

        var items = new List<TrayItemSnapshot>(registered.Length);
        foreach (var id in registered.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = ParseItemId(id);
            if (parsed is null) continue;
            var item = await ReadItemAsync(id, parsed.Value.Bus, parsed.Value.Path, cancellationToken);
            if (item is not null) items.Add(item);
        }
        return items;
    }

    private async Task<TrayItemSnapshot?> ReadItemAsync(
        string id,
        string bus,
        string path,
        CancellationToken cancellationToken)
    {
        var properties = await ReadPropertiesAsync(bus, path, cancellationToken);
        if (properties is null || properties.StringValue("Status").Equals("Passive", StringComparison.OrdinalIgnoreCase))
            return null;

        var iconName = properties.StringValue("IconName");
        if (properties.StringValue("Status").Equals("NeedsAttention", StringComparison.OrdinalIgnoreCase))
            iconName = properties.StringValue("AttentionIconName") is { Length: > 0 } attention ? attention : iconName;

        var appId = properties.StringValue("Id");
        var title = properties.StringValue("Title");
        if (string.IsNullOrWhiteSpace(title)) title = TooltipTitle(properties);
        if (string.IsNullOrWhiteSpace(title)) title = bus.Split('.').LastOrDefault() ?? bus;
        if (appId == "chrome_status_icon_1" && TooltipTitle(properties) is { Length: > 0 } tooltip)
            appId = tooltip.ToLowerInvariant();

        var iconPath = ResolveIconPath(properties.StringValue("IconThemePath"), iconName, appId, bus, title)
                       ?? WriteIconPixmap(id, properties);
        var menuPath = properties.ObjectPathValue("Menu");
        var menu = await ReadMenuAsync(id, title, bus, menuPath, cancellationToken);
        return new TrayItemSnapshot(id, title, iconPath ?? "", menu, bus, path, menuPath);
    }

    private async Task<Dictionary<string, VariantValue>?> ReadPropertiesAsync(
        string bus,
        string path,
        CancellationToken cancellationToken)
    {
        if (_connection is null) return null;
        foreach (var itemInterface in ItemInterfaces)
        {
            try
            {
                return await Dbus.CallAsync(
                    _connection, bus, path, PropertiesInterface, "GetAll",
                    reader => reader.ReadDictionaryOfStringToVariantValue(),
                    "s", (ref MessageWriter writer) => writer.WriteString(itemInterface)).WaitAsync(cancellationToken);
            }
            catch { }
        }
        return null;
    }

    private async Task<PopupSnapshot?> ReadMenuAsync(
        string id,
        string title,
        string bus,
        string menuPath,
        CancellationToken cancellationToken)
    {
        if (_connection is null || string.IsNullOrWhiteSpace(menuPath) || menuPath == "/") return null;
        try
        {
            try
            {
                await Dbus.CallAsync(
                    _connection, bus, menuPath, MenuInterface, "AboutToShow", "i",
                    (ref MessageWriter writer) => writer.WriteInt32(0)).WaitAsync(cancellationToken);
            }
            catch { }

            var root = await Dbus.CallAsync(
                _connection, bus, menuPath, MenuInterface, "GetLayout",
                reader =>
                {
                    _ = reader.ReadUInt32();
                    reader.AlignStruct();
                    var nodeId = reader.ReadInt32();
                    var properties = reader.ReadDictionaryOfStringToVariantValue();
                    var children = reader.ReadArrayOfVariantValue();
                    return new MenuNode(nodeId, properties, children.Select(ParseMenuNode).Where(node => node is not null).Select(node => node!).ToArray());
                },
                "iias",
                (ref MessageWriter writer) =>
                {
                    writer.WriteInt32(0);
                    writer.WriteInt32(-1);
                    writer.WriteArray(new[] { "label", "enabled", "visible", "children-display", "type" });
                }).WaitAsync(cancellationToken);

            var rows = new List<PopupRowSnapshot>();
            AppendMenuRows(rows, root.Children, 0);
            return rows.Count == 0 ? null : new PopupSnapshot(id, title, rows);
        }
        catch { return null; }
    }

    private static MenuNode? ParseMenuNode(VariantValue raw)
    {
        var value = raw.Unwrap();
        if (value.Type != VariantValueType.Struct || value.Count < 3) return null;
        var id = value.GetItem(0).Unwrap().GetInt32();
        var dictionary = value.GetItem(1).Unwrap().GetDictionary<string, VariantValue>();
        var childrenValue = value.GetItem(2).Unwrap();
        var children = Enumerable.Range(0, childrenValue.Count)
            .Select(index => ParseMenuNode(childrenValue.GetItem(index)))
            .Where(node => node is not null)
            .Select(node => node!)
            .ToArray();
        return new MenuNode(id, dictionary, children);
    }

    private static void AppendMenuRows(List<PopupRowSnapshot> rows, IReadOnlyList<MenuNode> nodes, int depth)
    {
        foreach (var node in nodes)
        {
            if (!BoolValue(node.Properties, "visible", true)) continue;
            if (node.Properties.StringValue("type").Equals("separator", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(new PopupRowSnapshot("", PopupRowKind.Separator, false));
                continue;
            }
            var label = CleanMenuLabel(node.Properties.StringValue("label"));
            if (label.Length == 0) label = "(item)";
            if (depth > 0) label = new string(' ', depth * 2) + label;
            var submenu = node.Children.Count > 0 ||
                          node.Properties.StringValue("children-display").Equals("submenu", StringComparison.OrdinalIgnoreCase);
            rows.Add(new PopupRowSnapshot(label, submenu ? PopupRowKind.Header : PopupRowKind.Action,
                BoolValue(node.Properties, "enabled", true) && !submenu, submenu ? null : node.Id));
            if (submenu) AppendMenuRows(rows, node.Children, depth + 1);
        }
    }

    internal async Task TriggerMenuActionAsync(TrayItemSnapshot item, int actionId)
    {
        if (_connection is null || string.IsNullOrWhiteSpace(item.MenuPath)) return;
        try
        {
            await Dbus.CallAsync(
                _connection, item.BusName, item.MenuPath, MenuInterface, "Event", "isvu",
                (ref MessageWriter writer) =>
                {
                    writer.WriteInt32(actionId);
                    writer.WriteString("clicked");
                    writer.WriteVariantInt32(0);
                    writer.WriteUInt32((uint)Environment.TickCount64);
                });
        }
        catch { }
    }

    private string? ResolveIconPath(string themePath, string iconName, string appId, string bus, string title)
    {
        if (!string.IsNullOrWhiteSpace(themePath) && !string.IsNullOrWhiteSpace(iconName))
        {
            foreach (var extension in new[] { ".png", ".svg", ".xpm" })
            {
                var direct = global::System.IO.Path.Combine(themePath, iconName + extension);
                if (File.Exists(direct)) return direct;
            }
            foreach (var size in new[] { "22x22", "24x24", "32x32", "48x48", "16x16" })
            foreach (var extension in new[] { ".png", ".svg" })
            {
                var nested = global::System.IO.Path.Combine(themePath, "hicolor", size, "apps", iconName + extension);
                if (File.Exists(nested)) return nested;
            }
        }
        return _iconResolver.TryResolve(iconName) ?? _iconResolver.TryResolve(appId) ??
               _iconResolver.TryResolve(bus) ?? _iconResolver.TryResolve(title);
    }

    // SNI pixmaps are a(iiay), with each pixel encoded as ARGB32. Keep only the
    // largest frame, as the Go tray does, and materialize it as a PAM image so
    // the existing image renderer can load it without retaining extra buffers.
    private string? WriteIconPixmap(string itemId, IReadOnlyDictionary<string, VariantValue> properties)
    {
        if (!properties.TryGetValue("IconPixmap", out var raw)) return null;
        var frames = raw.Unwrap();
        if (frames.Type != VariantValueType.Array) return null;

        byte[]? best = null;
        var bestWidth = 0;
        var bestHeight = 0;
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames.GetItem(index).Unwrap();
            if (frame.Type != VariantValueType.Struct || frame.Count < 3) continue;
            var width = frame.GetItem(0).Unwrap().GetInt32();
            var height = frame.GetItem(1).Unwrap().GetInt32();
            var data = frame.GetItem(2).Unwrap().GetArray<byte>();
            if (width <= 0 || height <= 0 || (long)width * height * 4 != data.Length ||
                (long)width * height <= (long)bestWidth * bestHeight) continue;
            best = data;
            bestWidth = width;
            bestHeight = height;
        }
        if (best is null) return null;

        var rgba = new byte[best.Length];
        for (var index = 0; index < best.Length; index += 4)
        {
            rgba[index] = best[index + 1];
            rgba[index + 1] = best[index + 2];
            rgba[index + 2] = best[index + 3];
            rgba[index + 3] = best[index];
        }

        try
        {
            // The path is stable per item. When an icon changes, Renderer
            // replaces the existing texture instead of growing its cache.
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(itemId)));
            var contentHash = Convert.ToHexString(SHA256.HashData(rgba));
            var directory = global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(),
                $"hyprnetshell-tray-{Environment.UserName}");
            Directory.CreateDirectory(directory);
            var file = global::System.IO.Path.Combine(directory, hash + ".pam");
            if (!_pixmapHashes.TryGetValue(itemId, out var previousHash) || previousHash != contentHash || !File.Exists(file))
            {
                var header = Encoding.ASCII.GetBytes(
                    $"P7\nWIDTH {bestWidth}\nHEIGHT {bestHeight}\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n");
                var image = new byte[header.Length + rgba.Length];
                Buffer.BlockCopy(header, 0, image, 0, header.Length);
                Buffer.BlockCopy(rgba, 0, image, header.Length, rgba.Length);
                File.WriteAllBytes(file, image);
                _pixmapHashes[itemId] = contentHash;
            }
            _pixmapFiles.Add(file);
            return file;
        }
        catch { return null; }
    }

    private static (string Bus, string Path)? ParseItemId(string id)
    {
        id = id.Trim();
        var slash = id.IndexOf('/');
        if (id.Length == 0 || id.StartsWith('/')) return null;
        return slash < 0 ? (id, "/StatusNotifierItem") : (id[..slash], id[slash..]);
    }

    private static bool BoolValue(IReadOnlyDictionary<string, VariantValue> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value) && value.Unwrap().Type == VariantValueType.Bool
            ? value.Unwrap().GetBool()
            : fallback;

    private static string TooltipTitle(IReadOnlyDictionary<string, VariantValue> values)
    {
        if (!values.TryGetValue("ToolTip", out var raw)) return "";
        var value = raw.Unwrap();
        if (value.Type != VariantValueType.Struct || value.Count < 4) return "";
        var title = value.GetItem(2).Unwrap().GetString().Trim();
        return title.Length > 0 ? title : value.GetItem(3).Unwrap().GetString().Trim();
    }

    private static string CleanMenuLabel(string label) =>
        label.Trim().Replace("__", "\0", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal)
            .Replace("\0", "_", StringComparison.Ordinal);

    public void Dispose()
    {
        _connection?.Dispose();
        _embeddedWatcher?.Dispose();
        _initializeGate.Dispose();
        foreach (var file in _pixmapFiles)
        {
            try { File.Delete(file); } catch { }
        }
    }

    private sealed record MenuNode(
        int Id,
        IReadOnlyDictionary<string, VariantValue> Properties,
        IReadOnlyList<MenuNode> Children);
}
