using System.Collections.ObjectModel;
using System.Numerics;

namespace HyprNetShell.Core.Games.Tetris;

internal sealed class Tetramino(TetrisGame game, BlockLayer layer)
{
    internal static readonly ReadOnlyDictionary<TetraminoType, int> Sizes = new(
        new Dictionary<TetraminoType, int>
        {
            [TetraminoType.L] = 3,
            [TetraminoType.I] = 4,
            [TetraminoType.O] = 2,
            [TetraminoType.Z] = 3,
            [TetraminoType.S] = 3,
            [TetraminoType.J] = 3,
            [TetraminoType.T] = 3,
        });

    private Block?[,] _blocks = new Block?[TetrisGame.TetraminoSize, TetrisGame.TetraminoSize];
    private int _rotation;
    private int _lastRotationTestIndex;
    private int _x;
    private int _y;

    internal Block? this[int x, int y]
    {
        get
        {
            var localX = x - _x;
            var localY = _y - y;
            return localX < 0 || localY < 0 || localX >= TetrisGame.TetraminoSize || localY >= TetrisGame.TetraminoSize
                ? null
                : _blocks[localX, localY];
        }
        set
        {
            var localX = x - _x;
            var localY = _y - y;
            if (localX >= 0 && localY >= 0 && localX < TetrisGame.TetraminoSize && localY < TetrisGame.TetraminoSize)
            {
                _blocks[localX, localY] = value;
            }
        }
    }

    internal Block?[,] Blocks => _blocks;
    internal TetraminoType Type { get; private set; }
    internal MoveType LastMove { get; private set; }
    internal int CurrentRotation => _rotation;
    internal int LastRotationTestIndex => _lastRotationTestIndex;
    internal int X => _x;
    internal int Y => _y;

    internal IEnumerable<Block> VisibleBlocks
    {
        get
        {
            for (var y = 0; y < TetrisGame.TetraminoSize; y++)
            {
                for (var x = 0; x < TetrisGame.TetraminoSize; x++)
                {
                    if (_blocks[x, y] is { } block)
                    {
                        yield return block;
                    }
                }
            }
        }
    }

    internal static bool CanFit(TetrisGame game, int x, int y, TetraminoType type)
    {
        var matrix = game.RotationSystem[type];
        for (var localY = 0; localY < TetrisGame.TetraminoSize; localY++)
        {
            for (var localX = 0; localX < TetrisGame.TetraminoSize; localX++)
            {
                if (matrix[localX, localY] == 0)
                {
                    continue;
                }

                var boardX = x + localX;
                var boardY = y - localY;
                if (boardX >= TetrisGame.BoardWidth || boardX < 0 || boardY >= TetrisGame.BoardHeight || boardY < 0 ||
                    game.Board.HasBlock(boardX, boardY))
                {
                    return false;
                }
            }
        }
        return true;
    }

    internal void Process(float deltaTime)
    {
        var decay = TetrisGame.AnimationSpeed / game.CurrentTimer;
        for (var y = 0; y < TetrisGame.TetraminoSize; y++)
        {
            for (var x = 0; x < TetrisGame.TetraminoSize; x++)
            {
                if (_blocks[x, y] is not { } block)
                {
                    continue;
                }

                block.TargetPosition = new(_x + x, _y - y);
                block.Process(deltaTime, decay);
            }
        }
    }

    internal void GrabNew()
    {
        ResetPosition();
        SetPiece(game.PieceQueue.Dequeue(), game.NextSpawnPosition);
    }

    internal void SetPiece(TetraminoType type, Vector2? spawnPosition = null)
    {
        Clear();
        Type = type;
        _rotation = 0;
        var matrix = game.RotationSystem[type];
        for (var y = 0; y < TetrisGame.TetraminoSize; y++)
        {
            for (var x = 0; x < TetrisGame.TetraminoSize; x++)
            {
                if (matrix[x, y] == 0)
                {
                    continue;
                }

                var targetX = _x + x;
                var targetY = _y - y;
                var start = spawnPosition ?? new Vector2(targetX, targetY);
                var block = new Block(type, layer, start.X, start.Y)
                {
                    TargetPosition = new(targetX, targetY),
                };
                block.Appear(game.CurrentTimer / 3f);
                _blocks[x, y] = block;
            }
        }

        if (layer == BlockLayer.Current)
        {
            game.UpdateGhostBlock();
        }
    }

    internal void CopyCurrentAsGhost(Tetramino current)
    {
        Clear();
        Type = current.Type;
        _rotation = current.CurrentRotation;
        _x = current.X;
        _y = current.Y;
        for (var y = 0; y < TetrisGame.TetraminoSize; y++)
        {
            for (var x = 0; x < TetrisGame.TetraminoSize; x++)
            {
                if (current._blocks[x, y] is not { } source)
                {
                    continue;
                }

                var block = new Block(Type, BlockLayer.Ghost, source.RenderPosition.X, source.RenderPosition.Y);
                block.Appear(game.CurrentTimer / 3f);
                _blocks[x, y] = block;
            }
        }
        UpdateGhostPosition(current);
    }

    internal void RotateGhost(Tetramino current, RotationDirection direction)
    {
        _rotation = current.CurrentRotation;
        _blocks = game.RotationSystem.GetRotation(this, direction);
        UpdateGhostPosition(current);
    }

    internal void UpdateGhostPosition(Tetramino current)
    {
        _x = current.X;
        _y = current.Y;
        while (Move(0, -1, false))
        {
        }
    }

    internal bool Move(int dx, int dy, bool stopTimerOnSuccess)
    {
        if (!PretendMove(dx, dy))
        {
            return false;
        }

        _x += dx;
        _y += dy;
        LastMove = MoveType.Move;
        if (stopTimerOnSuccess || PretendMove(0, -1))
        {
            game.LockTimer.Stop();
        }
        return true;
    }

    internal bool TryRotate(RotationDirection direction)
    {
        if (!game.RotationSystem.Rotate(this, direction, out var testIndex))
        {
            return false;
        }

        CheckNextDrop();
        _rotation = direction == RotationDirection.Clockwise ? (_rotation + 1) % 4 : (_rotation + 3) % 4;
        game.RotateGhostBlock(direction);
        LastMove = MoveType.Rotate;
        _lastRotationTestIndex = testIndex;
        return true;
    }

    internal void SetBlocks(Block?[,] blocks, int dx, int dy)
    {
        _blocks = blocks;
        _x += dx;
        _y += dy;
    }

    internal void ResetPosition()
    {
        _x = TetrisGame.SpawnX;
        _y = TetrisGame.SpawnY;
    }

    internal void Clear()
    {
        for (var y = 0; y < TetrisGame.TetraminoSize; y++)
        {
            for (var x = 0; x < TetrisGame.TetraminoSize; x++)
            {
                if (_blocks[x, y] is { } block)
                {
                    game.Retire(block, 0.1f);
                    _blocks[x, y] = null;
                }
            }
        }
    }

    private bool PretendMove(int dx, int dy)
    {
        for (var y = 0; y < TetrisGame.TetraminoSize; y++)
        {
            for (var x = 0; x < TetrisGame.TetraminoSize; x++)
            {
                if (_blocks[x, y] is null)
                {
                    continue;
                }

                var boardX = _x + x + dx;
                var boardY = _y - y + dy;
                if (boardX >= TetrisGame.BoardWidth || boardX < 0 || boardY >= TetrisGame.BoardHeight || boardY < 0 ||
                    game.Board.HasBlock(boardX, boardY))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private void CheckNextDrop()
    {
        if (PretendMove(0, -1))
        {
            game.LockTimer.Stop();
        }
        else
        {
            game.LockTimer.Resume();
        }
    }
}
