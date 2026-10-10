using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Games.Minesweeper;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Renderers;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class MinesweeperTab() : IMainDialogTab
{
    private readonly MinesweeperGame _game = new();
    private readonly Dictionary<string, ModulesCommon.BoxState> _buttonStates = [];

    private bool _flagMode;
    public string Id => "minesweeper";
    public string Title => "Minesweeper";
    public SvgAsset Icon => Icons.Gamepad;
    public bool HandleScroll => false;

    public void Activate()
    {
    }

    public void ActivateSelection() => _game.Reveal();

    public void MoveSelection(SelectionDirection direction)
    {
        var (dx, dy) = direction switch {
            SelectionDirection.Left => (-1, 0),
            SelectionDirection.Right => (1, 0),
            SelectionDirection.Up => (0, -1),
            SelectionDirection.Down => (0, 1),
            _ => (0, 0),
        };
        _game.Move(dx, dy);
    }

    public bool HandleKey(DialogKey key)
    {
        switch (key)
        {
            case DialogKey.PhysicalA:
                _game.Move(-1, 0);
                break;
            case DialogKey.PhysicalD:
                _game.Move(1, 0);
                break;
            case DialogKey.PhysicalW:
                _game.Move(0, -1);
                break;
            case DialogKey.PhysicalS:
                _game.Move(0, 1);
                break;
            case DialogKey.PhysicalQ:
                _game.ToggleFlag();
                break;
            case DialogKey.PhysicalR:
                Restart();
                break;
            case DialogKey.Space:
                _game.Reveal();
                break;
            default:
                return false;
        }
        return true;
    }

    private void Restart()
    {
        _game.Restart();
        _flagMode = false;
    }

    public Node Draw()
    {
        _game.Update(Renderer.DeltaTime);
        return new BoxNode {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 12 },
            Children =
            [
                MainDialogTabUi.BuildSectionHeader(Title, _game.Status),
                new BoxNode
                {
                    HorizontalAlignment = ItemsAlignment.Center,
                    Direction = Direction.Vertical,
                    Style = Style.Spacer,
                    Children =
                    [
                        new BoxNode
                        {
                            VerticalAlignment = ItemsAlignment.Center,
                            Style = new Style { Spacing = 12 },
                            Children =
                            [
                                new TextNode($"Mines: {MinesweeperGame.MineCount - _game.Flags}", color: ThemeManager.Current.Text.MutedColor),
                                new TextNode($"Time: {_game.Seconds:0}s", color: ThemeManager.Current.Text.MutedColor),
                                MainDialogTabUi.BuildButton(_buttonStates, "New game", "restart", Restart),
                                MainDialogTabUi.BuildButton(_buttonStates, _flagMode ? "Click mode: flag" : "Click mode: reveal", "mode", () => _flagMode = !_flagMode),
                            ],
                        },
                        new BoxNode
                        {
                            Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with { Padding = 8, BorderRadius = 8 },
                            Children = [new BoardNode(this)],
                        },
                        new TextNode("Click to reveal or flag • Arrows / W/A/S/D select\nSpace / Enter reveal • Q flag • R new game\nReveal a number again to clear neighbors when enough flags surround it.\nYour first click is always safe.", 14, ThemeManager.Current.Text.MutedColor, MinesweeperGame.BoardWidth * BoardNode.CELL_SIZE, TextWrapping.Wrap),
                    ],
                },
            ],
        };
    }

    private sealed class BoardNode(MinesweeperTab tab) : Node
    {
        internal const int CELL_SIZE = 25;

        public override int Width => MinesweeperGame.BoardWidth * CELL_SIZE;

        public override int Height => MinesweeperGame.BoardHeight * CELL_SIZE;

        public override void Draw(IRenderApi renderer, int x, int y)
        {
            UpdateInteractionState(x, y);

            var game = tab._game;
            var input = Layout.Input;
            if (input.HasPointer && input.PointerPressed && input.PointerX >= x && input.PointerX < x + Width && input.PointerY >= y && input.PointerY < y + Height)
            {
                game.Select((int)(input.PointerX - x) / CELL_SIZE, (int)(input.PointerY - y) / CELL_SIZE);
                if (tab._flagMode)
                {
                    game.ToggleFlag();
                }
                else
                {
                    game.Reveal();
                }
            }

            for (var row = 0; row < MinesweeperGame.BoardHeight; row++)
            {
                for (var column = 0; column < MinesweeperGame.BoardWidth; column++)
                {
                    var revealed = game.IsRevealed(column, row);
                    var selected = column == game.SelectedX && row == game.SelectedY;
                    var rect = new Rect(x + column * CELL_SIZE, y + row * CELL_SIZE, CELL_SIZE - 2, CELL_SIZE - 2);
                    if (selected)
                    {
                        renderer.FillRoundedRect(rect, 4, Color.FromRgb(100, 180, 250));
                    }

                    var background = revealed ? Color.FromRgb(35, 42, 50) : Color.FromRgb(75, 88, 103);
                    renderer.FillRoundedRect(new Rect(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), 3, background);
                    var mine = game.IsMine(column, row) && (revealed || game.Finished);
                    var count = game.Adjacent(column, row);
                    var text = mine ? "*" : game.IsFlagged(column, row) ? (game.Finished && !game.IsMine(column, row) ? "X" : "F") : revealed && count > 0 ? count.ToString() : "";
                    var color = mine
                        ? Color.White
                        : game.IsFlagged(column, row)
                        ? Color.FromRgb(255, 205, 90)
                        : count switch {
                            1 => Color.FromRgb(110, 180, 255),
                            2 => Color.FromRgb(110, 220, 140),
                            3 => Color.FromRgb(255, 120, 120),
                            _ => Color.FromRgb(210, 170, 255),
                        };
                    renderer.DrawText(text, rect.X + 8, rect.Y + 16, 14, color);
                }
            }
        }
    }
}
