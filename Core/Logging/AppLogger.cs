using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace HyprNetShell.Core.Logging;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

public static class AppLogger
{
    private sealed record LogEntry(LogLevel Level,
        string Category, string Message, Exception? Exception,
        string Timestamp, string Label, string Line);

    private const long MAX_LOG_BYTES = 5 * 1024 * 1024;

    private static readonly Lock _lock = new();

    private static StreamWriter? _writer;
    private static Socket? _journalSocket;
    private static bool _initialized;
    private static bool _handlersRegistered;
    private static Action<LogEntry>? _writeLogAction;

    public static string LogFilePath { get; private set; } = "";
    public static bool ConsoleLoggingEnabled { get; set; } = true;
    public static bool JournalLoggingEnabled { get; set; } = true;
    public static bool FileLoggingEnabled { get; set; }

    public static void Initialize()
    {
        lock (_lock)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _writeLogAction = null;
            LogFilePath = "";

            if (ConsoleLoggingEnabled)
            {
                _writeLogAction += WriteConsole;
            }

            if (JournalLoggingEnabled)
            {
                InitializeJournal();
            }

            if (FileLoggingEnabled)
            {
                InitializeFile();
            }
        }

        if (!_handlersRegistered)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Error("Application", "Unhandled exception", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Error("Application", "Unobserved task exception", args.Exception);
                args.SetObserved();
            };
            _handlersRegistered = true;
        }

        Info("Lifecycle", $"========== HyprNetShell | START | pid={Environment.ProcessId} ==========");
        Info("Application", $"Logging initialized{(LogFilePath.Length > 0 ? $" at {LogFilePath}" : "")}");
    }

    private static void InitializeJournal()
    {
        try
        {
            if (File.Exists("/run/systemd/journal/socket"))
            {
                _journalSocket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
                _journalSocket.Connect(new UnixDomainSocketEndPoint("/run/systemd/journal/socket"));
                _journalSocket.Blocking = false;
                _writeLogAction += WriteJournal;
            }
        }
        catch (Exception exception)
        {
            _journalSocket?.Dispose();
            _journalSocket = null;
            Warning("Logging", "Could not connect to journald; continuing with other enabled log outputs", exception);
        }
    }

    private static void InitializeFile()
    {
        try
        {
            var stateDirectory = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (string.IsNullOrWhiteSpace(stateDirectory))
            {
                stateDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local",
                    "state");
            }

            var logDirectory = Path.Combine(stateDirectory, "hyprnetshell");
            Directory.CreateDirectory(logDirectory);
            LogFilePath = Path.Combine(logDirectory, "hyprnetshell.log");
            RotateIfNeeded(LogFilePath);

            _writer = new StreamWriter(
                new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false)) {
                AutoFlush = true,
            };
            _writeLogAction += WriteFile;
        }
        catch (Exception exception)
        {
            Error("Application", "Could not initialize file logging", exception);
        }
    }

    public static void Info(string category, string message) => Write(LogLevel.Info, category, message, null);

    public static void Warning(string category, string message, Exception? exception = null) =>
        Write(LogLevel.Warning, category, message, exception);

    public static void Error(string category, string message, Exception? exception = null) =>
        Write(LogLevel.Error, category, message, exception);

    public static void Shutdown()
    {
        lock (_lock)
        {
            if (!_initialized)
            {
                return;
            }

            Info("Lifecycle", $"========== Bye-bye! | STOP | pid={Environment.ProcessId} ==========");
            _journalSocket?.Dispose();
            _journalSocket = null;
            _writer?.Dispose();
            _writer = null;
            _writeLogAction = null;
            _initialized = false;
        }
    }

    private static void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var label = level switch {
            LogLevel.Info => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => level.ToString(),
        };
        var timestamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var line = $"{timestamp} [{label}] [{category}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_lock)
        {
            _writeLogAction?.Invoke(new LogEntry(level, category, message, exception, timestamp, label, line));
        }
    }

    private static void WriteFile(LogEntry entry)
    {
        try
        {
            _writer?.WriteLine(entry.Line);
        }
        catch
        {
            // Logging must never take down the application.
        }
    }

    private static void WriteConsole(LogEntry entry)
    {
        try
        {
            var output = entry.Level == LogLevel.Info ? Console.Out : Console.Error;
            var redirected = entry.Level == LogLevel.Info
                ? Console.IsOutputRedirected
                : Console.IsErrorRedirected;
            var color = entry.Level switch {
                LogLevel.Info => "\u001b[36m",
                LogLevel.Warning => "\u001b[33m",
                LogLevel.Error => "\u001b[31m",
                _ => "\u001b[0m",
            };
            var tag = $"[{entry.Label}]";
            var text = $"[{entry.Category}] {entry.Message}";
            if (entry.Exception is not null)
            {
                text += Environment.NewLine + entry.Exception;
            }

            output.WriteLine(redirected ? entry.Line : $"{entry.Timestamp} {color}{tag}\u001b[0m {text}");
        }
        catch
        {
            // A console failure must not prevent other log outputs.
        }
    }

    private static void WriteJournal(LogEntry entry)
    {
        var (level, category, message, exception, _, _, _) = entry;
        if (_journalSocket is null)
        {
            return;
        }

        try
        {
            var priority = level switch {
                LogLevel.Error => 3,
                LogLevel.Warning => 4,
                _ => 6,
            };
            var text = $"[{category}] {message}";
            if (exception is not null)
            {
                text += Environment.NewLine + exception;
            }

            // The journal native protocol uses a little-endian length for multiline fields.
            using var payload = new MemoryStream();
            using var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true);
            writer.Write(Encoding.UTF8.GetBytes($"PRIORITY={priority}\nSYSLOG_IDENTIFIER=hyprnetshell\nMESSAGE\n"));
            var messageBytes = Encoding.UTF8.GetBytes(text);
            writer.Write((ulong)messageBytes.Length);
            writer.Write(messageBytes);
            writer.Write((byte)'\n');
            _journalSocket.Send(payload.GetBuffer().AsSpan(0, (int)payload.Length));
        }
        catch (Exception journalException)
        {
            _journalSocket.Dispose();
            _journalSocket = null;
            _writeLogAction -= WriteJournal;
            Warning("Logging", "Could not write to journald; journal logging disabled for this process", journalException);
        }
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MAX_LOG_BYTES)
        {
            return;
        }

        var previousPath = path + ".1";
        File.Move(path, previousPath, overwrite: true);
    }
}
