using System.Runtime.CompilerServices;

namespace HyprNetShell.Core.Games.Tetris;

internal sealed class Board(TetrisGame game, int width, int height)
{
    private readonly Block?[,] _cells = new Block?[width, height];

    internal Block? Get(int x, int y) => _cells[x, y];
    internal void Set(int x, int y, Block? value) => _cells[x, y] = value;
    internal bool HasBlock(int x, int y) => _cells[x, y] is not null;

    internal bool HasBlockInBounds(int x, int y) =>
        x < 0 || x >= width || y < 0 || y >= height || _cells[x, y] is not null;

    internal IEnumerable<Block> Blocks
    {
        get
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (_cells[x, y] is { } block)
                    {
                        yield return block;
                    }
                }
            }
        }
    }

    internal void Process(float deltaTime)
    {
        var decay = TetrisGame.AnimationSpeed * 2f / game.CurrentTimer;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (_cells[x, y] is not { } block)
                {
                    continue;
                }

                block.TargetPosition = new(x, y);
                block.Process(deltaTime, decay);
            }
        }
    }

    internal SpinType PlaceCurrent(Tetramino current)
    {
        var spin = DetectSpin(current);
        var size = Tetramino.Sizes[current.Type];
        for (var y = current.Y - size + 1; y < current.Y + 1; y++)
        {
            for (var x = current.X; x < current.X + size; x++)
            {
                if (current[x, y] is not { } block)
                {
                    continue;
                }

                block.Layer = BlockLayer.Board;
                _cells[x, y] = block;
                current[x, y] = null;
            }
        }
        return spin;
    }

    internal (int LinesCleared, bool Perfect) RemoveCompletedLines()
    {
        var rows = new List<int>();
        for (var y = 0; y < height; y++)
        {
            if (IsRowFull(y))
            {
                rows.Add(y);
            }
        }
        return RemoveLines(rows);
    }

    internal bool AnyInRow(int y)
    {
        for (var x = 0; x < width; x++)
        {
            if (_cells[x, y] is not null)
            {
                return true;
            }
        }
        return false;
    }

    internal void Clear(bool animate)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (_cells[x, y] is not { } block)
                {
                    continue;
                }

                if (animate)
                {
                    game.Retire(block, 0.0333f);
                }
                _cells[x, y] = null;
            }
        }
    }

    private SpinType DetectSpin(Tetramino current)
    {
        if (current.Type != TetraminoType.T || current.LastMove != MoveType.Rotate)
        {
            return SpinType.None;
        }

        var minX = current.X;
        var minY = current.Y - Tetramino.Sizes[current.Type] + 1;
        var maxX = current.X + Tetramino.Sizes[current.Type] - 1;
        var maxY = current.Y;
        var filledCorners = 0;
        var consecutiveChecks = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void TestBoard(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height || _cells[x, y] is not null)
            {
                filledCorners++;
                consecutiveChecks++;
            }
            else if (consecutiveChecks < 5)
            {
                consecutiveChecks = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void TestTetramino(int x, int y)
        {
            if (current[x, y] is not null)
            {
                consecutiveChecks++;
            }
            else if (consecutiveChecks < 5)
            {
                consecutiveChecks = 0;
            }
        }

        TestTetramino(minX, minY + 1);
        TestBoard(minX, minY);
        TestTetramino(minX + 1, minY);
        TestBoard(maxX, minY);
        TestTetramino(maxX, minY + 1);
        TestBoard(maxX, maxY);
        TestTetramino(minX + 1, maxY);
        TestBoard(minX, maxY);
        TestTetramino(minX, minY + 1);

        if (filledCorners < 3)
        {
            return SpinType.None;
        }

        return consecutiveChecks >= 5 || current.LastRotationTestIndex == game.RotationSystem.WallKickChecksCount - 1
            ? SpinType.TSpin
            : SpinType.TSpinMini;
    }

    private (int LinesCleared, bool Perfect) RemoveLines(IReadOnlyList<int> rows)
    {
        if (rows.Count == 0)
        {
            return (0, IsEmpty());
        }

        var cleared = new bool[height];
        foreach (var row in rows)
        {
            if (row >= 0 && row < height && IsRowFull(row))
            {
                cleared[row] = true;
            }
        }

        var duration = game.CurrentTimer / 3f / 5f;
        for (var y = 0; y < height; y++)
        {
            if (!cleared[y])
            {
                continue;
            }

            for (var x = 0; x < width; x++)
            {
                if (_cells[x, y] is { } block)
                {
                    // The original sequence appends each block tween, while all cleared rows run in parallel.
                    game.Retire(block, duration, x * duration);
                    _cells[x, y] = null;
                }
            }
        }

        Collapse(cleared);
        return (cleared.Count(value => value), IsEmpty());
    }

    private void Collapse(bool[] cleared)
    {
        var writeY = 0;
        for (var y = 0; y < height; y++)
        {
            if (cleared[y])
            {
                continue;
            }

            if (writeY != y)
            {
                for (var x = 0; x < width; x++)
                {
                    _cells[x, writeY] = _cells[x, y];
                    _cells[x, y] = null;
                }
            }
            writeY++;
        }

        for (var y = writeY; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                _cells[x, y] = null;
            }
        }
    }

    private bool IsRowFull(int y)
    {
        for (var x = 0; x < width; x++)
        {
            if (_cells[x, y] is null)
            {
                return false;
            }
        }
        return true;
    }

    private bool IsEmpty()
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (_cells[x, y] is not null)
                {
                    return false;
                }
            }
        }
        return true;
    }
}
