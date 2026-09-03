namespace HyprNetShell.Core.Games.Tetris;

internal sealed class TetrisQueue(IRandomGenerator randomGenerator)
{
    private readonly Queue<TetraminoType> _holdQueue = new();

    internal TetraminoType Next => randomGenerator.Next;
    internal TetraminoType? Held => _holdQueue.Count == 0 ? null : _holdQueue.Peek();
    internal int HoldSize => DefaultTetris.GetHoldQueueSize();

    internal void Init()
    {
        _holdQueue.Clear();
        randomGenerator.Init();
    }

    internal TetraminoType Dequeue() => randomGenerator.Dequeue();
    internal TetraminoType? GetNext(int index) => randomGenerator.GetNext(index);

    internal TetraminoType? HoldPiece(TetraminoType type)
    {
        _holdQueue.Enqueue(type);
        return _holdQueue.Count <= HoldSize ? null : _holdQueue.Dequeue();
    }
}
