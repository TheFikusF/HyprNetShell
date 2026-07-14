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
    string Title { get; }
    SvgAsset Icon { get; }

    void Activate();
    void HandleTextInput(string text);
    void HandleBackspace();
    void MoveSelection(SelectionDirection direction);
    void ActivateSelection();
    Node Draw();
}
