using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.System;
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
        public IDialogWindow Window { get; } = window;
        public bool IsOpen { get; set; }
        public float Opacity { get; set; }
    }

    private readonly Dictionary<Type, WindowState> _windows = [];
    private WindowState? _activeWindow;
    private bool _disposed;

    internal DialogService(StatusBarServices services, Theme theme)
    {
        Register(new MainDialog(services.ClipboardHistory, services.History, services.Hyprctl, services.Wallpapers, Close, theme));
        Register(new WifiDialog(services.Network, theme));
    }

    public bool IsOpen => _activeWindow?.IsOpen == true;
    public bool IsVisible => _activeWindow is { } state && (state.IsOpen || state.Opacity > 0.1f);

    public void ToggleMainDialog() => Toggle<MainDialog>();

    internal void Open<T>() where T : class, IDialogWindow
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = GetState<T>();
        if (ReferenceEquals(_activeWindow, next) && next.IsOpen)
        {
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

    internal void Toggle<T>() where T : class, IDialogWindow
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = GetState<T>();
        if (ReferenceEquals(_activeWindow, state) && state.IsOpen)
        {
            Close();
        }
        else
        {
            Open<T>();
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

        state.Opacity = PrimitivesMath.LerpSmooth(state.Opacity, state.IsOpen ? 1 : 0,
            24.0f, Renderer.DeltaTime);

        if (state.Opacity <= 0.1f)
        {
            return new SpacerNode();
        }

        var content = state.Window.Draw();
        content.Opacity *= state.Opacity;
        return content;
    }

    private void Register<T>(T window) where T : class, IDialogWindow =>
        _windows.Add(typeof(T), new WindowState(window));

    private WindowState GetState<T>() where T : class, IDialogWindow =>
        _windows.TryGetValue(typeof(T), out var state)
            ? state
            : throw new InvalidOperationException($"Dialog window {typeof(T).Name} is not registered");

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
