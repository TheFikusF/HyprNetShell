namespace HyprNetShell.Core.Games.Tetris;

internal sealed class Timer(float maxTime, bool repeat = false)
{
    private float _time;

    internal float Time => _time;
    internal float MaxTime { get; set; } = maxTime;
    internal bool Repeat { get; set; } = repeat;
    internal bool Started { get; private set; }
    internal event Action? Completed;

    internal void Resume() => Started = true;

    internal void Start()
    {
        _time = 0;
        Started = true;
    }

    internal void Stop() => Started = false;

    internal void Reset()
    {
        _time = 0;
        Started = false;
    }

    internal void Process(float deltaTime)
    {
        if (!Started)
        {
            return;
        }

        _time += deltaTime;
        if (_time < MaxTime)
        {
            return;
        }

        Completed?.Invoke();
        _time = 0;
        Started = Repeat;
    }
}
