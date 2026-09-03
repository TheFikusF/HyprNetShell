namespace HyprNetShell.Core.Games.Tetris;

internal sealed class Stats(int startingLevel)
{
    private int _linesCleared;
    private int _score;
    private int _combo;
    private float _timeSpent;
    private int _consecutiveDifficultClears;

    internal int Score => _score;
    internal int Level { get; private set; }
    internal int LinesCleared => _linesCleared;
    internal int Combo => _combo;
    internal float TimeSpent => _timeSpent;
    internal int ConsecutiveDifficultClears => _consecutiveDifficultClears;

    internal event Action<int, int>? ScoreChanged;
    internal event Action<int>? LevelChanged;
    internal event Action<int>? LinesChanged;
    internal event Action<int>? ComboChanged;

    internal void Reset()
    {
        _timeSpent = 0;
        _linesCleared = 0;
        _score = 0;
        _combo = 0;
        _consecutiveDifficultClears = 0;
        Level = Math.Max(1, startingLevel);
        LevelChanged?.Invoke(Level);
        ScoreChanged?.Invoke(0, 0);
        LinesChanged?.Invoke(0);
        ComboChanged?.Invoke(0);
    }

    internal void Process(float deltaTime) => _timeSpent += deltaTime;

    internal void HandleLinesClear(int amount, bool perfect, SpinType spin = SpinType.None)
    {
        if (amount <= 0)
        {
            _combo = 0;
            ComboChanged?.Invoke(_combo);
            return;
        }

        var previousLines = _linesCleared;
        _linesCleared += amount;
        var baseScore = DefaultTetris.GetLineClearScore(amount, perfect);
        var spinBonus = GetSpinBonus(spin, amount);

        if (spin != SpinType.None && amount > 0 || amount >= 4)
        {
            _consecutiveDifficultClears++;
        }
        else if (amount > 0)
        {
            _consecutiveDifficultClears = 0;
        }

        var reward = (baseScore + spinBonus + _combo * DefaultTetris.GetComboMultiplier()) * Level;
        if (_consecutiveDifficultClears >= 2)
        {
            reward = (int)MathF.Round(reward * 1.5f, MidpointRounding.AwayFromZero);
        }
        AddScore(reward);

        for (var i = previousLines / 10; i < _linesCleared / 10; i++)
        {
            Level++;
            LevelChanged?.Invoke(Level);
        }

        LinesChanged?.Invoke(_linesCleared);
        _combo++;
        ComboChanged?.Invoke(_combo);
    }

    internal void HandleDrop(TetrisGame.DropType type, int amount)
    {
        if (amount > 0)
        {
            AddScore(type == TetrisGame.DropType.Soft ? amount : amount * 2);
        }
    }

    private void AddScore(int amount)
    {
        var previous = _score;
        _score += amount;
        // Preserves the original event's previous-minus-new difference sign.
        ScoreChanged?.Invoke(_score, previous - _score);
    }



    private static int GetSpinBonus(SpinType spin, int linesCleared) => spin switch
    {
        SpinType.TSpinMini when linesCleared == 0 => 100,
        SpinType.TSpin => (linesCleared + 1) * 400,
        SpinType.TSpinElegant => (linesCleared + 1) * 600,
        SpinType.TSpinMini => (linesCleared + 1) * 200,
        _ => 0,
    };
}
