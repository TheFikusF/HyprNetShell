namespace HyprNetShell.Core.Services;

public interface IBarDataService
{
    ValueTask UpdateAsync(BarStateBuilder state, CancellationToken cancellationToken);
}
