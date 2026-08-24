using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyprNetShell.Core.Bar;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.Rendering;
using Tmds.DBus.Protocol;

namespace HyprNetShell.Core.Features.System;

/// <summary>Owns the freedesktop notification bus name and keeps notification history in-process.</summary>
internal sealed partial class NotificationService : IPathMethodHandler, IDisposable
{
    private const string BUS_NAME = "org.freedesktop.Notifications";
    private const string OBJECT_PATH = "/org/freedesktop/Notifications";
    private const string INTERFACE = "org.freedesktop.Notifications";
    private const int MAX_NOTIFICATIONS = 100;
    private const int MAX_IMAGE_BYTES = 64 * 1024 * 1024;

    private static readonly TimeSpan PopupDuration = TimeSpan.FromSeconds(6);

    private readonly Lock _gate = new();
    private readonly DBusConnection _connection = new(Dbus.SessionAddress);
    private readonly HyprlandService _hyprland;
    private readonly IHyprctl _hyprctl;
    private readonly HistoryStore _history;
    private readonly AppIconResolver _iconResolver = new();
    private readonly List<NotificationSnapshot> _items = [];
    private uint _nextId = 1;
    private bool _ownsName;
    private bool _doNotDisturb;

    public string Path => OBJECT_PATH;
    public bool HandlesChildPaths => false;

    public NotificationService(HyprlandService hyprland, IHyprctl hyprctl, HistoryStore history)
    {
        _hyprland = hyprland;
        _hyprctl = hyprctl;
        _history = history;
        _items.AddRange(history.LoadNotifications().Select(item => new NotificationSnapshot(
            item.RuntimeId,
            item.Title,
            item.Body,
            item.AppName,
            item.DesktopEntry,
            item.IconName,
            null,
            item.Image,
            [],
            item.Resident,
            item.ReceivedAt,
            DateTime.MinValue)));
        _history.LimitsChanged += ApplyHistoryLimit;
        // Claim the bus name before the bar starts drawing so notifications sent
        // during startup cannot race the embedded daemon initialization.
        StartAsync().GetAwaiter().GetResult();
    }

    public NotificationsSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new NotificationsSnapshot(_items.Count, _items.ToArray(), _doNotDisturb);
            }
        }
    }

    public void ToggleDoNotDisturb()
    {
        lock (_gate)
        {
            _doNotDisturb = !_doNotDisturb;
            if (_doNotDisturb)
            {
                for (var index = 0; index < _items.Count; index++)
                {
                    _items[index] = _items[index] with { PopupUntil = DateTime.MinValue };
                }
            }
        }
    }

    public async ValueTask HandleMethodAsync(MethodContext context)
    {
        try
        {
            var member = context.Request.MemberAsString ?? "";
            var @interface = context.Request.InterfaceAsString ?? "";
            if (@interface == INTERFACE && member == "Notify")
            {
                HandleNotify(context);
                return;
            }

            if (@interface == INTERFACE && member == "CloseNotification")
            {
                var id = context.Request.GetBodyReader().ReadUInt32();
                Close(id, 3);
                ReplyEmpty(context);
                return;
            }

            if (@interface == INTERFACE && member == "GetCapabilities")
            {
                using var writer = context.CreateReplyWriter("as");
                writer.WriteArray(CAPABILITIES_STRING);
                context.Reply(writer.CreateMessage());
                return;
            }

            if (@interface == INTERFACE && member == "GetServerInformation")
            {
                using var writer = context.CreateReplyWriter("ssss");
                writer.WriteString("HyprNetShell");
                writer.WriteString("HyprNetShell");
                writer.WriteString("1.0");
                writer.WriteString("1.2");
                context.Reply(writer.CreateMessage());
                return;
            }

            if (@interface == "org.freedesktop.DBus.Introspectable" && member == "Introspect")
            {
                using var writer = context.CreateReplyWriter("s");
                writer.WriteString(INTROSPECTION_XML);
                context.Reply(writer.CreateMessage());
                return;
            }

            context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", "Unknown notification method");
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Notifications", "Invalid D-Bus notification method call", exception);
            context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", exception.Message);
        }
    }

    public void Dismiss(uint id) => Close(id, 2);

    public void Activate(uint id) => _ = ActivateAsync(id);

    public void InvokeAction(uint id, string actionKey)
    {
        NotificationSnapshot? notification;
        lock (_gate)
        {
            notification = _items.FirstOrDefault(item => item.Id == id);
        }

        if (notification is null || !notification.Actions.Any(action => action.Key == actionKey))
        {
            AppLogger.Warning("Notifications", $"Ignored unknown action '{actionKey}' for notification {id}");
            return;
        }

        try
        {
            EmitActionInvoked(id, actionKey);
            if (!notification.Resident)
            {
                Close(id, 2);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Notifications", $"Could not invoke action '{actionKey}' for notification {id}", exception);
        }
    }

    public void Clear()
    {
        uint[] ids;
        lock (_gate)
        {
            ids = _items.Select(item => item.Id).ToArray();
            _items.Clear();
        }

        _history.ClearNotifications();
        foreach (var id in ids)
        {
            EmitClosed(id, 2);
        }
    }

    private async Task StartAsync()
    {
        try
        {
            await _connection.ConnectAsync();
            _connection.AddMethodHandler(this);
            const uint ALLOW_REPLACEMENT = 1;
            const uint REPLACE_EXISTING = 2;
            const uint DO_NOT_QUEUE = 4;
            _ownsName = await Dbus.RequestNameAsync(
                _connection,
                BUS_NAME,
                ALLOW_REPLACEMENT | REPLACE_EXISTING | DO_NOT_QUEUE) == 1;
            if (!_ownsName)
            {
                AppLogger.Warning("Notifications", "Another notification daemon owns org.freedesktop.Notifications");
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Notifications", "Could not start embedded daemon", exception);
        }
    }

    private void HandleNotify(MethodContext context)
    {
        var reader = context.Request.GetBodyReader();
        var appName = reader.ReadString();
        var replacesId = reader.ReadUInt32();
        var appIcon = reader.ReadString();
        var summary = reader.ReadString();
        var body = reader.ReadString();
        var actions = ParseActions(reader.ReadArrayOfString());
        var hints = reader.ReadDictionaryOfStringToVariantValue();
        _ = reader.ReadInt32(); // Popup lifetime is managed separately from notification history.

        var desktopEntry = hints.StringValue("desktop-entry");
        var imageData = ReadImageData(hints, "image-data") ?? ReadImageData(hints, "image_data");
        var iconName = FirstNonEmpty(
            hints.StringValue("image-path"),
            hints.StringValue("image_path"),
            appIcon);
        if (imageData is null && string.IsNullOrWhiteSpace(iconName))
        {
            imageData = ReadImageData(hints, "icon_data");
        }

        if (imageData is null && string.IsNullOrWhiteSpace(iconName))
        {
            iconName = desktopEntry;
        }

        var historyImage = EncodeHistoryImage(imageData, iconName);
        var now = DateTime.UtcNow;
        uint id;
        lock (_gate)
        {
            var replacementIndex = replacesId == 0
                ? -1
                : _items.FindIndex(item => item.Id == replacesId);
            id = replacementIndex >= 0 ? replacesId : NextId();
            var notification = new NotificationSnapshot(
                id,
                PlainText(string.IsNullOrWhiteSpace(summary) ? appName : summary),
                PlainText(body),
                appName,
                desktopEntry,
                iconName,
                imageData,
                null,
                actions,
                BoolHint(hints, "resident"),
                now,
                _doNotDisturb ? DateTime.MinValue : now + PopupDuration);

            if (replacementIndex >= 0)
            {
                _items.RemoveAt(replacementIndex);
            }

            _items.Insert(0, notification);
            if (_items.Count > _history.NotificationLimit)
            {
                _items.RemoveRange(_history.NotificationLimit, _items.Count - _history.NotificationLimit);
            }

            _history.SaveNotification(notification, historyImage);
        }

        using var writer = context.CreateReplyWriter("u");
        writer.WriteUInt32(id);
        context.Reply(writer.CreateMessage());
    }

    private uint NextId()
    {
        while (_nextId == 0 || _items.Any(item => item.Id == _nextId))
        {
            _nextId++;
        }

        return _nextId++;
    }

    private void Close(uint id, uint reason)
    {
        var removed = false;
        lock (_gate)
        {
            var index = _items.FindIndex(item => item.Id == id);
            if (index >= 0)
            {
                _items.RemoveAt(index);
                removed = true;
            }
        }

        if (removed)
        {
            _history.DeleteNotification(id);
            EmitClosed(id, reason);
        }
    }

    private void EmitClosed(uint id, uint reason)
    {
        if (!_ownsName)
        {
            return;
        }

        var writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteSignalHeader(null!, OBJECT_PATH, INTERFACE, "NotificationClosed", "uu");
            writer.WriteUInt32(id);
            writer.WriteUInt32(reason);
            _connection.TrySendMessage(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private async Task ActivateAsync(uint id)
    {
        try
        {
            NotificationSnapshot? notification;
            lock (_gate)
            {
                notification = _items.FirstOrDefault(item => item.Id == id);
            }

            if (notification is null)
            {
                return;
            }

            var defaultAction = notification.Actions.FirstOrDefault(action => action.Key == "default");
            if (defaultAction is not null)
            {
                EmitActionInvoked(id, defaultAction.Key);
            }

            AppLogger.Info("Notifications", JsonSerializer.Serialize(notification));
            var window = FindWindow(notification);
            if (window is not null && !string.IsNullOrWhiteSpace(window.Address))
            {
                AppLogger.Info("Notifications", $"focusing: {window.ClassName}@{window.Address}");
                await _hyprctl.FocusWindowAsync(window.Address);
            }

            if (!notification.Resident)
            {
                Close(id, 2);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Notifications", $"Could not activate notification {id}", exception);
        }
    }

    private WindowSummary? FindWindow(NotificationSnapshot notification)
    {
        var identifiers = AppIdentifiers(notification).ToArray();
        if (identifiers.Length == 0)
        {
            return null;
        }

        var windows = _hyprland.Snapshot.Windows;
        foreach (var identifier in identifiers)
        {
            var normalizedIdentifier = NormalizeAppIdentifier(identifier);
            var exact = windows.FirstOrDefault(window =>
                NormalizeAppIdentifier(window.ClassName) == normalizedIdentifier ||
                NormalizeAppIdentifier(window.InitialClassName) == normalizedIdentifier);
            if (exact is not null)
            {
                return exact;
            }
        }

        AppLogger.Info(
            "Notifications",
            $"No window matched notification {notification.Id}; identifiers=[{string.Join(", ", identifiers)}]; " +
            $"windows=[{string.Join(", ", windows.Select(window => $"{window.ClassName}/{window.InitialClassName}@{window.Address}"))}]");
        return null;
    }

    private static IEnumerable<string> AppIdentifiers(NotificationSnapshot notification)
    {
        if (!string.IsNullOrWhiteSpace(notification.DesktopEntry))
        {
            var desktopEntry = notification.DesktopEntry;
            yield return desktopEntry;

            if (desktopEntry.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
            {
                yield return desktopEntry[..^8];
            }

            var lastSegment = desktopEntry.Split('.').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(lastSegment))
            {
                yield return lastSegment;
            }
        }

        if (!string.IsNullOrWhiteSpace(notification.AppName))
        {
            yield return notification.AppName;
        }
    }

    private static string NormalizeAppIdentifier(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private void EmitActionInvoked(uint id, string actionKey)
    {
        if (!_ownsName)
        {
            return;
        }

        var writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteSignalHeader(null!, OBJECT_PATH, INTERFACE, "ActionInvoked", "us");
            writer.WriteUInt32(id);
            writer.WriteString(actionKey);
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

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static string PlainText(string value) =>
        WebUtility.HtmlDecode(
            WhitespaceRegex().Replace(MarkupRegex().Replace(value, " "), " ").Trim());

    private static IReadOnlyList<NotificationActionSnapshot> ParseActions(IReadOnlyList<string> values)
    {
        var actions = new List<NotificationActionSnapshot>(values.Count / 2);
        for (var index = 0; index + 1 < values.Count; index += 2)
        {
            var key = values[index];
            var label = PlainText(values[index + 1]);
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(label))
            {
                actions.Add(new NotificationActionSnapshot(key, label));
            }
        }

        return actions;
    }

    private static bool BoolHint(IReadOnlyDictionary<string, VariantValue> hints, string key) =>
        hints.TryGetValue(key, out var value) &&
        value.Unwrap().Type == VariantValueType.Bool &&
        value.Unwrap().GetBool();

    private static RawImageData? ReadImageData(
        IReadOnlyDictionary<string, VariantValue> hints,
        string key)
    {
        if (!hints.TryGetValue(key, out var raw))
        {
            return null;
        }

        try
        {
            var value = raw.Unwrap();
            if (value.Type != VariantValueType.Struct || value.Count != 7)
            {
                return null;
            }

            var width = value.GetItem(0).Unwrap().GetInt32();
            var height = value.GetItem(1).Unwrap().GetInt32();
            var rowstride = value.GetItem(2).Unwrap().GetInt32();
            var hasAlpha = value.GetItem(3).Unwrap().GetBool();
            var bitsPerSample = value.GetItem(4).Unwrap().GetInt32();
            var channels = value.GetItem(5).Unwrap().GetInt32();
            var data = value.GetItem(6).Unwrap().GetArray<byte>();

            var expectedChannels = hasAlpha ? 4 : 3;
            var packedRowBytes = (long)width * expectedChannels;
            var sourceBytes = (long)rowstride * height;
            var outputBytes = (long)width * height * 4;
            if (width <= 0 || height <= 0 || bitsPerSample != 8 || channels != expectedChannels ||
                rowstride < packedRowBytes || sourceBytes > data.Length ||
                outputBytes <= 0 || outputBytes > MAX_IMAGE_BYTES)
            {
                return null;
            }

            var rgba = new byte[(int)outputBytes];
            for (var y = 0; y < height; y++)
            {
                var sourceOffset = y * rowstride;
                var destinationOffset = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var sourcePixel = sourceOffset + x * channels;
                    var destinationPixel = destinationOffset + x * 4;
                    rgba[destinationPixel] = data[sourcePixel];
                    rgba[destinationPixel + 1] = data[sourcePixel + 1];
                    rgba[destinationPixel + 2] = data[sourcePixel + 2];
                    rgba[destinationPixel + 3] = hasAlpha ? data[sourcePixel + 3] : byte.MaxValue;
                }
            }

            return new RawImageData(width, height, rgba);
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex MarkupRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    private EncodedImageData? EncodeHistoryImage(RawImageData? imageData, string iconName)
    {
        try
        {
            if (imageData is not null)
            {
                var encoded = ImageEncoding.EncodePng(imageData);
                return encoded.Bytes.Length <= MAX_IMAGE_BYTES ? encoded : null;
            }

            if (string.IsNullOrWhiteSpace(iconName))
            {
                return null;
            }

            var path = _iconResolver.TryResolveIcon(iconName) ?? _iconResolver.TryResolve(iconName);
            if (path is null)
            {
                return null;
            }

            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0 || file.Length > MAX_IMAGE_BYTES)
            {
                return null;
            }

            var mimeType = global::System.IO.Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                ".xpm" => "image/x-xpixmap",
                _ => "application/octet-stream",
            };
            return new EncodedImageData(mimeType, File.ReadAllBytes(path));
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Notifications", "Could not encode a notification image for history", exception);
            return null;
        }
    }

    private void ApplyHistoryLimit()
    {
        lock (_gate)
        {
            if (_items.Count > _history.NotificationLimit)
            {
                _items.RemoveRange(_history.NotificationLimit, _items.Count - _history.NotificationLimit);
            }
        }
    }

    public void Dispose()
    {
        _history.LimitsChanged -= ApplyHistoryLimit;
        _connection.Dispose();
    }

    private const string INTROSPECTION_XML = """
                                             <!DOCTYPE node PUBLIC "-//freedesktop//DTD D-BUS Object Introspection 1.0//EN" "http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd">
                                             <node name="/org/freedesktop/Notifications">
                                               <interface name="org.freedesktop.Notifications">
                                                 <method name="GetCapabilities"><arg type="as" direction="out"/></method>
                                                 <method name="Notify">
                                                   <arg name="app_name" type="s" direction="in"/><arg name="replaces_id" type="u" direction="in"/>
                                                   <arg name="app_icon" type="s" direction="in"/><arg name="summary" type="s" direction="in"/>
                                                   <arg name="body" type="s" direction="in"/><arg name="actions" type="as" direction="in"/>
                                                   <arg name="hints" type="a{sv}" direction="in"/><arg name="expire_timeout" type="i" direction="in"/>
                                                   <arg name="id" type="u" direction="out"/>
                                                 </method>
                                                 <method name="CloseNotification"><arg name="id" type="u" direction="in"/></method>
                                                 <method name="GetServerInformation"><arg type="s" direction="out"/><arg type="s" direction="out"/><arg type="s" direction="out"/><arg type="s" direction="out"/></method>
                                                 <signal name="NotificationClosed"><arg name="id" type="u"/><arg name="reason" type="u"/></signal>
                                                 <signal name="ActionInvoked"><arg name="id" type="u"/><arg name="action_key" type="s"/></signal>
                                               </interface>
                                               <interface name="org.freedesktop.DBus.Introspectable"><method name="Introspect"><arg type="s" direction="out"/></method></interface>
                                             </node>
                                             """;
    internal static readonly string[] CAPABILITIES_STRING = new[] { "actions", "body", "body-markup", "icon-static", "persistence" };
}
