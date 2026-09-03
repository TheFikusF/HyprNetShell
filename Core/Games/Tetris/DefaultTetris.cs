namespace HyprNetShell.Core.Games.Tetris;

public sealed class DefaultTetris(int startingLevel = 1, float maxTime = 0f, int? randomSeed = null)
    : TetrisGame(startingLevel, maxTime, randomSeed)
{
    public static int GetLineClearScore(int amount, bool perfect) => perfect
        ? amount switch
        {
            1 => 100,
            2 => 300,
            3 => 500,
            4 => 800,
            _ => 0,
        }
        : amount switch
        {
            1 => 800,
            2 => 1200,
            3 => 1800,
            4 => 2000,
            _ => 0,
        };

    public static int GetComboMultiplier() => 50;
    public static int GetHoldQueueSize() => 1;
}
