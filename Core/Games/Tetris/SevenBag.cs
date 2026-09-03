namespace HyprNetShell.Core.Games.Tetris;

internal interface IRandomGenerator
{
    TetraminoType Next { get; }
    TetraminoType Dequeue();
    TetraminoType? GetNext(int index);
    void Init();
}

internal sealed class SevenBag(Random random) : IRandomGenerator
{
    private static readonly TetraminoType[] DefaultSet =
    [
        TetraminoType.L,
        TetraminoType.I,
        TetraminoType.O,
        TetraminoType.Z,
        TetraminoType.S,
        TetraminoType.J,
        TetraminoType.T,
    ];

    private readonly Queue<TetraminoType> _queue = new();

    public TetraminoType Next => _queue.Peek();

    public TetraminoType Dequeue()
    {
        if (_queue.Count <= DefaultSet.Length)
        {
            EnqueueBag();
        }

        return _queue.Dequeue();
    }

    public TetraminoType? GetNext(int index) => _queue.ElementAt(index);

    public void Init()
    {
        _queue.Clear();
        EnqueueBag();
        EnqueueBag();
    }

    private void EnqueueBag()
    {
        var bag = DefaultSet.ToArray();
        random.Shuffle(bag);
        foreach (var type in bag)
        {
            _queue.Enqueue(type);
        }
    }
}
