namespace HyprNetShell.Core.Games.Minesweeper;

internal sealed class MinesweeperGame
{
    internal const int BoardWidth = 30;
    internal const int BoardHeight = 16;
    internal const int MineCount = 99;
    private readonly bool[,] _mines = new bool[BoardWidth, BoardHeight];
    private readonly bool[,] _revealed = new bool[BoardWidth, BoardHeight];
    private readonly bool[,] _flags = new bool[BoardWidth, BoardHeight];
    private bool _started;
    internal int SelectedX { get; private set; }
    internal int SelectedY { get; private set; }
    internal bool Lost { get; private set; }
    internal bool Won { get; private set; }
    internal int Flags { get; private set; }
    internal double Seconds { get; private set; }
    internal bool Finished => Lost || Won;
    internal string Status => Lost ? "Mine hit! Start a new game." : Won ? "All clear — you won!" : "Find all the safe squares";

    internal bool IsMine(int x, int y) => _mines[x, y];
    internal bool IsRevealed(int x, int y) => _revealed[x, y];
    internal bool IsFlagged(int x, int y) => _flags[x, y];
    internal int Adjacent(int x, int y) => Neighbors(x, y).Count(p => _mines[p.X, p.Y]);
    internal void Select(int x, int y)
    {
        SelectedX = Math.Clamp(x, 0, BoardWidth - 1);
        SelectedY = Math.Clamp(y, 0, BoardHeight - 1);
    }
    internal void Move(int dx, int dy) => Select(SelectedX + dx, SelectedY + dy);
    internal void Update(double delta)
    {
        if (_started && !Finished) Seconds += Math.Max(0, delta);
    }
    internal void Restart()
    {
        Array.Clear(_mines);
        Array.Clear(_revealed);
        Array.Clear(_flags);
        _started = Lost = Won = false;
        Flags = 0;
        Seconds = 0;
        Select(0, 0);
    }
    internal void ToggleFlag()
    {
        if (Finished || _revealed[SelectedX, SelectedY]) return;
        _flags[SelectedX, SelectedY] = !_flags[SelectedX, SelectedY];
        Flags += _flags[SelectedX, SelectedY] ? 1 : -1;
    }
    internal void Reveal()
    {
        if (Finished || _flags[SelectedX, SelectedY]) return;
        if (!_started)
        {
            // Keep the first square and its neighbors clear so every opening is useful.
            var candidates = new List<(int X, int Y)>();
            for (var y = 0; y < BoardHeight; y++)
                for (var x = 0; x < BoardWidth; x++)
                    if (Math.Abs(x - SelectedX) > 1 || Math.Abs(y - SelectedY) > 1)
                        candidates.Add((x, y));
            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(candidates));
            foreach (var cell in candidates.Take(MineCount)) _mines[cell.X, cell.Y] = true;
            _started = true;
        }
        if (_revealed[SelectedX, SelectedY])
        {
            var neighbors = Neighbors(SelectedX, SelectedY).ToArray();
            if (neighbors.Count(p => _flags[p.X, p.Y]) != Adjacent(SelectedX, SelectedY)) return;
            foreach (var cell in neighbors)
            {
                RevealCell(cell.X, cell.Y);
                if (Lost) break;
            }
        }
        else RevealCell(SelectedX, SelectedY);
        var safe = 0;
        for (var y = 0; y < BoardHeight; y++)
            for (var x = 0; x < BoardWidth; x++)
                if (_revealed[x, y] && !_mines[x, y]) safe++;
        Won = !Lost && safe == BoardWidth * BoardHeight - MineCount;
    }
    private void RevealCell(int x, int y)
    {
        var pending = new Queue<(int X, int Y)>();
        pending.Enqueue((x, y));
        while (pending.TryDequeue(out var cell))
        {
            if (_revealed[cell.X, cell.Y] || _flags[cell.X, cell.Y]) continue;
            _revealed[cell.X, cell.Y] = true;
            if (_mines[cell.X, cell.Y]) { Lost = true; return; }
            if (Adjacent(cell.X, cell.Y) == 0)
                foreach (var neighbor in Neighbors(cell.X, cell.Y)) pending.Enqueue(neighbor);
        }
    }
    private static IEnumerable<(int X, int Y)> Neighbors(int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if ((dx != 0 || dy != 0) && x + dx >= 0 && x + dx < BoardWidth && y + dy >= 0 && y + dy < BoardHeight)
                    yield return (x + dx, y + dy);
    }
}
