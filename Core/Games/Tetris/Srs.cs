namespace HyprNetShell.Core.Games.Tetris;

internal interface IRotationSystem
{
    int WallKickChecksCount { get; }
    int[,] this[TetraminoType type] { get; }
    Block?[,] GetRotation(Tetramino tetramino, RotationDirection direction);
    bool Rotate(Tetramino tetramino, RotationDirection direction, out int testIndex);
}

internal sealed class Srs(TetrisGame game) : IRotationSystem
{
    internal const int TestsCount = 5;

    private static readonly CellPoint[,] WallKickData =
    {
        { new(0, 0), new(-1, 0), new(-1, +1), new(0, -2), new(-1, -2) },
        { new(0, 0), new(+1, 0), new(+1, -1), new(0, +2), new(+1, +2) },
        { new(0, 0), new(+1, 0), new(+1, +1), new(0, -2), new(+1, -2) },
        { new(0, 0), new(-1, 0), new(-1, -1), new(0, +2), new(-1, +2) },
    };

    private static readonly CellPoint[,] WallKickDataI =
    {
        { new(0, 0), new(-2, 0), new(+1, 0), new(-2, -1), new(+1, +2) },
        { new(0, 0), new(-1, 0), new(+2, 0), new(-1, +2), new(+2, -1) },
        { new(0, 0), new(+2, 0), new(-1, 0), new(+2, +1), new(-1, -2) },
        { new(0, 0), new(+1, 0), new(-2, 0), new(+1, -2), new(-2, +1) },
    };

    private static readonly CellPoint[,] WallKickDataCounterClockwise =
    {
        { new(0, 0), new(+1, 0), new(+1, +1), new(0, -2), new(+1, -2) },
        { new(0, 0), new(+1, 0), new(+1, -1), new(0, +2), new(+1, +2) },
        { new(0, 0), new(-1, 0), new(-1, +1), new(0, -2), new(-1, -2) },
        { new(0, 0), new(-1, 0), new(-1, -1), new(0, +2), new(-1, +2) },
    };

    private static readonly CellPoint[,] WallKickDataICounterClockwise =
    {
        { new(0, 0), new(-1, 0), new(+2, 0), new(-1, +2), new(+2, -1) },
        { new(0, 0), new(+2, 0), new(-1, 0), new(+2, +1), new(-1, -2) },
        { new(0, 0), new(+1, 0), new(-2, 0), new(+1, -2), new(-2, +1) },
        { new(0, 0), new(-2, 0), new(+1, 0), new(-2, -1), new(+1, +2) },
    };

    public int WallKickChecksCount => TestsCount;
    public int[,] this[TetraminoType type] => GetTetramino(type);

    public Block?[,] GetRotation(Tetramino tetramino, RotationDirection direction) =>
        direction == RotationDirection.Clockwise
            ? MatrixUtilities.RotateClockwise(tetramino.Blocks, Tetramino.Sizes[tetramino.Type])
            : MatrixUtilities.RotateCounterClockwise(tetramino.Blocks, Tetramino.Sizes[tetramino.Type]);

    public bool Rotate(Tetramino tetramino, RotationDirection direction, out int testIndex)
    {
        for (testIndex = 0; testIndex < TestsCount; testIndex++)
        {
            var offset = GetWallKick(tetramino.Type, testIndex, tetramino.CurrentRotation, direction);
            if (!IsRotationAvailable(tetramino, offset.X, offset.Y, direction))
            {
                continue;
            }

            tetramino.SetBlocks(GetRotation(tetramino, direction), offset.X, offset.Y);
            return true;
        }

        return false;
    }

    private bool IsRotationAvailable(Tetramino tetramino, int dx, int dy, RotationDirection direction)
    {
        var layout = GetRotation(tetramino, direction);
        for (var y = 0; y < TetrisGame.TetraminoSize; y++)
        {
            for (var x = 0; x < TetrisGame.TetraminoSize; x++)
            {
                if (layout[x, y] is null)
                {
                    continue;
                }

                var boardX = tetramino.X + x + dx;
                var boardY = tetramino.Y - y + dy;
                if (boardX >= TetrisGame.BoardWidth || boardY >= TetrisGame.BoardHeight || boardX < 0 || boardY < 0 ||
                    game.Board.HasBlock(boardX, boardY))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static CellPoint GetWallKick(TetraminoType type, int testIndex, int rotation, RotationDirection direction)
    {
        var clockwise = direction == RotationDirection.Clockwise;
        return type switch
        {
            TetraminoType.O => new CellPoint(0, 0),
            TetraminoType.I => clockwise
                ? WallKickDataI[rotation, testIndex]
                : WallKickDataICounterClockwise[rotation, testIndex],
            _ => clockwise
                ? WallKickData[rotation, testIndex]
                : WallKickDataCounterClockwise[rotation, testIndex],
        };
    }

    internal static int[,] GetTetramino(TetraminoType type) => type switch
    {
        TetraminoType.I => new int[,]
        {
            { 0, 1, 0, 0 },
            { 0, 1, 0, 0 },
            { 0, 1, 0, 0 },
            { 0, 1, 0, 0 },
        },
        TetraminoType.L => new int[,]
        {
            { 0, 2, 0, 0 },
            { 0, 2, 0, 0 },
            { 2, 2, 0, 0 },
            { 0, 0, 0, 0 },
        },
        TetraminoType.J => new int[,]
        {
            { 3, 3, 0, 0 },
            { 0, 3, 0, 0 },
            { 0, 3, 0, 0 },
            { 0, 0, 0, 0 },
        },
        TetraminoType.O => new int[,]
        {
            { 4, 4, 0, 0 },
            { 4, 4, 0, 0 },
            { 0, 0, 0, 0 },
            { 0, 0, 0, 0 },
        },
        TetraminoType.S => new int[,]
        {
            { 5, 0, 0, 0 },
            { 5, 5, 0, 0 },
            { 0, 5, 0, 0 },
            { 0, 0, 0, 0 },
        },
        TetraminoType.T => new int[,]
        {
            { 0, 6, 0, 0 },
            { 6, 6, 0, 0 },
            { 0, 6, 0, 0 },
            { 0, 0, 0, 0 },
        },
        TetraminoType.Z => new int[,]
        {
            { 0, 7, 0, 0 },
            { 7, 7, 0, 0 },
            { 7, 0, 0, 0 },
            { 0, 0, 0, 0 },
        },
        _ => new int[TetrisGame.TetraminoSize, TetrisGame.TetraminoSize],
    };
}
