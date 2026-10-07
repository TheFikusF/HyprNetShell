using System.Globalization;
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
    private const long MAX_LOG_BYTES = 5 * 1024 * 1024;
    private static readonly Lock _lock = new();
    private static StreamWriter? _writer;

    public static string LogFilePath { get; private set; } = "";
    public static bool ConsoleLoggingEnabled { get; set; } = true;

    public static void Initialize()
    {
        lock (_lock)
        {
            if (_writer is not null)
            {
                return;
            }

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
            }
            catch (Exception exception)
            {
                if (ConsoleLoggingEnabled)
                {
                    Error("Application", "Could not initialize file logging", exception);
                }
            }
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Error("Application", "Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Error("Application", "Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        Info("Application", $"Logging initialized{(LogFilePath.Length > 0 ? $" at {LogFilePath}" : "")}");
    }

    public static void Info(string category, string message) => Write(LogLevel.Info, category, message, null);

    public static void Warning(string category, string message, Exception? exception = null) =>
        Write(LogLevel.Warning, category, message, exception);

    public static void Error(string category, string message, Exception? exception = null) =>
        Write(LogLevel.Error, category, message, exception);

    public static void Shutdown()
    {
        Info("Application", "Shutting down");
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
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
            try
            {
                if (ConsoleLoggingEnabled)
                {
                    var output = level == LogLevel.Info ? Console.Out : Console.Error;
                    var redirected = level == LogLevel.Info
                        ? Console.IsOutputRedirected
                        : Console.IsErrorRedirected;
                    var color = level switch {
                        LogLevel.Info => "\u001b[36m",
                        LogLevel.Warning => "\u001b[33m",
                        LogLevel.Error => "\u001b[31m",
                        _ => "\u001b[0m",
                    };
                    output.WriteLine(redirected ? line : $"{color}{line}\u001b[0m");
                }
            }
            catch
            {
                // A console failure must not prevent file logging.
            }

            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // Logging must never take down the application.
            }
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
