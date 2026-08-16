namespace HyprNetShell.Core.Services;

public interface IBarDataService
{
    ValueTask RefreshAsync(CancellationToken cancellationToken);
}
