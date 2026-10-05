namespace HyprNetShell.Core.Games.Solitaire;

internal readonly struct SolitaireConfig
{
    internal const bool CardsHidden = true;
    internal const bool FreeSlotEnabled = true;
    internal const bool OnlyKingsOnEmptyTableau = true;
    internal const int AmountOfDeals = 2;
    internal const int CardsInDeal = 1;
    internal const float DoubleTapSeconds = 0.25f;
    internal const float AnimationSpeed = 10.0f;
    internal const float MaximumTiltDegrees = 30.0f;
}
