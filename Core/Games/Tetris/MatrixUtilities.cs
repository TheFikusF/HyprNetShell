namespace HyprNetShell.Core.Games.Tetris;

internal static class MatrixUtilities
{
    internal static T?[,] RotateCounterClockwise<T>(T?[,] matrix, int size)
    {
        var result = new T?[TetrisGame.TetraminoSize, TetrisGame.TetraminoSize];
        for (var i = 0; i < size; i++)
        {
            for (var j = 0; j < size; j++)
            {
                result[j, size - 1 - i] = matrix[i, j];
            }
        }
        return result;
    }

    internal static T?[,] RotateClockwise<T>(T?[,] matrix, int size)
    {
        var result = new T?[TetrisGame.TetraminoSize, TetrisGame.TetraminoSize];
        for (var i = 0; i < size; i++)
        {
            for (var j = 0; j < size; j++)
            {
                result[size - 1 - j, i] = matrix[i, j];
            }
        }
        return result;
    }
}
