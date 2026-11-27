namespace HyprNetShell.Core.Models;

internal enum DriveHealth
{
    Unavailable,
    Passed,
    Failing,
    Standby,
}

internal sealed record DriveDiagnostics(
    DriveHealth Health,
    decimal? LifetimeReadBytes = null,
    decimal? LifetimeWrittenBytes = null,
    ulong? WearPercent = null,
    ulong? MediaErrors = null);

internal sealed record DriveHealthSnapshot(
    string DevicePath,
    DriveDiagnostics Diagnostics,
    decimal? SessionReadBytes,
    decimal? SessionWrittenBytes);
