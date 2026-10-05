namespace HyprNetShell.Core.Games.Solitaire;

internal enum SolitaireSuit
{
    Diamonds,
    Clubs,
    Hearts,
    Spades,
}

internal enum SolitairePileKind
{
    Stock,
    Waste,
    Foundation,
    Tableau,
}

internal sealed class SolitaireCard(SolitaireSuit suit, int rank)
{
    internal SolitaireSuit Suit { get; } = suit;
    internal int Rank { get; } = rank;
    internal bool FaceUp { get; set; }
    internal float RenderX { get; set; } = -200;
    internal float RenderY { get; set; } = -200;
    internal float Flip { get; set; }
    internal float TiltDegrees { get; set; }
    internal bool IsRed => Suit is SolitaireSuit.Diamonds or SolitaireSuit.Hearts;

    internal string RankSymbol => Rank switch
    {
        1 => "A",
        11 => "J",
        12 => "Q",
        13 => "K",
        _ => Rank.ToString(),
    };

    internal string SuitSymbol => Suit switch
    {
        SolitaireSuit.Diamonds => "♦",
        SolitaireSuit.Clubs => "♣",
        SolitaireSuit.Hearts => "♥",
        SolitaireSuit.Spades => "♠",
        _ => "?",
    };
}

internal readonly record struct SolitaireSelection(SolitairePileKind Kind, int Pile = 0, int Card = 0);

internal sealed class SolitaireGame
{
    internal const int TableauCount = 7;

    private readonly List<SolitaireCard>[] _tableau = Enumerable.Range(0, TableauCount)
        .Select(_ => new List<SolitaireCard>())
        .ToArray();
    private readonly List<SolitaireCard>[] _foundations = Enumerable.Range(0, 4)
        .Select(_ => new List<SolitaireCard>())
        .ToArray();
    private readonly List<SolitaireCard> _stock = [];
    private readonly List<SolitaireCard> _waste = [];
    private readonly List<SolitaireCard> _held = [];
    private SolitaireSelection _heldFrom;
    private SolitaireSelection _lastActivated;
    private SolitaireCard? _shelf;
    private float _doubleTapRemaining;
    private int _stockPasses;

    internal IReadOnlyList<IReadOnlyList<SolitaireCard>> Tableau => _tableau;
    internal IReadOnlyList<IReadOnlyList<SolitaireCard>> Foundations => _foundations;
    internal IReadOnlyList<SolitaireCard> Stock => _stock;
    internal IReadOnlyList<SolitaireCard> Waste => _waste;
    internal IReadOnlyList<SolitaireCard> Held => _held;
    internal SolitaireCard? Shelf => _shelf;
    internal SolitaireSelection Selection { get; private set; }
    internal int Moves { get; private set; }
    internal bool IsWon => _foundations.All(pile => pile.Count == 13);
    internal bool CanRecycleStock => _stock.Count == 0 && _waste.Count > 0 && _stockPasses < Math.Max(0, SolitaireConfig.AmountOfDeals - 1);
    internal bool IsShelfAvailable => SolitaireConfig.FreeSlotEnabled && _stock.Count == 0 && !CanRecycleStock;
    internal string Status => IsWon ? $"Won in {Moves} moves" : _held.Count > 0 ? $"Holding {_held.Count} card{(_held.Count == 1 ? "" : "s")}" : $"{Moves} moves";

    internal SolitaireGame() => Restart();

    internal void Update(float deltaTime) => _doubleTapRemaining = Math.Max(0, _doubleTapRemaining - deltaTime);

    internal void Restart()
    {
        var cards = (from suit in Enum.GetValues<SolitaireSuit>()
                     from rank in Enumerable.Range(1, 13)
                     select new SolitaireCard(suit, rank)).ToArray();
        Random.Shared.Shuffle(cards);

        foreach (var pile in _tableau)
        {
            pile.Clear();
        }
        foreach (var pile in _foundations)
        {
            pile.Clear();
        }
        _stock.Clear();
        _waste.Clear();
        _held.Clear();

        var cardIndex = 0;
        for (var pileIndex = 0; pileIndex < TableauCount; pileIndex++)
        {
            for (var row = 0; row <= pileIndex; row++)
            {
                var card = cards[cardIndex++];
                card.FaceUp = !SolitaireConfig.CardsHidden || row == pileIndex;
                _tableau[pileIndex].Add(card);
            }
        }

        while (cardIndex < cards.Length)
        {
            _stock.Add(cards[cardIndex++]);
        }

        foreach (var card in cards)
        {
            card.Flip = card.FaceUp ? 0.0f : 1.0f;
        }

        _shelf = null;
        _doubleTapRemaining = 0;
        _stockPasses = 0;
        Moves = 0;
        Selection = new SolitaireSelection(SolitairePileKind.Stock);
    }

    internal void MoveSelection(int dx, int dy)
    {
        if (dx != 0)
        {
            MoveHorizontal(dx);
        }
        if (dy != 0)
        {
            MoveVertical(dy);
        }
        NormalizeSelection();
    }

    internal void Select(SolitaireSelection selection)
    {
        Selection = selection;
        NormalizeSelection();
    }

    internal void ActivateSelection()
    {
        var isDoubleTap = _doubleTapRemaining > 0 && Selection == _lastActivated;
        if (isDoubleTap && _held.Count > 0 && Selection == _heldFrom)
        {
            ReturnHeld();
            TryMoveSelectionToFoundation();
            _doubleTapRemaining = 0;
            return;
        }

        if (_held.Count == 0)
        {
            TakeOrDraw();
        }
        else
        {
            Drop();
        }

        _lastActivated = Selection;
        _doubleTapRemaining = SolitaireConfig.DoubleTapSeconds;
    }

    internal bool CancelHeld()
    {
        if (_held.Count == 0)
        {
            return false;
        }

        ReturnHeld();
        Selection = _heldFrom;
        NormalizeSelection();
        return true;
    }

    internal bool TryMoveSelectionToFoundation()
    {
        if (_held.Count != 0)
        {
            return false;
        }

        SolitaireCard? card = null;
        switch (Selection.Kind)
        {
            case SolitairePileKind.Stock when _shelf is not null:
                card = _shelf;
                break;
            case SolitairePileKind.Waste when _waste.Count > 0:
                card = _waste[^1];
                break;
            case SolitairePileKind.Tableau when _tableau[Selection.Pile].Count > 0:
                var pile = _tableau[Selection.Pile];
                if (Selection.Card == pile.Count - 1)
                {
                    card = pile[^1];
                }
                break;
        }

        if (card is null || !CanPlaceOnFoundation(card))
        {
            return false;
        }

        if (Selection.Kind == SolitairePileKind.Stock)
        {
            _shelf = null;
        }
        else if (Selection.Kind == SolitairePileKind.Waste)
        {
            _waste.RemoveAt(_waste.Count - 1);
        }
        else
        {
            _tableau[Selection.Pile].RemoveAt(_tableau[Selection.Pile].Count - 1);
            RevealTableauTop(Selection.Pile);
        }

        _foundations[(int)card.Suit].Add(card);
        Moves++;
        NormalizeSelection();
        return true;
    }

    private void TakeOrDraw()
    {
        switch (Selection.Kind)
        {
            case SolitairePileKind.Stock:
                if (_shelf is not null)
                {
                    _heldFrom = Selection;
                    _held.Add(_shelf);
                    _shelf = null;
                }
                else
                {
                    DrawStock();
                }
                return;
            case SolitairePileKind.Waste:
                if (_waste.Count == 0)
                {
                    return;
                }
                _heldFrom = Selection;
                _held.Add(_waste[^1]);
                _waste.RemoveAt(_waste.Count - 1);
                break;
            case SolitairePileKind.Foundation:
                var foundation = _foundations[Selection.Pile];
                if (foundation.Count == 0)
                {
                    return;
                }
                _heldFrom = Selection;
                _held.Add(foundation[^1]);
                foundation.RemoveAt(foundation.Count - 1);
                break;
            case SolitairePileKind.Tableau:
                TakeTableau();
                break;
        }
    }

    private void DrawStock()
    {
        if (_stock.Count > 0)
        {
            for (var draw = 0; draw < Math.Max(1, SolitaireConfig.CardsInDeal) && _stock.Count > 0; draw++)
            {
                var card = _stock[^1];
                _stock.RemoveAt(_stock.Count - 1);
                card.FaceUp = true;
                _waste.Add(card);
            }
            Moves++;
            return;
        }

        if (!CanRecycleStock)
        {
            return;
        }

        for (var index = _waste.Count - 1; index >= 0; index--)
        {
            var card = _waste[index];
            card.FaceUp = false;
            _stock.Add(card);
        }
        _waste.Clear();
        _stockPasses++;
        Moves++;
    }

    private void TakeTableau()
    {
        var pile = _tableau[Selection.Pile];
        if (pile.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(Selection.Card, 0, pile.Count - 1);
        if (!pile[index].FaceUp || !IsValidRun(pile, index))
        {
            return;
        }

        _heldFrom = Selection with { Card = index };
        _held.AddRange(pile.GetRange(index, pile.Count - index));
        pile.RemoveRange(index, pile.Count - index);
    }

    private void Drop()
    {
        var dropped = Selection.Kind switch
        {
            SolitairePileKind.Stock => DropOnShelf(),
            SolitairePileKind.Foundation => DropOnFoundation(),
            SolitairePileKind.Tableau => DropOnTableau(),
            _ => false,
        };

        if (!dropped)
        {
            return;
        }

        if (_heldFrom.Kind == SolitairePileKind.Tableau)
        {
            RevealTableauTop(_heldFrom.Pile);
        }
        _held.Clear();
        Moves++;
        NormalizeSelection();
    }

    private bool DropOnShelf()
    {
        if (!IsShelfAvailable || _shelf is not null || _held.Count != 1)
        {
            return false;
        }

        _shelf = _held[0];
        return true;
    }

    private bool DropOnFoundation()
    {
        if (_held.Count != 1 || Selection.Pile != (int)_held[0].Suit || !CanPlaceOnFoundation(_held[0]))
        {
            return false;
        }

        _foundations[Selection.Pile].Add(_held[0]);
        return true;
    }

    private bool DropOnTableau()
    {
        var pile = _tableau[Selection.Pile];
        var first = _held[0];
        if (pile.Count == 0
                ? SolitaireConfig.OnlyKingsOnEmptyTableau && first.Rank != 13
                : !CanStack(pile[^1], first))
        {
            return false;
        }

        pile.AddRange(_held);
        Selection = Selection with { Card = pile.Count - 1 };
        return true;
    }

    private bool CanPlaceOnFoundation(SolitaireCard card)
    {
        var foundation = _foundations[(int)card.Suit];
        return foundation.Count == 0 ? card.Rank == 1 : foundation[^1].Rank + 1 == card.Rank;
    }

    private static bool IsValidRun(IReadOnlyList<SolitaireCard> pile, int start)
    {
        for (var index = start; index < pile.Count - 1; index++)
        {
            if (!CanStack(pile[index], pile[index + 1]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool CanStack(SolitaireCard upper, SolitaireCard lower) =>
        upper.FaceUp && lower.FaceUp && upper.Rank == lower.Rank + 1 && upper.IsRed != lower.IsRed;

    private void ReturnHeld()
    {
        switch (_heldFrom.Kind)
        {
            case SolitairePileKind.Stock:
                _shelf = _held[0];
                break;
            case SolitairePileKind.Waste:
                _waste.AddRange(_held);
                break;
            case SolitairePileKind.Foundation:
                _foundations[_heldFrom.Pile].AddRange(_held);
                break;
            case SolitairePileKind.Tableau:
                _tableau[_heldFrom.Pile].InsertRange(_heldFrom.Card, _held);
                break;
        }
        _held.Clear();
    }

    private void RevealTableauTop(int pileIndex)
    {
        var pile = _tableau[pileIndex];
        if (pile.Count > 0)
        {
            pile[^1].FaceUp = true;
        }
    }

    private void MoveHorizontal(int direction)
    {
        if (Selection.Kind == SolitairePileKind.Tableau)
        {
            var pile = (Selection.Pile + direction + TableauCount) % TableauCount;
            Selection = new SolitaireSelection(SolitairePileKind.Tableau, pile, Selection.Card);
            return;
        }

        var topIndex = Selection.Kind switch
        {
            SolitairePileKind.Stock => 0,
            SolitairePileKind.Waste => 1,
            SolitairePileKind.Foundation => Selection.Pile + 2,
            _ => 0,
        };
        topIndex = (topIndex + direction + 6) % 6;
        Selection = topIndex switch
        {
            0 => new SolitaireSelection(SolitairePileKind.Stock),
            1 => new SolitaireSelection(SolitairePileKind.Waste),
            _ => new SolitaireSelection(SolitairePileKind.Foundation, topIndex - 2),
        };
    }

    private void MoveVertical(int direction)
    {
        if (Selection.Kind != SolitairePileKind.Tableau)
        {
            if (direction > 0)
            {
                var pile = Selection.Kind switch
                {
                    SolitairePileKind.Stock => 0,
                    SolitairePileKind.Waste => 1,
                    SolitairePileKind.Foundation => Selection.Pile + 3,
                    _ => 0,
                };
                Selection = new SolitaireSelection(SolitairePileKind.Tableau, pile, 0);
            }
            return;
        }

        var tableau = _tableau[Selection.Pile];
        if (direction < 0 && Selection.Card <= 0)
        {
            Selection = Selection.Pile switch
            {
                0 => new SolitaireSelection(SolitairePileKind.Stock),
                1 or 2 => new SolitaireSelection(SolitairePileKind.Waste),
                _ => new SolitaireSelection(SolitairePileKind.Foundation, Selection.Pile - 3),
            };
            return;
        }

        Selection = Selection with { Card = Selection.Card + direction };
    }

    private void NormalizeSelection()
    {
        if (Selection.Kind == SolitairePileKind.Foundation)
        {
            Selection = Selection with { Pile = Math.Clamp(Selection.Pile, 0, 3), Card = 0 };
            return;
        }
        if (Selection.Kind != SolitairePileKind.Tableau)
        {
            Selection = Selection with { Pile = 0, Card = 0 };
            return;
        }

        var pileIndex = Math.Clamp(Selection.Pile, 0, TableauCount - 1);
        var pile = _tableau[pileIndex];
        Selection = Selection with
        {
            Pile = pileIndex,
            Card = pile.Count == 0 ? 0 : Math.Clamp(Selection.Card, 0, pile.Count - 1),
        };
    }
}
