using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Games.Solitaire;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class SolitaireTab() : IMainDialogTab
{
    private readonly SolitaireGame _game = new();
    private readonly SolitaireCardTextures _textures = new();
    private readonly Dictionary<string, ModulesCommon.BoxState> _buttonStates = [];

    public string Id => "solitaire";
    public string Title => "Solitaire";
    public SvgAsset Icon => Icons.Gamepad;
    public bool HandleScroll => false;

    public void Activate()
    {
    }

    public bool HandleKey(DialogKey key)
    {
        switch (key)
        {
            case DialogKey.PhysicalA:
                _game.MoveSelection(-1, 0);
                return true;
            case DialogKey.PhysicalD:
                _game.MoveSelection(1, 0);
                return true;
            case DialogKey.PhysicalW:
                _game.MoveSelection(0, -1);
                return true;
            case DialogKey.PhysicalS:
                _game.MoveSelection(0, 1);
                return true;
            case DialogKey.PhysicalQ:
                return _game.CancelHeld();
            case DialogKey.PhysicalP:
                _game.TryMoveSelectionToFoundation();
                return true;
            case DialogKey.PhysicalR:
                _game.Restart();
                return true;
            case DialogKey.Space:
                _game.ActivateSelection();
                return true;
            default:
                return false;
        }
    }

    public bool HandleEscape() => _game.CancelHeld();

    public void MoveSelection(SelectionDirection direction)
    {
        var (dx, dy) = direction switch
        {
            SelectionDirection.Left => (-1, 0),
            SelectionDirection.Right => (1, 0),
            SelectionDirection.Up => (0, -1),
            SelectionDirection.Down => (0, 1),
            _ => (0, 0),
        };
        _game.MoveSelection(dx, dy);
    }

    public void ActivateSelection() => _game.ActivateSelection();

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
            MainDialogTabUi.BuildSectionHeader("Solitaire", _game.Status),
            new BoxNode
            {
                HorizontalAlignment = ItemsAlignment.Center,
                VerticalAlignment = ItemsAlignment.Start,
                Style = new Style { Spacing = 20 },
                Children =
                [
                    new BoxNode
                    {
                        Style = ModulesCommon.ModuleStyle(Color.FromRgb(16, 92, 52, 0.96f)) with
                        {
                            BorderRadius = 8,
                            BorderWidth = 0,
                            Padding = 12,
                        },
                        Children = [new SolitaireBoardNode(_game, _textures)],
                    },
                    BuildSidebar(),
                ],
            },
        ],
        };
    }

    private BoxNode BuildSidebar() => new(220)
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = Style.Spacer,
        Children =
        [
            MainDialogTabUi.BuildButton(_buttonStates, "New game", "restart", _game.Restart),
            MainDialogTabUi.BuildButton(_buttonStates,
                "Move to foundation",
                "foundation",
                () => { _game.TryMoveSelectionToFoundation(); }),
            new TextNode("Controls", ThemeManager.Current.Text.HeaderSize),
            new TextNode(
                $"Arrows or W/A/S/D select\nSpace/Enter draw, pick up, or place\nDouble-tap sends an eligible card home\nP moves the selected card to its foundation\nQ/Escape returns held cards\nR starts a new game\n\nDeals: {SolitaireConfig.AmountOfDeals}   Draw: {SolitaireConfig.CardsInDeal}" +
                (SolitaireConfig.FreeSlotEnabled ? "\nAfter the final deal, the empty stock becomes a one-card shelf." : ""),
                14,
                ThemeManager.Current.Text.MutedColor,
                220,
                TextWrapping.Wrap),
        ],
    };

    private sealed class SolitaireBoardNode(SolitaireGame game, SolitaireCardTextures textures) : Node
    {
        private const int CardWidth = 60;
        private const int CardHeight = 86;
        private const int ColumnStep = 70;
        private const int TableauTop = 112;
        private const int FaceDownStep = 18;
        private const int FaceUpStep = 24;

        public override int Width => ColumnStep * SolitaireGame.TableauCount - (ColumnStep - CardWidth);
        public override int Height => 468;

        public override void Draw(IRenderApi renderer, int x, int y)
        {
            UpdateInteractionState(x, y);
            HandlePointer(x, y);

            DrawEmptySlot(renderer, x, y);
            if (game.Stock.Count > 0)
            {
                for (var index = 0; index < game.Stock.Count; index++)
                {
                    DrawCard(renderer, game.Stock[index], x + Math.Min(index, 5), y,
                        hidden: true,
                        selected: IsSelected(SolitairePileKind.Stock) && index == game.Stock.Count - 1);
                }
            }
            else if (game.Shelf is not null)
            {
                DrawCard(renderer, game.Shelf, x, y, selected: IsSelected(SolitairePileKind.Stock));
            }
            else if (game.CanRecycleStock)
            {
                renderer.DrawText("↻", x + 20, y + 51, 25, Color.FromRgb(210, 225, 210, 0.75f));
            }
            else if (SolitaireConfig.FreeSlotEnabled)
            {
                renderer.DrawText("FREE", x + 8, y + 47, 12, Color.FromRgb(210, 230, 210, 0.55f));
            }

            DrawEmptySlot(renderer, x + ColumnStep, y);
            if (game.Waste.Count > 0)
            {
                for (var index = 0; index < game.Waste.Count; index++)
                {
                    DrawCard(renderer, game.Waste[index], x + ColumnStep + Math.Min(index, 5), y,
                        selected: IsSelected(SolitairePileKind.Waste) && index == game.Waste.Count - 1);
                }
            }

            for (var foundation = 0; foundation < 4; foundation++)
            {
                var foundationX = x + (foundation + 3) * ColumnStep;
                DrawEmptySlot(renderer, foundationX, y, SuitSymbol((SolitaireSuit)foundation));
                var pile = game.Foundations[foundation];
                for (var index = 0; index < pile.Count; index++)
                {
                    DrawCard(renderer, pile[index], foundationX + Math.Min(index, 5), y,
                        selected: IsSelected(SolitairePileKind.Foundation, foundation) && index == pile.Count - 1);
                }
            }

            for (var pileIndex = 0; pileIndex < SolitaireGame.TableauCount; pileIndex++)
            {
                var pile = game.Tableau[pileIndex];
                var cardY = y + TableauTop;
                if (pile.Count == 0)
                {
                    DrawEmptySlot(renderer, x + pileIndex * ColumnStep, cardY, "K");
                    continue;
                }

                for (var cardIndex = 0; cardIndex < pile.Count; cardIndex++)
                {
                    DrawCard(renderer, pile[cardIndex], x + pileIndex * ColumnStep, cardY,
                        selected: IsSelected(SolitairePileKind.Tableau, pileIndex, cardIndex));
                    cardY += CardStep(pile[cardIndex]);
                }
            }

            DrawEmptySelection(renderer, x, y);
            DrawHeldCards(renderer, x, y);
        }

        private void HandlePointer(int x, int y)
        {
            var input = Layout.Input;
            if (!input.PointerPressed || !input.HasPointer ||
                input.PointerX < x || input.PointerX >= x + Width ||
                input.PointerY < y || input.PointerY >= y + Height)
            {
                return;
            }

            var localX = input.PointerX - x;
            var localY = input.PointerY - y;
            if (game.Held.Count > 0)
            {
                var heldTarget = SelectionRect(x, y);
                var heldBounds = new Rect(heldTarget.X, heldTarget.Y, CardWidth + 14,
                    CardHeight + FaceUpStep * (game.Held.Count - 1) + 22);
                if (heldBounds.Contains(input.PointerX, input.PointerY))
                {
                    game.ActivateSelection();
                    return;
                }
            }

            SolitaireSelection? selection = null;
            if (localY < CardHeight)
            {
                var column = (int)(localX / ColumnStep);
                selection = column switch
                {
                    0 => new SolitaireSelection(SolitairePileKind.Stock),
                    1 => new SolitaireSelection(SolitairePileKind.Waste),
                    >= 3 and <= 6 => new SolitaireSelection(SolitairePileKind.Foundation, column - 3),
                    _ => null,
                };
            }
            else if (localY >= TableauTop)
            {
                var pileIndex = Math.Clamp((int)(localX / ColumnStep), 0, SolitaireGame.TableauCount - 1);
                var pile = game.Tableau[pileIndex];
                var cardIndex = 0;
                var cardY = TableauTop;
                for (var index = 0; index < pile.Count; index++)
                {
                    cardIndex = index;
                    if (localY < cardY + (index == pile.Count - 1 ? CardHeight : CardStep(pile[index])))
                    {
                        break;
                    }
                    cardY += CardStep(pile[index]);
                }
                selection = new SolitaireSelection(SolitairePileKind.Tableau, pileIndex, cardIndex);
            }

            if (selection is { } selected)
            {
                game.Select(selected);
                game.ActivateSelection();
            }
        }

        private void DrawEmptySelection(IRenderApi renderer, int x, int y)
        {
            if (game.Held.Count > 0 || SelectionHasCard())
            {
                return;
            }

            var rect = SelectionRect(x, y);
            renderer.FillRoundedBorder(
                new Rect(rect.X - 3, rect.Y - 3, rect.Width + 6, rect.Height + 6),
                7,
                3,
                Color.FromRgb(255, 210, 45));
        }

        private bool SelectionHasCard() => game.Selection.Kind switch
        {
            SolitairePileKind.Stock => game.Stock.Count > 0 || game.Shelf is not null,
            SolitairePileKind.Waste => game.Waste.Count > 0,
            SolitairePileKind.Foundation => game.Foundations[game.Selection.Pile].Count > 0,
            SolitairePileKind.Tableau => game.Tableau[game.Selection.Pile].Count > 0,
            _ => false,
        };

        private bool IsSelected(SolitairePileKind kind, int pile = 0, int card = 0) =>
            game.Held.Count == 0 &&
            game.Selection.Kind == kind &&
            (kind is not (SolitairePileKind.Foundation or SolitairePileKind.Tableau) || game.Selection.Pile == pile) &&
            (kind != SolitairePileKind.Tableau || game.Selection.Card == card);

        private void DrawHeldCards(IRenderApi renderer, int x, int y)
        {
            if (game.Held.Count == 0)
            {
                return;
            }

            var target = SelectionRect(x, y);
            var heldY = target.Y + 18;
            for (var index = 0; index < game.Held.Count; index++)
            {
                DrawCard(renderer, game.Held[index], (int)target.X + 7, (int)heldY, selected: index == 0);
                heldY += FaceUpStep;
            }
        }

        private Rect SelectionRect(int x, int y)
        {
            var selection = game.Selection;
            return selection.Kind switch
            {
                SolitairePileKind.Stock => new Rect(x, y, CardWidth, CardHeight),
                SolitairePileKind.Waste => new Rect(x + ColumnStep, y, CardWidth, CardHeight),
                SolitairePileKind.Foundation => new Rect(x + (selection.Pile + 3) * ColumnStep, y, CardWidth, CardHeight),
                SolitairePileKind.Tableau => TableauSelectionRect(selection, x, y),
                _ => new Rect(x, y, CardWidth, CardHeight),
            };
        }

        private Rect TableauSelectionRect(SolitaireSelection selection, int x, int y)
        {
            var pile = game.Tableau[selection.Pile];
            var cardY = y + TableauTop;
            for (var index = 0; index < Math.Min(selection.Card, pile.Count); index++)
            {
                cardY += CardStep(pile[index]);
            }
            return new Rect(x + selection.Pile * ColumnStep, cardY, CardWidth, CardHeight);
        }

        private static int CardStep(SolitaireCard card) => card.FaceUp ? FaceUpStep : FaceDownStep;

        private static void DrawEmptySlot(IRenderApi renderer, int x, int y, string? label = null)
        {
            renderer.FillRoundedBorder(
                new Rect(x, y, CardWidth, CardHeight),
                6,
                2,
                Color.FromRgb(205, 225, 205, 0.4f));
            if (label is not null)
            {
                renderer.DrawText(label, x + 22, y + 49, 19, Color.FromRgb(210, 230, 210, 0.45f));
            }
        }

        private void DrawCard(
            IRenderApi renderer,
            SolitaireCard card,
            int x,
            int y,
            bool hidden = false,
            bool selected = false)
        {
            var deltaTime = Math.Max(Renderer.DeltaTime, 0.0001f);
            var amount = 1.0f - MathF.Exp(-SolitaireConfig.AnimationSpeed * deltaTime);
            var previousX = card.RenderX;
            card.RenderX += (x - card.RenderX) * amount;
            card.RenderY += (y - card.RenderY) * amount;

            var targetFlip = hidden || !card.FaceUp ? 1.0f : 0.0f;
            card.Flip += (targetFlip - card.Flip) * amount;
            var horizontalVelocity = (previousX - card.RenderX) / deltaTime;
            var targetTilt = Math.Clamp(-horizontalVelocity * 0.1f,
                -SolitaireConfig.MaximumTiltDegrees,
                SolitaireConfig.MaximumTiltDegrees);
            card.TiltDegrees += (targetTilt - card.TiltDegrees) * amount;

            var flipScale = 1.0f - MathF.Abs(MathF.Abs(0.5f - card.Flip) - 0.5f);
            var width = CardWidth * flipScale;
            var rect = new Rect(
                card.RenderX + (CardWidth - width) / 2.0f,
                card.RenderY,
                width,
                CardHeight);
            if (selected)
            {
                renderer.FillRoundedBorder(
                    new Rect(rect.X - 3, rect.Y - 3, rect.Width + 6, rect.Height + 6),
                    7,
                    3,
                    Color.FromRgb(255, 210, 45));
            }

            var image = card.Flip > 0.5f ? textures.Back : textures.Face(card);
            renderer.DrawImage(image, rect, Color.White, card.TiltDegrees * MathF.PI / 180.0f);
        }

        private static string SuitSymbol(SolitaireSuit suit) => suit switch
        {
            SolitaireSuit.Diamonds => "♦",
            SolitaireSuit.Clubs => "♣",
            SolitaireSuit.Hearts => "♥",
            SolitaireSuit.Spades => "♠",
            _ => "?",
        };
    }
}
