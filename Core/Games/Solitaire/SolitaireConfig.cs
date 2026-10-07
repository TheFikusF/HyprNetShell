namespace HyprNetShell.Core.Games.Solitaire;

internal readonly struct SolitaireConfig
{
    internal const bool CARDS_HIDDEN = true;
    internal const bool FREE_SLOT_ENABLED = true;
    internal const bool ONLY_KINGS_ON_EMPTY_TABLEAU = true;
    internal const int AMOUNT_OF_DEALS = 2;
    internal const int CARDS_IN_DEAL = 1;
    internal const float DOUBLE_TAP_SECONDS = 0.25f;
    internal const float ANIMATION_SPEED = 10.0f;
    internal const float MAXIMUM_TILT_DEGREES = 30.0f;
}
