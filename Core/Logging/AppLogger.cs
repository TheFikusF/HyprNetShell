using System.Globalization;
using System.Text;

namespace HyprNetShell.Core.Logging;

public static class AppLogger
{
    private const long MAX_LOG_BYTES = 5 * 1024 * 1024;
    private const string CONSOLE_LOGGING_VARIABLE = "HYPRNETSHELL_LOG_TO_CONSOLE";
    private static readonly Lock Gate = new();
    private static StreamWriter? _writer;

    public static string LogFilePath { get; private set; } = "";
    public static bool ConsoleLoggingEnabled { get; set; } = true;

    public static void Initialize()
    {
        lock (Gate)
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
                    new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
            }
            catch (Exception exception)
            {
                if (ConsoleLoggingEnabled)
                {
                    Console.Error.WriteLine($"Could not initialize file logging: {exception}");
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

    public static void Info(string category, string message) => Write("INF", category, message, null);

    public static void Warning(string category, string message, Exception? exception = null) =>
        Write("WRN", category, message, exception);

    public static void Error(string category, string message, Exception? exception = null) =>
        Write("ERR", category, message, exception);

    public static void Shutdown()
    {
        Info("Application", "Shutting down");
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static void Write(string level, string category, string message, Exception? exception)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        var line = $"{timestamp} [{level}] [{category}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (Gate)
        {
            try
            {
                if (ConsoleLoggingEnabled)
                {
                    Console.Error.WriteLine(line);
                }
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

    private static bool ReadConsoleLoggingSwitch()
    {
        var value = Environment.GetEnvironmentVariable(CONSOLE_LOGGING_VARIABLE);
        return value is not null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
