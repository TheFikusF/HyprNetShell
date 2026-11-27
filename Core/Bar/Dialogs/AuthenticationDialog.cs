using HyprNetShell.Core.Bar.Common;

using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Native.Interop.Authorization;

namespace HyprNetShell.Core.Bar.Dialogs;

public sealed class AuthorizationDialogController
{
    private readonly DialogService _dialogs;
    private readonly DesktopAuthorization _authorization;
    private readonly AuthenticationDialog _window;

    public ulong? PromptId => _window.Prompt?.Id;
    public bool IsOpen => PromptId is not null;

    public AuthorizationDialogController(DialogService dialogs, DesktopAuthorization authorization)
    {
        _dialogs = dialogs;
        _authorization = authorization;
        _window = new AuthenticationDialog(authorization);
        _dialogs.Register(_window);
    }

    public void Update()
    {
        _authorization.Update();
        SetPrompt(_authorization.Prompt);
    }

    public void HandleSensitiveInput(int pressedKey, ReadOnlySpan<char> text, bool controlPressed) =>
        _window.HandleSensitiveInput(DialogService.ToDialogKey(pressedKey, text), text, controlPressed);

    public void Cancel()
    {
        if (_window.Prompt is { } prompt)
        {
            _authorization.Cancel(prompt.Id);
            SetPrompt(null);
        }
    }

    public void RevokeAll()
    {
        _authorization.RevokeAll();
        SetPrompt(null);
    }

    private void SetPrompt(AuthorizationPrompt? prompt)
    {
        _window.Prompt = prompt;
        _dialogs.SetPriorityWindow<AuthenticationDialog>(prompt is not null);
    }
}

internal sealed class AuthenticationDialog(DesktopAuthorization authorization) : IDialogWindow
{
    private const int DIALOG_WIDTH = 400;


    private readonly ActionButton _cancel = new() { Text = "Cancel" };
    private readonly ActionButton _submit = new() { Text = "Authenticate" };

    internal AuthorizationPrompt? Prompt { get; set; }

    public void OnOpened() { }

    public void OnClosed() { }

    public DialogInputResult HandleInput(DialogInput input) => DialogInputResult.None;

    internal void HandleSensitiveInput(DialogKey key, ReadOnlySpan<char> text, bool controlPressed)
    {
        if (Prompt is not { } prompt)
        {
            return;
        }

        if (key == DialogKey.Escape)
        {
            authorization.Cancel(prompt.Id);
            return;
        }

        // A frame can contain printable text followed by Enter or Backspace.
        if (!controlPressed && !text.IsEmpty)
        {
            authorization.Append(prompt.Id, text);
        }

        switch (key)
        {
            case DialogKey.Backspace:
                authorization.Backspace(prompt.Id);
                break;
            case DialogKey.Enter:
                authorization.Submit(prompt.Id);
                break;
        }
    }

    public Node Draw()
    {
        if (Prompt is not { } prompt)
        {
            return new SpacerNode();
        }

        var id = prompt.Id;
        _cancel.Action = () => authorization.Cancel(id);
        _submit.Action = () => authorization.Submit(id);

        return new BoxNode(DIALOG_WIDTH) {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.PopupStyle() with { Padding = 20, Spacing = 12 },
            Children =
            [
                new TextNode("Authentication required", 18),
                new TextNode(prompt.Message, maxWidth: DIALOG_WIDTH - 40, wrapping: TextWrapping.Wrap),
                PasswordFieldView.Build(prompt.PasswordLength),
                new BoxNode
                {
                    HorizontalAlignment = ItemsAlignment.End,
                    Style = Style.Spacer with { Spacing = 8 },
                    Children = [_cancel.Build(), _submit.Build()],
                },
            ],
        };
    }
}
