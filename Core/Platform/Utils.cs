using System.Diagnostics;

namespace HyprNetShell.Core.Platform;

public static class Utils
{
    public static void CopyToClipboard(string text)
    {
        Task.Run(() =>
        {
            if (!TryCopyWith("wl-copy", [], text))
            {
                TryCopyWith("xclip", ["-selection", "clipboard"], text);
            }
        });
    }
    
    private static bool TryCopyWith(string fileName, IEnumerable<string> arguments, string text)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StandardInput.Write(text);
            process.StandardInput.Close();
            process.WaitForExit(800);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}