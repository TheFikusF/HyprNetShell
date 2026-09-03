using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal enum SelectionDirection
{
    Up,
    Down,
    Left,
    Right,
}

internal interface IMainDialogTab
{
    string Id { get; }
    string Title { get; }
    SvgAsset Icon { get; }

    void Activate();
    bool HandleKey(DialogKey key) => false;
    void HandleTextInput(string text);
    void HandleBackspace();
    bool HandleEscape() => false;
    void MoveSelection(SelectionDirection direction);
    void ActivateSelection();
    Node Draw();
}
