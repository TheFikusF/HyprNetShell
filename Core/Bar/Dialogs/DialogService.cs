using System.Collections.Concurrent;
using HyprNetShell.Core.Bar.MainDialogTabs;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Dialogs;

internal enum DialogKey
{
    None,
    Escape,
    Backspace,
    Tab,
    Enter,
    Up,
    Left,
    Right,
    Down,
}

internal readonly record struct DialogInput(DialogKey Key, string Text, float ScrollDelta);

internal enum DialogInputResult
{
    None,
    Close,
}

internal interface IDialogWindow : IDrawableModule
{
    void OnOpened();
    void OnClosed();
    DialogInputResult HandleInput(DialogInput input);
}

public sealed class DialogService : IDisposable
{
    private sealed class WindowState(IDialogWindow window)
    {
        internal IDialogWindow Window { get; } = window;
        internal bool IsOpen { get; set; }
        internal float Opacity { get; set; }
    }

    private sealed record PendingCompositeOpen(Type WindowType, IReadOnlyList<IMainDialogTab> Tabs);

    private readonly Dictionary<Type, WindowState> _windows = [];
    private readonly ConcurrentQueue<PendingCompositeOpen> _pendingCompositeOpens = new();
    private WindowState? _activeWindow;
    private bool _disposed;

    public bool IsOpen => _activeWindow?.IsOpen == true;
    public bool IsVisible => _activeWindow is { } state && (state.IsOpen || state.Opacity > 0.1f);

    internal void Register<T>(T window) where T : class, IDialogWindow
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _windows.Add(typeof(T), new WindowState(window));
    }

    internal void Open<T>() where T : class, IDialogWindow
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = GetState<T>();
        if (state.Window is CompositeWindow)
        {
            throw new InvalidOperationException(
                $"{nameof(CompositeWindow)} must be opened with a non-empty tab collection.");
        }

        OpenState(state);
    }

    internal void Open<T>(IReadOnlyList<IMainDialogTab> tabs) where T : class, IDialogWindow
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = GetState<T>();
        if (state.Window is not CompositeWindow compositeWindow)
        {
            throw new InvalidOperationException(
                $"Tabs can only be supplied when opening {nameof(CompositeWindow)}.");
        }

        compositeWindow.SetTabs(tabs);
        OpenState(state, restart: true);
    }

    internal void Toggle<T>() where T : class, IDialogWindow
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = GetState<T>();
        if (state.Window is CompositeWindow)
        {
            throw new InvalidOperationException(
                $"{nameof(CompositeWindow)} must be opened with a non-empty tab collection.");
        }

        ToggleState(state);
    }

    internal void RequestOpen<T>(IReadOnlyList<IMainDialogTab> tabs) where T : class, IDialogWindow
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (typeof(T) != typeof(CompositeWindow))
        {
            throw new InvalidOperationException(
                $"Queued tab-based opening is only supported for {nameof(CompositeWindow)}.");
        }

        ArgumentOutOfRangeException.ThrowIfZero(tabs.Count);
        _pendingCompositeOpens.Enqueue(new PendingCompositeOpen(typeof(T), tabs));
    }

    public void ProcessPendingRequests()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_pendingCompositeOpens.TryDequeue(out var request))
        {
            var state = GetState(request.WindowType);
            var compositeWindow = (CompositeWindow)state.Window;
            compositeWindow.SetTabs(request.Tabs);
            OpenState(state, restart: true);
        }
    }

    public void Close()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_activeWindow is not { IsOpen: true } state)
        {
            return;
        }

        state.IsOpen = false;
        state.Window.OnClosed();
    }

    public void HandleInput(int pressedKey, string textInput, float scrollDelta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_activeWindow is not { IsOpen: true } state)
        {
            return;
        }

        var input = new DialogInput(ToDialogKey(pressedKey), textInput, scrollDelta);
        if (state.Window.HandleInput(input) == DialogInputResult.Close)
        {
            Close();
        }
    }

    public Node Draw()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_activeWindow is not { } state)
        {
            return new SpacerNode();
        }

        state.Opacity = PrimitivesMath.LerpSmooth(state.Opacity, state.IsOpen ? 1 : 0, 24, Renderer.DeltaTime);
        if (state.Opacity <= 0.1f)
        {
            return new SpacerNode();
        }

        var content = state.Window.Draw();
        content.Opacity *= state.Opacity;
        return content;
    }

    private void OpenState(WindowState next, bool restart = false)
    {
        if (ReferenceEquals(_activeWindow, next) && next.IsOpen)
        {
            if (restart)
            {
                next.Window.OnOpened();
            }

            return;
        }

        if (_activeWindow is { } current)
        {
            if (current.IsOpen)
            {
                current.IsOpen = false;
                current.Window.OnClosed();
            }

            current.Opacity = 0;
        }

        _activeWindow = next;
        next.IsOpen = true;
        next.Window.OnOpened();
    }

    private void ToggleState(WindowState state)
    {
        if (ReferenceEquals(_activeWindow, state) && state.IsOpen)
        {
            Close();
        }
        else
        {
            OpenState(state);
        }
    }

    private WindowState GetState<T>() where T : class, IDialogWindow => GetState(typeof(T));

    private WindowState GetState(Type type) => _windows.TryGetValue(type, out var state)
        ? state
        : throw new InvalidOperationException($"Dialog window {type.Name} is not registered");

    private static DialogKey ToDialogKey(int key) => key switch
    {
        1 => DialogKey.Escape,
        14 => DialogKey.Backspace,
        15 => DialogKey.Tab,
        28 => DialogKey.Enter,
        103 => DialogKey.Up,
        105 => DialogKey.Left,
        106 => DialogKey.Right,
        108 => DialogKey.Down,
        _ => DialogKey.None,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var state in _windows.Values)
        {
            if (state.Window is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _windows.Clear();
        _activeWindow = null;
    }
}
