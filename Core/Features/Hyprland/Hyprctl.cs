using System.Diagnostics;
using System.Globalization;
using System.Text;
using HyprNetShell.Core.Logging;

namespace HyprNetShell.Core.Features.Hyprland;

internal sealed class Hyprctl : IHyprctl
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private readonly HyprlandBindingManager _bindings;

    public Hyprctl()
    {
        _bindings = new HyprlandBindingManager(BindCommandAsync, UnbindAsync);
    }

    public Task<bool> LaunchDesktopEntryAsync(
        string desktopFile,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(
            $"hl.dsp.exec_cmd({LuaString($"gio launch {ShellQuote(desktopFile)}")})",
            $"launch desktop entry {desktopFile}",
            cancellationToken);

    public Task<bool> FocusWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default) =>
        DispatchAsync(
            $"hl.dsp.focus({{ workspace = {workspaceId} }})",
            $"focus workspace {workspaceId}",
            cancellationToken);

    public Task<bool> FocusWindowAsync(string windowAddress, CancellationToken cancellationToken = default) =>
        DispatchAsync(
            $"hl.dsp.focus({{ window = {LuaString($"address:{windowAddress}")} }})",
            $"focus window {windowAddress}",
            cancellationToken);

    public Task<bool> Bind(
        string keys,
        Action callback,
        HyprlandBindOptions options = default,
        CancellationToken cancellationToken = default) =>
        _bindings.BindAsync(keys, callback, options, cancellationToken);

    public void Dispose() => _bindings.Dispose();

    private Task<bool> BindCommandAsync(
        string keys,
        string command,
        HyprlandBindOptions options = default,
        CancellationToken cancellationToken = default)
    {
        var flags = new List<string>(2);
        if (options.Release)
        {
            flags.Add("release = true");
        }
        if (options.Transparent)
        {
            flags.Add("transparent = true");
        }

        var flagsExpression = flags.Count == 0 ? "" : $", {{ {string.Join(", ", flags)} }}";
        return EvalAsync(
            $"hl.bind({LuaString(keys)}, hl.dsp.exec_cmd({LuaString(command)}){flagsExpression})",
            $"bind {keys}",
            cancellationToken);
    }

    private Task<bool> UnbindAsync(string keys, CancellationToken cancellationToken = default) =>
        EvalAsync($"hl.unbind({LuaString(keys)})", $"unbind {keys}", cancellationToken);

    public Task<bool> SwitchKeyboardLayoutAsync(
        string keyboardName,
        int? layoutIndex = null,
        CancellationToken cancellationToken = default) =>
        RunForSuccessAsync(
            ["switchxkblayout", keyboardName, layoutIndex?.ToString(CultureInfo.InvariantCulture) ?? "next"],
            $"switch keyboard layout for {keyboardName}",
            cancellationToken);

    public async Task<bool> SetWallpaperAsync(string path, CancellationToken cancellationToken = default)
    {
        if (await RunForSuccessAsync(
                ["hyprpaper", "wallpaper", $", {path}, cover"],
                $"set wallpaper {path}",
                cancellationToken,
                logFailure: false))
        {
            return true;
        }

        return await RunForSuccessAsync(
            ["hyprpaper", "reload", $",{path}"],
            $"reload wallpaper {path}",
            cancellationToken);
    }

    public async Task<int?> GetColorTemperatureAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            ["hyprsunset", "temperature"],
            "read color temperature",
            cancellationToken,
            logFailure: false);
        var token = result.StandardOutput
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return result.Success &&
               double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature)
            ? (int)Math.Round(temperature)
            : null;
    }

    public Task<bool> SetColorTemperatureAsync(
        int temperatureKelvin,
        CancellationToken cancellationToken = default) =>
        RunForSuccessAsync(
            ["hyprsunset", "temperature", temperatureKelvin.ToString(CultureInfo.InvariantCulture)],
            $"set color temperature to {temperatureKelvin}K",
            cancellationToken);

    private Task<bool> DispatchAsync(
        string dispatcherExpression,
        string operation,
        CancellationToken cancellationToken) =>
        EvalAsync($"hl.dispatch({dispatcherExpression})", operation, cancellationToken);

    private async Task<bool> EvalAsync(
        string luaExpression,
        string operation,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(["eval", luaExpression], operation, cancellationToken);
        return result.Success;
    }

    private async Task<bool> RunForSuccessAsync(
        IReadOnlyList<string> arguments,
        string operation,
        CancellationToken cancellationToken,
        bool logFailure = true)
    {
        var result = await RunAsync(arguments, operation, cancellationToken, logFailure);
        return result.Success;
    }

    private static async Task<HyprctlResult> RunAsync(
        IReadOnlyList<string> arguments,
        string operation,
        CancellationToken cancellationToken,
        bool logFailure = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "hyprctl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo);
            if (process is null)
            {
                AppLogger.Error("Hyprctl", $"Could not start hyprctl while trying to {operation}");
                return HyprctlResult.Failed("Process did not start");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var result = new HyprctlResult(
                process.ExitCode,
                (await stdoutTask).Trim(),
                (await stderrTask).Trim());

            if (!result.Success && logFailure)
            {
                AppLogger.Error(
                    "Hyprctl",
                    $"Failed to {operation}: exit={result.ExitCode}; stdout={result.StandardOutput}; stderr={result.StandardError}");
            }

            return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            AppLogger.Error("Hyprctl", $"Timed out after {Timeout.TotalSeconds:0.#} seconds while trying to {operation}", exception);
            return HyprctlResult.Failed("Timed out");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return HyprctlResult.Failed("Cancelled");
        }
        catch (Exception exception)
        {
            TryKill(process);
            AppLogger.Error("Hyprctl", $"Exception while trying to {operation}", exception);
            return HyprctlResult.Failed(exception.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string LuaString(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\': result.Append("\\\\"); break;
                case '"': result.Append("\\\""); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (character is < ' ' or '\u007f')
                    {
                        result.Append($"\\x{(int)character:x2}");
                    }
                    else
                    {
                        result.Append(character);
                    }
                    break;
            }
        }

        return result.Append('"').ToString();
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static void TryKill(Process? process)
    {
        try
        {
            process?.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Hyprctl", "Could not clean up the hyprctl process", exception);
        }
    }

    private readonly record struct HyprctlResult(int? ExitCode, string StandardOutput, string StandardError)
    {
        public bool Success => ExitCode == 0;
        public static HyprctlResult Failed(string error) => new(null, "", error);
    }
}
