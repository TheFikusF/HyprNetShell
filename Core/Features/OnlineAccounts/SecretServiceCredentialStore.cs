using System.Text;
using HyprNetShell.Core.Features.Sni;
using Tmds.DBus.Protocol;

namespace HyprNetShell.Core.Features.OnlineAccounts;

internal interface ICredentialStore
{
    Task<string?> ReadAsync(string provider, CancellationToken cancellationToken);
    Task WriteAsync(string provider, string secret, CancellationToken cancellationToken);
    Task DeleteAsync(string provider, CancellationToken cancellationToken);
}

internal sealed class SecretServiceCredentialStore : ICredentialStore
{
    private const string SERVICE_NAME = "org.freedesktop.secrets";
    private const string SERVICE_PATH = "/org/freedesktop/secrets";
    private const string SERVICE_INTERFACE = "org.freedesktop.Secret.Service";
    private const string COLLECTION_INTERFACE = "org.freedesktop.Secret.Collection";
    private const string ITEM_INTERFACE = "org.freedesktop.Secret.Item";
    private const string PROMPT_INTERFACE = "org.freedesktop.Secret.Prompt";
    private const string DEFAULT_COLLECTION_PATH = "/org/freedesktop/secrets/aliases/default";
    private const string NO_OBJECT_PATH = "/";
    private const string APPLICATION_ATTRIBUTE = "application";
    private const string PROVIDER_ATTRIBUTE = "provider";
    private const string APPLICATION_NAME = "hyprnetshell";

    public async Task<string?> ReadAsync(string provider, CancellationToken cancellationToken)
    {
        using var connection = await ConnectAsync(cancellationToken);
        var sessionPath = await OpenSessionAsync(connection, cancellationToken);
        var (unlocked, locked) = await SearchAsync(connection, provider, cancellationToken);
        if (unlocked.Length == 0 && locked.Length > 0)
        {
            await UnlockAsync(connection, locked, cancellationToken);
            (unlocked, locked) = await SearchAsync(connection, provider, cancellationToken);
        }

        if (unlocked.Length == 0)
        {
            if (locked.Length > 0)
            {
                throw new InvalidOperationException("The desktop keyring remains locked.");
            }

            return null;
        }

        var secretBytes = await Dbus.WaitAsync(
            Dbus.CallAsync(
                connection,
                SERVICE_NAME,
                unlocked[0].ToString(),
                ITEM_INTERFACE,
                "GetSecret",
                static reader =>
                {
                    reader.AlignStruct();
                    _ = reader.ReadObjectPath();
                    _ = reader.ReadArrayOfByte();
                    var value = reader.ReadArrayOfByte();
                    _ = reader.ReadString();
                    return value;
                },
                "o",
                (ref MessageWriter writer) => writer.WriteObjectPath(sessionPath)),
            cancellationToken);

        try
        {
            return Encoding.UTF8.GetString(secretBytes);
        }
        finally
        {
            Array.Clear(secretBytes);
        }
    }

    public async Task WriteAsync(string provider, string secret, CancellationToken cancellationToken)
    {
        using var connection = await ConnectAsync(cancellationToken);
        var sessionPath = await OpenSessionAsync(connection, cancellationToken);
        var value = Encoding.UTF8.GetBytes(secret);
        try
        {
            var promptPath = await Dbus.WaitAsync(
                Dbus.CallAsync(
                    connection,
                    SERVICE_NAME,
                    DEFAULT_COLLECTION_PATH,
                    COLLECTION_INTERFACE,
                    "CreateItem",
                    static reader =>
                    {
                        _ = reader.ReadObjectPath();
                        return reader.ReadObjectPathAsString();
                    },
                    "a{sv}(oayays)b",
                    (ref MessageWriter writer) =>
                    {
                        WriteProperties(ref writer, provider);
                        writer.WriteStructureStart();
                        writer.WriteObjectPath(sessionPath);
                        writer.WriteArray(Array.Empty<byte>());
                        writer.WriteArray(value);
                        writer.WriteString("application/json; charset=utf-8");
                        writer.WriteBool(true);
                    }),
                cancellationToken);

            EnsureNoPrompt(promptPath, "The desktop keyring must be unlocked before credentials can be saved.");
        }
        finally
        {
            Array.Clear(value);
        }
    }

    public async Task DeleteAsync(string provider, CancellationToken cancellationToken)
    {
        using var connection = await ConnectAsync(cancellationToken);
        var (unlocked, locked) = await SearchAsync(connection, provider, cancellationToken);
        if (locked.Length > 0)
        {
            throw new InvalidOperationException("The desktop keyring is locked. Unlock it and try again.");
        }

        foreach (var itemPath in unlocked)
        {
            var promptPath = await Dbus.WaitAsync(
                Dbus.CallAsync(
                    connection,
                    SERVICE_NAME,
                    itemPath.ToString(),
                    ITEM_INTERFACE,
                    "Delete",
                    static reader => reader.ReadObjectPathAsString()),
                cancellationToken);
            EnsureNoPrompt(promptPath, "The desktop keyring requires confirmation before credentials can be removed.");
        }
    }

    private static async Task<DBusConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var connection = new DBusConnection(Dbus.SessionAddress);
        try
        {
            await Dbus.WaitAsync(connection.ConnectAsync().AsTask(), cancellationToken);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static Task<string> OpenSessionAsync(DBusConnection connection, CancellationToken cancellationToken) =>
        Dbus.WaitAsync(
            Dbus.CallAsync(
                connection,
                SERVICE_NAME,
                SERVICE_PATH,
                SERVICE_INTERFACE,
                "OpenSession",
                static reader =>
                {
                    _ = reader.ReadVariantValue();
                    return reader.ReadObjectPathAsString();
                },
                "sv",
                static (ref MessageWriter writer) =>
                {
                    writer.WriteString("plain");
                    writer.WriteVariantString("");
                }),
            cancellationToken);

    private static Task<(ObjectPath[] Unlocked, ObjectPath[] Locked)> SearchAsync(
        DBusConnection connection,
        string provider,
        CancellationToken cancellationToken) =>
        Dbus.WaitAsync(
            Dbus.CallAsync(
                connection,
                SERVICE_NAME,
                SERVICE_PATH,
                SERVICE_INTERFACE,
                "SearchItems",
                static reader => (reader.ReadArrayOfObjectPath(), reader.ReadArrayOfObjectPath()),
                "a{ss}",
                (ref MessageWriter writer) => WriteAttributes(ref writer, provider)),
            cancellationToken);

    private static async Task UnlockAsync(
        DBusConnection connection,
        ObjectPath[] locked,
        CancellationToken cancellationToken)
    {
        var (_, promptPath) = await Dbus.WaitAsync(
            Dbus.CallAsync(
                connection,
                SERVICE_NAME,
                SERVICE_PATH,
                SERVICE_INTERFACE,
                "Unlock",
                static reader => (reader.ReadArrayOfObjectPath(), reader.ReadObjectPathAsString()),
                "ao",
                (ref MessageWriter writer) => writer.WriteArray(locked)),
            cancellationToken);
        if (string.Equals(promptPath, NO_OBJECT_PATH, StringComparison.Ordinal))
        {
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await connection.AddMatchAsync(
            new MatchRule {
                Type = MessageType.Signal,
                Path = promptPath,
                Interface = PROMPT_INTERFACE,
                Member = "Completed",
            },
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                var dismissed = reader.ReadBool();
                _ = reader.ReadVariantValue();
                return !dismissed;
            },
            static notification =>
            {
                var source = (TaskCompletionSource<bool>)notification.State!;
                if (notification.HasValue)
                {
                    source.TrySetResult(notification.Value);
                }
                else
                {
                    source.TrySetException(new InvalidOperationException(
                        "The Secret Service connection closed while waiting for the keyring prompt."));
                }
            },
            false,
            Dbus.CONNECTION_FAILURE_OBSERVER_FLAGS,
            completion);

        await Dbus.WaitAsync(
            Dbus.CallAsync(
                connection,
                SERVICE_NAME,
                promptPath,
                PROMPT_INTERFACE,
                "Prompt",
                "s",
                static (ref MessageWriter writer) => writer.WriteString("")),
            cancellationToken);

        if (!await completion.Task.WaitAsync(cancellationToken))
        {
            throw new InvalidOperationException("The desktop keyring unlock prompt was dismissed.");
        }
    }

    private static void WriteProperties(ref MessageWriter writer, string provider)
    {
        var properties = writer.WriteDictionaryStart();

        writer.WriteDictionaryEntryStart();
        writer.WriteString("org.freedesktop.Secret.Item.Label");
        writer.WriteVariantString($"HyprNetShell {provider} account");

        writer.WriteDictionaryEntryStart();
        writer.WriteString("org.freedesktop.Secret.Item.Attributes");
        writer.WriteSignature("a{ss}");
        WriteAttributes(ref writer, provider);

        writer.WriteDictionaryEnd(properties);
    }

    private static void WriteAttributes(ref MessageWriter writer, string provider)
    {
        var attributes = writer.WriteDictionaryStart();
        WriteAttribute(ref writer, APPLICATION_ATTRIBUTE, APPLICATION_NAME);
        WriteAttribute(ref writer, PROVIDER_ATTRIBUTE, provider);
        writer.WriteDictionaryEnd(attributes);
    }

    private static void WriteAttribute(ref MessageWriter writer, string key, string value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteString(value);
    }

    private static void EnsureNoPrompt(string promptPath, string message)
    {
        if (!string.Equals(promptPath, NO_OBJECT_PATH, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(message);
        }
    }
}
