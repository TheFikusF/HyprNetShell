namespace HyprNetShell.Core.Bar.Common;

public sealed class PopupCoordinator
{
    private readonly Dictionary<string, DateTime> _cantOpenBefore = [];
    private string? _lastOpenedId;
    private string? _pendingOpenedId;

    public void Register(string moduleId) =>
        _cantOpenBefore[moduleId] = DateTime.Now + TimeSpan.FromMilliseconds(200);

    public bool IsOpen(string moduleId) => _lastOpenedId == moduleId;

    public bool TryRequestOpen(string moduleId)
    {
        if (!_cantOpenBefore.TryGetValue(moduleId, out var cantOpenBefore))
        {
            Register(moduleId);
            return false;
        }

        if (cantOpenBefore >= DateTime.Now
            || (_lastOpenedId == moduleId && !string.IsNullOrEmpty(_pendingOpenedId)))
        {
            return false;
        }

        _pendingOpenedId = moduleId;
        return true;
    }

    public void EndFrame()
    {
        if (_pendingOpenedId != _lastOpenedId && _lastOpenedId is { } lastOpenedId)
        {
            _cantOpenBefore[lastOpenedId] = DateTime.Now + TimeSpan.FromMilliseconds(200);
        }

        _lastOpenedId = _pendingOpenedId;
        _pendingOpenedId = null;
    }
}
