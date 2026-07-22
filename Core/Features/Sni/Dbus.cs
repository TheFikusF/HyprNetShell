using Tmds.DBus.Protocol;

namespace HyprNetShell.Core.Features.Sni;

internal delegate void MessageBodyWriter(ref MessageWriter writer);

internal static class Dbus
{
    public const string BUS_NAME = "org.freedesktop.DBus";
    public const string BUS_PATH = "/org/freedesktop/DBus";
    public const string BUS_INTERFACE = "org.freedesktop.DBus";

    public static string SessionAddress =>
        Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")
        ?? throw new InvalidOperationException("DBUS_SESSION_BUS_ADDRESS is not set");

    public static MessageBuffer Call(
        DBusConnection connection,
        string destination,
        string path,
        string @interface,
        string member,
        string? signature = null,
        MessageBodyWriter? writeBody = null)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, @interface, member, signature ?? "", MessageFlags.None);
            writeBody?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    public static async Task<T> CallAsync<T>(
        DBusConnection connection,
        string destination,
        string path,
        string @interface,
        string member,
        Func<Reader, T> read,
        string? signature = null,
        MessageBodyWriter? writeBody = null)
    {
        var message = Call(connection, destination, path, @interface, member, signature, writeBody);
        return await connection.CallMethodAsync(message, static (reply, state) =>
        {
            var reader = reply.GetBodyReader();
            return ((Func<Reader, T>)state!)(reader);
        }, read);
    }

    public static Task CallAsync(
        DBusConnection connection,
        string destination,
        string path,
        string @interface,
        string member,
        string? signature = null,
        MessageBodyWriter? writeBody = null) =>
        connection.CallMethodAsync(Call(connection, destination, path, @interface, member, signature, writeBody));

    public static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch
        {
            ObserveCompletion(task);
            throw;
        }
    }

    public static async Task WaitAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(cancellationToken);
        }
        catch
        {
            ObserveCompletion(task);
            throw;
        }
    }

    public static async Task<T> WaitAsync<T>(Task<T> task, TimeSpan timeout)
    {
        try
        {
            return await task.WaitAsync(timeout);
        }
        catch
        {
            ObserveCompletion(task);
            throw;
        }
    }

    private static void ObserveCompletion(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = ObserveCompletionAsync(task);
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The caller already observed the timeout/cancellation. Observe the
            // eventual D-Bus result so a later error is not raised by finalization.
        }
    }

    public static async Task<uint> RequestNameAsync(DBusConnection connection, string name, uint flags) =>
        await CallAsync(
            connection,
            BUS_NAME,
            BUS_PATH,
            BUS_INTERFACE,
            "RequestName",
            reader => reader.ReadUInt32(),
            "su",
            (ref MessageWriter writer) =>
            {
                writer.WriteString(name);
                writer.WriteUInt32(flags);
            });

    public static async Task<string> GetNameOwnerAsync(DBusConnection connection, string name) =>
        await CallAsync(
            connection,
            BUS_NAME,
            BUS_PATH,
            BUS_INTERFACE,
            "GetNameOwner",
            reader => reader.ReadString(),
            "s",
            (ref MessageWriter writer) => writer.WriteString(name));

    public static VariantValue Unwrap(this VariantValue value) =>
        value.Type == VariantValueType.Variant ? value.GetVariantValue() : value;

    public static string StringValue(this IReadOnlyDictionary<string, VariantValue> values, string key) =>
        values.TryGetValue(key, out var value) && value.Unwrap().Type == VariantValueType.String
            ? value.Unwrap().GetString()
            : "";

    public static string ObjectPathValue(this IReadOnlyDictionary<string, VariantValue> values, string key) =>
        values.TryGetValue(key, out var value) && value.Unwrap().Type == VariantValueType.ObjectPath
            ? value.Unwrap().GetObjectPathAsString()
            : "";
}
