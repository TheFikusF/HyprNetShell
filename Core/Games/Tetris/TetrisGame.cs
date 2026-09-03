using System.Numerics;

namespace HyprNetShell.Core.Games.Tetris;

public class TetrisGame
{
    public const int BoardWidth = 10;
    public const int BoardHeight = 20;
    public const int SpawnX = 3;
    public const int SpawnY = 19;
    public const int TetraminoSize = 4;
    public const float AnimationSpeed = 10f;
    public const float LockDelay = 0.5f;
    public const float AfterClearDelay = 0.3f;

    private readonly float _maxTime;
    private readonly Stats _stats;
    private readonly List<Block> _retiredBlocks = [];
    private readonly Tetramino _current;
    private readonly Tetramino _ghost;
    private bool _started;
    private bool _dropping;
    private int _usedHold;
    private float _currentDanger;
    private float _time;

    public TetrisGame(int startingLevel = 1, float maxTime = 20f, int? randomSeed = null)
    {
        _maxTime = maxTime;
        _stats = new Stats(Math.Clamp(startingLevel, 1, 29));
        RandomGenerator = new SevenBag(randomSeed is { } seed ? new Random(seed) : new Random());
        PieceQueue = new TetrisQueue(RandomGenerator);
        Board = new Board(this, BoardWidth, BoardHeight);
        RotationSystem = new Srs(this);
        GravityTimer = new Timer(GetTimer(startingLevel), repeat: true);
        LockTimer = new Timer(LockDelay);
        AfterClearTimer = new Timer(AfterClearDelay);
        _current = new Tetramino(this, BlockLayer.Current);
        _ghost = new Tetramino(this, BlockLayer.Ghost);
        GravityTimer.Completed += MakeStep;
        LockTimer.Completed += Lock;

        _stats.ScoreChanged += (score, difference) => ScoreChanged?.Invoke(score, difference);
        _stats.LevelChanged += level => LevelChanged?.Invoke(level);
        _stats.LinesChanged += lines => LinesChanged?.Invoke(lines);
        _stats.ComboChanged += combo => ComboChanged?.Invoke(combo);
    }

    public enum DropType
    {
        Soft,
        Hard,
    }

    internal Board Board { get; private set; }
    internal IRotationSystem RotationSystem { get; }
    internal IRandomGenerator RandomGenerator { get; }
    internal TetrisQueue PieceQueue { get; }
    internal Timer GravityTimer { get; }
    internal Timer LockTimer { get; }
    internal Timer AfterClearTimer { get; }

    /// <summary>The visual origin used when a queued piece enters the board, in board-space cells.</summary>
    public Vector2 NextSpawnPosition { get; set; } = new(BoardWidth + 3f, BoardHeight - 3f);

    /// <summary>The visual origin used when a held piece re-enters the board, in board-space cells.</summary>
    public Vector2 HoldSpawnPosition { get; set; } = new(-3f, BoardHeight - 3f);

    public int Score => _stats.Score;
    public int Level => _stats.Level;
    public int Lines => _stats.LinesCleared;
    public int Combo => _stats.Combo;
    public int ConsecutiveDifficultClears => _stats.ConsecutiveDifficultClears;
    public float TimeSpent => _stats.TimeSpent;
    public float CurrentTime => _time;
    public float CurrentTimer => GetTimer(Level);
    public float CurrentDanger => _currentDanger;
    public bool IsStarted => _started;
    public bool IsPaused { get; private set; }
    public bool IsGameOver { get; private set; }
    public bool IsClearing => AfterClearTimer.Started;
    public TetraminoType CurrentPiece => _current.Type;
    public TetraminoType NextPiece => PieceQueue.Next;
    public TetraminoType? HeldPiece => PieceQueue.Held;
    public int CurrentX => _current.X;
    public int CurrentY => _current.Y;
    public int CurrentRotation => _current.CurrentRotation;
    public int GhostY => _ghost.Y;
    public string Status => IsGameOver ? "Game over — press R to restart" : IsPaused ? "Paused" : "Arrow keys to play";

    public event Action<int, int>? ScoreChanged;
    public event Action<int>? LevelChanged;
    public event Action<int>? LinesChanged;
    public event Action<int>? ComboChanged;
    public event Action<TetraminoType>? PieceSpawned;
    public event Action<LockResult>? PieceLocked;
    public event Action? BoardChanged;
    public event Action? GameOver;

    public Block? GetBoardBlock(int x, int y) =>
        x >= 0 && x < BoardWidth && y >= 0 && y < BoardHeight ? Board.Get(x, y) : null;

    public TetraminoType? GetNextPiece(int index) => PieceQueue.GetNext(index);

    public IReadOnlyList<Block> RenderBlocks
    {
        get
        {
            var result = new List<Block>(BoardWidth * BoardHeight + 8 + _retiredBlocks.Count);
            result.AddRange(Board.Blocks);
            result.AddRange(_ghost.VisibleBlocks);
            result.AddRange(_current.VisibleBlocks);
            result.AddRange(_retiredBlocks);
            return result;
        }
    }

    public bool CurrentOccupies(int x, int y) => _current[x, y] is not null;
    public bool GhostOccupies(int x, int y) => _ghost[x, y] is not null;

    public static IReadOnlyList<CellPoint> Cells(TetraminoType type, int rotation = 0)
    {
        var source = Srs.GetTetramino(type);
        var matrix = new bool?[TetraminoSize, TetraminoSize];
        for (var y = 0; y < TetraminoSize; y++)
        {
            for (var x = 0; x < TetraminoSize; x++)
            {
                matrix[x, y] = source[x, y] == 0 ? null : true;
            }
        }

        for (var turn = 0; turn < ((rotation % 4) + 4) % 4; turn++)
        {
            matrix = MatrixUtilities.RotateClockwise(matrix, Tetramino.Sizes[type]);
        }

        var result = new List<CellPoint>(4);
        for (var y = 0; y < TetraminoSize; y++)
        {
            for (var x = 0; x < TetraminoSize; x++)
            {
                if (matrix[x, y] is not null)
                {
                    result.Add(new CellPoint(x, -y));
                }
            }
        }
        return result;
    }

    public void Restart()
    {
        if (_started)
        {
            _current.Clear();
            _ghost.Clear();
            Board.Clear(animate: true);
        }

        StopTimers();
        Board = new Board(this, BoardWidth, BoardHeight);
        PieceQueue.Init();
        _stats.Reset();
        _currentDanger = 0;
        _time = 0;
        _usedHold = 0;
        _dropping = false;
        IsPaused = false;
        IsGameOver = false;
        _started = true;

        GravityTimer.MaxTime = CurrentTimer;
        GravityTimer.Start();
        _current.GrabNew();
        UpdateGhostBlock();
        PieceSpawned?.Invoke(_current.Type);
        BoardChanged?.Invoke();
    }

    public void Update(float deltaTime)
    {
        if (deltaTime <= 0)
        {
            return;
        }

        // DOTween continued destruction tweens independently of the gameplay component.
        ProcessRetiredBlocks(deltaTime);
        if (!_started || IsPaused)
        {
            return;
        }

        _time += deltaTime;
        if (_maxTime > 1 && _time > _maxTime)
        {
            EndGame();
            return;
        }

        if (!AfterClearTimer.Started)
        {
            Board.Process(deltaTime);
        }
        _current.Process(deltaTime);
        _ghost.Process(deltaTime);

        AfterClearTimer.Process(deltaTime);
        if (AfterClearTimer.Started)
        {
            return;
        }

        GravityTimer.Process(deltaTime);
        LockTimer.Process(deltaTime);
        _stats.Process(deltaTime);
    }

    public void Pause()
    {
        if (_started && !IsGameOver)
        {
            IsPaused = true;
        }
    }

    public void Resume()
    {
        if (_started && !IsGameOver)
        {
            IsPaused = false;
        }
    }

    public void TogglePause()
    {
        if (_started && !IsGameOver)
        {
            IsPaused = !IsPaused;
        }
    }

    public bool Move(int dx, int dy = 0)
    {
        if (!CanReadInput)
        {
            return false;
        }

        var moved = _current.Move(dx, dy, false);
        _ghost.UpdateGhostPosition(_current);
        BoardChanged?.Invoke();
        return moved;
    }

    public bool Rotate(int direction) => Rotate(direction >= 0 ? RotationDirection.Clockwise : RotationDirection.CounterClockwise);

    public bool Rotate(RotationDirection direction)
    {
        if (!CanReadInput)
        {
            return false;
        }

        var rotated = _current.TryRotate(direction);
        BoardChanged?.Invoke();
        return rotated;
    }

    public bool Hold()
    {
        if (_usedHold >= PieceQueue.HoldSize || !CanReadInput)
        {
            return false;
        }

        _usedHold++;
        var held = PieceQueue.HoldPiece(_current.Type);
        var next = held ?? PieceQueue.Next;
        if (!Validate(next))
        {
            return false;
        }

        if (held is { } heldType)
        {
            _current.SetPiece(heldType, HoldSpawnPosition);
        }
        else
        {
            _current.SetPiece(PieceQueue.Dequeue(), NextSpawnPosition);
        }
        _current.ResetPosition();
        UpdateGhostBlock();
        PieceSpawned?.Invoke(_current.Type);
        BoardChanged?.Invoke();
        return true;
    }

    public int HardDrop()
    {
        if (!CanReadInput)
        {
            return 0;
        }

        _dropping = true;
        var amount = 0;
        while (_current.Move(0, -1, true))
        {
            amount++;
        }

        LockTimer.Resume();
        _stats.HandleDrop(DropType.Hard, amount);
        BoardChanged?.Invoke();
        return amount;
    }

    public bool SoftDrop()
    {
        if (!CanReadInput)
        {
            return false;
        }

        if (_current.Move(0, -1, true))
        {
            _stats.HandleDrop(DropType.Soft, 1);
            return true;
        }

        LockTimer.Resume();
        BoardChanged?.Invoke();
        return false;
    }

    public static float GetTimer(int level) => level switch
    {
        1 => 0.800f,
        2 => 0.717f,
        3 => 0.550f,
        4 => 0.467f,
        5 => 0.383f,
        6 => 0.300f,
        7 => 0.216f,
        8 => 0.133f,
        9 => 0.100f,
        < 13 => 0.083f,
        < 16 => 0.067f,
        < 17 => 0.050f,
        < 29 => 0.033f,
        _ => 0.017f,
    };

    internal void Retire(Block block, float duration, float delay = 0)
    {
        if (_retiredBlocks.Contains(block))
        {
            return;
        }
        block.Pop(duration, delay);
        _retiredBlocks.Add(block);
    }

    internal void UpdateGhostBlock() => _ghost.CopyCurrentAsGhost(_current);
    internal void RotateGhostBlock(RotationDirection direction) => _ghost.RotateGhost(_current, direction);

    private bool CanReadInput => _started && !_dropping && !AfterClearTimer.Started && !IsPaused && !IsGameOver;

    private void MakeStep()
    {
        if (!_current.Move(0, -1, true))
        {
            LockTimer.Resume();
        }
    }

    private void Lock()
    {
        var spin = Board.PlaceCurrent(_current);
        var (linesCleared, perfect) = Board.RemoveCompletedLines();
        _stats.HandleLinesClear(linesCleared, perfect, spin);

        var size = Tetramino.Sizes[_current.Type];
        var lockPosition = new CellPoint(_current.X + size / 2, _current.Y - size / 2);
        PieceLocked?.Invoke(new LockResult(
            lockPosition,
            linesCleared,
            spin,
            perfect,
            _stats.ConsecutiveDifficultClears));

        if (Validate(PieceQueue.Next))
        {
            _current.GrabNew();
            PieceSpawned?.Invoke(_current.Type);
            LockTimer.Reset();
            GravityTimer.MaxTime = CurrentTimer;
            GravityTimer.Start();
            _dropping = false;
            _usedHold = 0;
            if (linesCleared > 0)
            {
                AfterClearTimer.Start();
            }
        }

        CalculateDanger();
        BoardChanged?.Invoke();
    }

    private bool Validate(TetraminoType type)
    {
        if (Tetramino.CanFit(this, SpawnX, SpawnY, type))
        {
            return true;
        }

        EndGame();
        return false;
    }

    private void CalculateDanger()
    {
        for (var y = 0; y < BoardHeight; y++)
        {
            if (Board.AnyInRow(y))
            {
                _currentDanger = Math.Clamp((y - 12f) / 5f, 0f, 1f);
            }
        }
    }

    private void EndGame()
    {
        _started = false;
        IsGameOver = true;
        _current.Clear();
        _ghost.Clear();
        StopTimers();
        GameOver?.Invoke();
    }

    private void StopTimers()
    {
        LockTimer.Stop();
        GravityTimer.Stop();
        AfterClearTimer.Stop();
    }

    private void ProcessRetiredBlocks(float deltaTime)
    {
        var decay = AnimationSpeed * 2f / CurrentTimer;
        for (var index = _retiredBlocks.Count - 1; index >= 0; index--)
        {
            var block = _retiredBlocks[index];
            block.Process(deltaTime, decay);
            if (block.IsAnimationComplete)
            {
                _retiredBlocks.RemoveAt(index);
            }
        }
    }
}
