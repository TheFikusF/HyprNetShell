using System.Collections.ObjectModel;
using System.Text;

namespace HyprNetShell;

public enum SessionLockState
{
    Acquiring,
    Locked,
    Finished,
    Unlocked,
    Error,
}

public enum SessionLockAuthenticationState
{
    Idle,
    Pending,
    Success,
    Denied,
    Error,
}

public sealed class SessionLock : IDisposable
{
    private readonly Dictionary<ulong, Surface> _surfacesById = [];
    private readonly List<Surface> _surfaces = [];
    private readonly ReadOnlyCollection<Surface> _readOnlySurfaces;
    private IntPtr _lock;
    private ulong _topologySerial;
    private bool _hasTopologySerial;

    public SessionLock(string pamService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pamService);
        _readOnlySurfaces = _surfaces.AsReadOnly();
        _lock = NativeMethods.hypr_lock_create(pamService);
        if (_lock == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Failed to request the Wayland session lock. See native error output above.");
        }
    }

    public IReadOnlyList<Surface> Surfaces => _readOnlySurfaces;
    public SessionLockState State => _lock == IntPtr.Zero
        ? SessionLockState.Error
        : (SessionLockState)NativeMethods.hypr_lock_get_state(_lock);
    public SessionLockAuthenticationState AuthenticationState => _lock == IntPtr.Zero
        ? SessionLockAuthenticationState.Error
        : (SessionLockAuthenticationState)NativeMethods.hypr_lock_get_auth_state(_lock);
    public int PasswordLength => _lock == IntPtr.Zero
        ? 0
        : Math.Max(0, NativeMethods.hypr_lock_get_password_length(_lock));
    public bool HasError => _lock == IntPtr.Zero || NativeMethods.hypr_lock_has_error(_lock) != 0;

    public sealed class Surface(ulong id)
    {
        public ulong Id { get; } = id;
        public string Name { get; internal set; } = "";
        public int Width { get; internal set; }
        public int Height { get; internal set; }
    }

    public bool Update()
    {
        if (_lock == IntPtr.Zero || NativeMethods.hypr_lock_poll_events(_lock) == 0)
        {
            return false;
        }

        ReconcileSurfaces();
        return State is not (SessionLockState.Finished or SessionLockState.Unlocked or SessionLockState.Error);
    }

    public bool MakeCurrent(ulong surfaceId) =>
        _lock != IntPtr.Zero && NativeMethods.hypr_lock_make_current(_lock, surfaceId) != 0;

    public bool SwapBuffers(ulong surfaceId) =>
        _lock != IntPtr.Zero && NativeMethods.hypr_lock_swap_buffers(_lock, surfaceId) != 0;

    public bool Unlock() =>
        _lock != IntPtr.Zero && NativeMethods.hypr_lock_unlock(_lock) != 0;

    public void Dispose()
    {
        if (_lock != IntPtr.Zero)
        {
            NativeMethods.hypr_lock_destroy(_lock);
            _lock = IntPtr.Zero;
        }
        _surfaces.Clear();
        _surfacesById.Clear();
    }

    private void ReconcileSurfaces()
    {
        var serial = NativeMethods.hypr_lock_get_topology_serial(_lock);
        if (_hasTopologySerial && serial == _topologySerial)
        {
            return;
        }
        _hasTopologySerial = true;
        _topologySerial = serial;

        var currentIds = new HashSet<ulong>();
        var reconciled = new List<Surface>();
        var count = Math.Max(0, NativeMethods.hypr_lock_get_surface_count(_lock));
        for (var index = 0; index < count; index++)
        {
            var id = NativeMethods.hypr_lock_get_surface_id(_lock, index);
            if (id == 0 || !currentIds.Add(id))
            {
                continue;
            }
            if (!_surfacesById.TryGetValue(id, out var surface))
            {
                surface = new Surface(id);
                _surfacesById.Add(id, surface);
            }
            surface.Name = GetSurfaceName(id);
            surface.Width = NativeMethods.hypr_lock_get_surface_width(_lock, id);
            surface.Height = NativeMethods.hypr_lock_get_surface_height(_lock, id);
            reconciled.Add(surface);
        }

        foreach (var id in _surfacesById.Keys.Where(id => !currentIds.Contains(id)).ToArray())
        {
            _surfacesById.Remove(id);
        }
        _surfaces.Clear();
        _surfaces.AddRange(reconciled);
    }

    private string GetSurfaceName(ulong id)
    {
        var buffer = new byte[256];
        var length = NativeMethods.hypr_lock_get_surface_name(_lock, id, buffer, buffer.Length);
        if (length <= 0)
        {
            return "";
        }
        if (length >= buffer.Length)
        {
            buffer = new byte[length + 1];
            length = NativeMethods.hypr_lock_get_surface_name(_lock, id, buffer, buffer.Length);
        }
        return length > 0 ? Encoding.UTF8.GetString(buffer, 0, Math.Min(length, buffer.Length)).TrimEnd('\0') : "";
    }
}
