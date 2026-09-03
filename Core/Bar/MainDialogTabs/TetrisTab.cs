using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Games.Tetris;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class TetrisTab : IMainDialogTab
{
    private readonly Theme _theme;
    private readonly DefaultTetris _game = new();

    internal TetrisTab(Theme theme)
    {
        _theme = theme;
        _game.Restart();
    }

    public string Id => "tetris";
    public string Title => "Tetris";
    public SvgAsset Icon => Icons.Gamepad;

    public void Activate() => _game.Resume();

    public bool HandleKey(DialogKey key)
    {
        switch (key)
        {
            case DialogKey.PhysicalA:
                _game.Move(-1);
                return true;
            case DialogKey.PhysicalD:
                _game.Move(1);
                return true;
            case DialogKey.PhysicalS:
                _game.SoftDrop();
                return true;
            case DialogKey.PhysicalW:
                _game.Rotate(RotationDirection.Clockwise);
                return true;
            case DialogKey.PhysicalQ:
                _game.Rotate(RotationDirection.CounterClockwise);
                return true;
            case DialogKey.PhysicalR:
                _game.Restart();
                return true;
            case DialogKey.PhysicalP:
                _game.TogglePause();
                return true;
            case DialogKey.Shift:
                _game.Hold();
                return true;
            case DialogKey.Space:
                _game.HardDrop();
                return true;
            default:
                return false;
        }
    }

    public void HandleTextInput(string text)
    {
    }

    public void HandleBackspace()
    {
    }

    public void MoveSelection(SelectionDirection direction)
    {
        switch (direction)
        {
            case SelectionDirection.Left:
                _game.Move(-1);
                break;
            case SelectionDirection.Right:
                _game.Move(1);
                break;
            case SelectionDirection.Up:
                _game.Rotate(RotationDirection.Clockwise);
                break;
            case SelectionDirection.Down:
                _game.SoftDrop();
                break;
        }
    }

    public void ActivateSelection() => _game.HardDrop();

    public Node Draw()
    {
        _game.Update(Renderer.DeltaTime);

        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 12 },
            Children =
            [
                MainDialogTabUi.BuildSectionHeader("Tetris", _game.Status),
                new BoxNode
                {
                    HorizontalAlignment = ItemsAlignment.Center,
                    VerticalAlignment = ItemsAlignment.Start,
                    Style = new Style { Spacing = 24 },
                    Children =
                    [
                        new BoxNode()
                        {
                            Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel) with
                            {
                                BorderRadius = 8,
                                Padding = 8,
                                Spacing = 4,
                            },
                            Children = [new TetrisBoardNode(_game)]
                        },
                        BuildSidebar(),
                    ],
                },
            ],
        };
    }

    private BoxNode BuildSidebar() => new (150 + 150 + 8)
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = Style.Spacer,
        Children =
        [
            BuildStats(),
            new BoxNode
            {
                HorizontalAlignment = ItemsAlignment.Spread,
                Style = Style.Spacer,
                Children =
                [
                    BuildPreview("HOLD", _game.HeldPiece),
                    BuildPreview("NEXT", _game.NextPiece),
                ],
            },
            new TextNode("Controls", _theme.Text.HeaderSize, _theme.Text),
            new TextNode("A/D or horizontal arrows move\nS or down arrow soft drop\nW or up arrow rotate   Q rotate CCW\nSpace/Enter hard drop   Shift hold\nP pause   R restart", 14,
                _theme.Text.MutedColor, 320, TextWrapping.Wrap),
        ],
    };

    private BoxNode BuildStats() => new ()
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel) with
        {
            BorderRadius = 8,
            Padding = 14,
            Spacing = 6,
        },
        Children =
        [
            MainDialogTabUi.BuildSectionHeader($"Score  {_game.Score:N0}", $"Level {_game.Level}"),
            new TextNode($"Lines: {_game.Lines}", _theme.Text, _theme.Text.MutedColor),
            new TextNode($"Combo: {_game.Combo}", _theme.Text, _theme.Text.MutedColor),
            new TextNode($"Time: {_game.CurrentTime:0.0}s", _theme.Text, _theme.Text.MutedColor),
        ],
    };

    private BoxNode BuildPreview(string title, TetraminoType? piece) => new (150)
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Center,
        Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel) with
        {
            BorderRadius = 8,
            Padding = 8,
            Spacing = 4,
        },
        Children =
        [
            new TextNode(title, _theme.Text, _theme.Text.MutedColor),
            new PiecePreviewNode(piece),
        ],
    };

    private sealed class TetrisBoardNode(TetrisGame game) : Node
    {
        private const int CellSize = 18;

        public override int Width => TetrisGame.BoardWidth * CellSize;
        public override int Height => TetrisGame.BoardHeight * CellSize;

        public override void Draw(IRenderApi renderer, int x, int y)
        {
            UpdateInteractionState(x, y);

            for (var row = 0; row < TetrisGame.BoardHeight; row++)
            {
                for (var column = 0; column < TetrisGame.BoardWidth; column++)
                {
                    renderer.FillRect(
                        new Rect(x + column * CellSize,
                            y + (TetrisGame.BoardHeight - 1 - row) * CellSize,
                            CellSize - 1,
                            CellSize - 1),
                        Color.FromRgb(0, 0, 0, 0.20f));
                }
            }

            foreach (var block in game.RenderBlocks.OrderBy(DrawOrder))
            {
                DrawBlock(renderer, x, y, block);
            }
        }

        private static int DrawOrder(Block block) => block.Layer switch
        {
            BlockLayer.Ghost => 0,
            BlockLayer.Board => 1,
            BlockLayer.Current => 2,
            BlockLayer.Clearing => 3,
            _ => 0,
        };

        private static void DrawBlock(IRenderApi renderer, int x, int y, Block block)
        {
            if (!block.IsVisible || block.Scale <= 0.001f)
            {
                return;
            }

            var size = (CellSize - 2) * block.Scale;
            var centerX = x + block.RenderPosition.X * CellSize + CellSize / 2f;
            var centerY = y + (TetrisGame.BoardHeight - 1 - block.RenderPosition.Y) * CellSize + CellSize / 2f;
            var rect = new Rect(centerX - size / 2f, centerY - size / 2f, size, size);
            var color = PieceColor(block.Type);
            if (block.Layer == BlockLayer.Ghost)
            {
                color = color with { A = 0.333f };
            }

            renderer.FillRoundedRect(rect, Math.Min(3, size / 4f), color);
            if (block.Layer != BlockLayer.Ghost && size > 6)
            {
                renderer.FillRect(new Rect(rect.X + size * 0.16f, rect.Y + size * 0.10f, size * 0.68f, size * 0.12f),
                    Color.Lighten(color, 0.28f));
            }
        }
    }

    private sealed class PiecePreviewNode(TetraminoType? piece) : Node
    {
        public override int Width => 112;
        public override int Height => 58;

        public override void Draw(IRenderApi renderer, int x, int y)
        {
            UpdateInteractionState(x, y);
            if (piece is not { } type)
            {
                return;
            }

            const int size = 14;
            var cells = TetrisGame.Cells(type);
            var minX = cells.Min(cell => cell.X);
            var maxX = cells.Max(cell => cell.X);
            var minY = cells.Min(cell => cell.Y);
            var maxY = cells.Max(cell => cell.Y);
            var originX = x + (Width - (maxX - minX + 1) * size) / 2 - minX * size;
            var originY = y + (Height - (maxY - minY + 1) * size) / 2 + maxY * size;
            foreach (var cell in cells)
            {
                renderer.FillRoundedRect(new Rect(originX + cell.X * size, originY - cell.Y * size, size - 2, size - 2),
                    3, PieceColor(type));
            }
        }
    }

    private static Color PieceColor(TetraminoType type) => type switch
    {
        TetraminoType.I => Color.FromRgb(  1, 237, 250),
        TetraminoType.J => Color.FromRgb( 24, 130, 246),
        TetraminoType.L => Color.FromRgb(255, 120,  12),
        TetraminoType.O => Color.FromRgb(250, 182,  21),
        TetraminoType.S => Color.FromRgb( 42, 218,  34),
        TetraminoType.T => Color.FromRgb(178,  10, 156),
        TetraminoType.Z => Color.FromRgb(234,  20,  28),
        _ => Color.White,
    };
}
