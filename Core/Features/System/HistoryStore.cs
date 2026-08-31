using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.Rendering;
using Microsoft.Data.Sqlite;

namespace HyprNetShell.Core.Features.System;

internal sealed class HistoryStore : IDisposable
{
    internal const int DefaultLimit = 200;
    internal const int MinimumLimit = 25;
    internal const int MaximumLimit = 2000;
    internal const int LimitStep = 25;

    private const long MaximumNotificationImageBytes = 256L * 1024 * 1024;
    private const long MaximumClipboardBytes = 256L * 1024 * 1024;
    private const string NotificationLimitKey = "notification_limit";
    private const string ClipboardLimitKey = "clipboard_limit";

    private readonly Lock _gate = new();
    private readonly string _connectionString;
    private bool _available;
    private bool _disposed;

    public int NotificationLimit { get; private set; } = DefaultLimit;
    public int ClipboardLimit { get; private set; } = DefaultLimit;
    public event Action? LimitsChanged;

    public HistoryStore()
    {
        var databasePath = GetDatabasePath();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        try
        {
            var databaseDirectory = Path.GetDirectoryName(databasePath)!;
            Directory.CreateDirectory(databaseDirectory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    databaseDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            lock (_gate)
            {
                using var connection = OpenConnection();
                InitializeSchema(connection);
                NotificationLimit = ReadLimit(connection, NotificationLimitKey);
                ClipboardLimit = ReadLimit(connection, ClipboardLimitKey);
                PruneNotifications(connection);
                PruneClipboard(connection);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(databasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                _available = true;
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("History", "Could not initialize the local history database; using in-memory history", exception);
        }
    }

    public IReadOnlyList<StoredNotification> LoadNotifications()
    {
        if (!_available)
        {
            return [];
        }

        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT runtime_id, title, body, app_name, desktop_entry, icon_name,
                           image_mime_type, image_data, resident, received_at
                    FROM notifications
                    ORDER BY received_at DESC, id DESC
                    LIMIT $limit
                    """;
                command.Parameters.AddWithValue("$limit", NotificationLimit);
                using var reader = command.ExecuteReader();
                var items = new List<StoredNotification>();
                while (reader.Read())
                {
                    EncodedImageData? image = null;
                    if (!reader.IsDBNull(6) && !reader.IsDBNull(7))
                    {
                        image = new EncodedImageData(reader.GetString(6), (byte[])reader[7]);
                    }

                    items.Add(new StoredNotification(
                        checked((uint)reader.GetInt64(0)),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        image,
                        reader.GetBoolean(8),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)).UtcDateTime));
                }

                return items;
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("History", "Could not load notification history", exception);
            return [];
        }
    }

    public void SaveNotification(NotificationSnapshot notification, EncodedImageData? image)
    {
        if (!_available)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM notifications WHERE runtime_id = $runtime_id";
                    delete.Parameters.AddWithValue("$runtime_id", notification.Id);
                    delete.ExecuteNonQuery();
                }

                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO notifications (
                            runtime_id, title, body, app_name, desktop_entry, icon_name,
                            image_mime_type, image_data, resident, received_at)
                        VALUES (
                            $runtime_id, $title, $body, $app_name, $desktop_entry, $icon_name,
                            $image_mime_type, $image_data, $resident, $received_at)
                        """;
                    insert.Parameters.AddWithValue("$runtime_id", notification.Id);
                    insert.Parameters.AddWithValue("$title", notification.Title);
                    insert.Parameters.AddWithValue("$body", notification.Body);
                    insert.Parameters.AddWithValue("$app_name", notification.AppName);
                    insert.Parameters.AddWithValue("$desktop_entry", notification.DesktopEntry);
                    insert.Parameters.AddWithValue("$icon_name", notification.IconName);
                    insert.Parameters.AddWithValue("$image_mime_type", (object?)image?.MimeType ?? DBNull.Value);
                    var imageParameter = insert.Parameters.Add("$image_data", SqliteType.Blob);
                    imageParameter.Value = image is null ? DBNull.Value : image.Bytes.ToArray();
                    insert.Parameters.AddWithValue("$resident", notification.Resident);
                    insert.Parameters.AddWithValue(
                        "$received_at",
                        new DateTimeOffset(notification.ReceivedAt).ToUnixTimeMilliseconds());
                    insert.ExecuteNonQuery();
                }

                PruneNotifications(connection, transaction);
                transaction.Commit();
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("History", "Could not save notification history", exception);
        }
    }

    public void DeleteNotification(uint runtimeId) => Execute(
        "DELETE FROM notifications WHERE runtime_id = $id",
        command => command.Parameters.AddWithValue("$id", runtimeId),
        "Could not delete a notification from history");

    public void ClearNotifications() => Execute(
        "DELETE FROM notifications",
        null,
        "Could not clear notification history");

    public IReadOnlyList<ClipboardHistoryEntry> LoadClipboardEntries()
    {
        if (!_available)
        {
            return [];
        }

        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, mime_type, data, preview, content_hash, is_pinned, captured_at
                    FROM clipboard_entries
                    ORDER BY is_pinned DESC, captured_at DESC, id DESC
                    LIMIT $limit
                    """;
                command.Parameters.AddWithValue("$limit", ClipboardLimit);
                using var reader = command.ExecuteReader();
                var items = new List<ClipboardHistoryEntry>();
                while (reader.Read())
                {
                    var mimeType = reader.GetString(1);
                    var data = (byte[])reader[2];
                    items.Add(new ClipboardHistoryEntry(
                        reader.GetInt64(0),
                        mimeType,
                        data,
                        reader.GetString(3),
                        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                            ? new EncodedImageData(mimeType, data)
                            : null,
                        reader.GetString(4),
                        reader.GetBoolean(5),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)).UtcDateTime));
                }

                return items;
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("History", "Could not load clipboard history", exception);
            return [];
        }
    }

    public void DeleteClipboardEntry(string mimeType, string hash) => Execute(
        "DELETE FROM clipboard_entries WHERE mime_type = $mime_type AND content_hash = $content_hash",
        command =>
        {
            command.Parameters.AddWithValue("$mime_type", mimeType);
            command.Parameters.AddWithValue("$content_hash", hash);
        },
        "Could not delete a clipboard entry");

    public void SetClipboardPinned(string mimeType, string hash, bool isPinned) => Execute(
        """
        UPDATE clipboard_entries
        SET is_pinned = $is_pinned
        WHERE mime_type = $mime_type AND content_hash = $content_hash
        """,
        command =>
        {
            command.Parameters.AddWithValue("$is_pinned", isPinned);
            command.Parameters.AddWithValue("$mime_type", mimeType);
            command.Parameters.AddWithValue("$content_hash", hash);
        },
        "Could not update a clipboard pin");

    public void SaveClipboardEntry(ClipboardHistoryEntry entry)
    {
        if (!_available)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO clipboard_entries (
                            mime_type, data, preview, content_hash, is_pinned, captured_at)
                        VALUES ($mime_type, $data, $preview, $content_hash, $is_pinned, $captured_at)
                        ON CONFLICT(mime_type, content_hash) DO UPDATE SET
                            data = excluded.data,
                            preview = excluded.preview,
                            captured_at = excluded.captured_at
                        """;
                    command.Parameters.AddWithValue("$mime_type", entry.MimeType);
                    command.Parameters.Add("$data", SqliteType.Blob).Value = entry.Data;
                    command.Parameters.AddWithValue("$preview", entry.Preview);
                    command.Parameters.AddWithValue("$content_hash", entry.Hash);
                    command.Parameters.AddWithValue("$is_pinned", entry.IsPinned);
                    command.Parameters.AddWithValue(
                        "$captured_at",
                        new DateTimeOffset(entry.CapturedAt).ToUnixTimeMilliseconds());
                    command.ExecuteNonQuery();
                }

                PruneClipboard(connection, transaction);
                transaction.Commit();
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("History", "Could not save clipboard history", exception);
        }
    }

    public void SetNotificationLimit(int value)
    {
        value = NormalizeLimit(value);
        if (value == NotificationLimit)
        {
            return;
        }

        NotificationLimit = value;
        SaveLimit(NotificationLimitKey, value, PruneNotifications);
    }

    public void SetClipboardLimit(int value)
    {
        value = NormalizeLimit(value);
        if (value == ClipboardLimit)
        {
            return;
        }

        ClipboardLimit = value;
        SaveLimit(ClipboardLimitKey, value, PruneClipboard);
    }

    private void SaveLimit(string key, int value, Action<SqliteConnection, SqliteTransaction?> prune)
    {
        if (_available)
        {
            try
            {
                lock (_gate)
                {
                    using var connection = OpenConnection();
                    using var transaction = connection.BeginTransaction();
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO settings (key, value) VALUES ($key, $value)
                        ON CONFLICT(key) DO UPDATE SET value = excluded.value
                        """;
                    command.Parameters.AddWithValue("$key", key);
                    command.Parameters.AddWithValue("$value", value.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
                    command.ExecuteNonQuery();
                    prune(connection, transaction);
                    transaction.Commit();
                }
            }
            catch (Exception exception)
            {
                AppLogger.Warning("History", "Could not save a history limit", exception);
            }
        }

        LimitsChanged?.Invoke();
    }

    private static int NormalizeLimit(int value) => Math.Clamp(value, MinimumLimit, MaximumLimit);

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString) { DefaultTimeout = 5 };
        connection.Open();
        return connection;
    }

    private static void InitializeSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS notifications (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                runtime_id INTEGER NOT NULL,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                app_name TEXT NOT NULL,
                desktop_entry TEXT NOT NULL,
                icon_name TEXT NOT NULL,
                image_mime_type TEXT NULL,
                image_data BLOB NULL,
                resident INTEGER NOT NULL,
                received_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_notifications_received_at
                ON notifications(received_at DESC);

            CREATE TABLE IF NOT EXISTS clipboard_entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                mime_type TEXT NOT NULL COLLATE NOCASE,
                data BLOB NOT NULL,
                preview TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                is_pinned INTEGER NOT NULL DEFAULT 0,
                captured_at INTEGER NOT NULL,
                UNIQUE(mime_type, content_hash)
            );
            CREATE INDEX IF NOT EXISTS ix_clipboard_order
                ON clipboard_entries(is_pinned DESC, captured_at DESC);
            """;
        command.ExecuteNonQuery();
    }

    private static int ReadLimit(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        var value = command.ExecuteScalar() as string;
        return int.TryParse(value, out var parsed) ? NormalizeLimit(parsed) : DefaultLimit;
    }

    private void PruneNotifications(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM notifications
            WHERE id NOT IN (
                SELECT id FROM notifications
                ORDER BY received_at DESC, id DESC
                LIMIT $limit)
            """;
        command.Parameters.AddWithValue("$limit", NotificationLimit);
        command.ExecuteNonQuery();

        using var sizeCommand = connection.CreateCommand();
        sizeCommand.Transaction = transaction;
        sizeCommand.CommandText = """
            DELETE FROM notifications
            WHERE id IN (
                SELECT id
                FROM (
                    SELECT id,
                           SUM(COALESCE(length(image_data), 0)) OVER (
                               ORDER BY received_at DESC, id DESC
                               ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_bytes
                    FROM notifications)
                WHERE running_bytes > $maximum_bytes)
            """;
        sizeCommand.Parameters.AddWithValue("$maximum_bytes", MaximumNotificationImageBytes);
        sizeCommand.ExecuteNonQuery();
    }

    private void PruneClipboard(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM clipboard_entries
            WHERE id NOT IN (
                SELECT id FROM clipboard_entries
                ORDER BY is_pinned DESC, captured_at DESC, id DESC
                LIMIT $limit)
            """;
        command.Parameters.AddWithValue("$limit", ClipboardLimit);
        command.ExecuteNonQuery();

        using var sizeCommand = connection.CreateCommand();
        sizeCommand.Transaction = transaction;
        sizeCommand.CommandText = """
            DELETE FROM clipboard_entries
            WHERE id IN (
                SELECT id
                FROM (
                    SELECT id,
                           SUM(length(data)) OVER (
                               ORDER BY is_pinned DESC, captured_at DESC, id DESC
                               ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_bytes
                    FROM clipboard_entries)
                WHERE running_bytes > $maximum_bytes)
            """;
        sizeCommand.Parameters.AddWithValue("$maximum_bytes", MaximumClipboardBytes);
        sizeCommand.ExecuteNonQuery();
    }

    private void Execute(string sql, Action<SqliteCommand>? configure, string failureMessage)
    {
        if (!_available)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                configure?.Invoke(command);
                command.ExecuteNonQuery();
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("History", failureMessage, exception);
        }
    }

    private static string GetDatabasePath()
    {
        var dataRoot = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        return Path.Combine(dataRoot, "hyprnetshell", "history.db");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}

internal sealed record StoredNotification(
    uint RuntimeId,
    string Title,
    string Body,
    string AppName,
    string DesktopEntry,
    string IconName,
    EncodedImageData? Image,
    bool Resident,
    DateTime ReceivedAt);
