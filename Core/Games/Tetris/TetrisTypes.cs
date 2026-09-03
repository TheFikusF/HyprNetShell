using System.Numerics;

namespace HyprNetShell.Core.Games.Tetris;

public enum TetraminoType
{
    L,
    I,
    O,
    Z,
    S,
    J,
    T,
}

public enum RotationDirection
{
    Clockwise,
    CounterClockwise,
}

public enum SpinType
{
    None,
    TSpin,
    TSpinElegant,
    TSpinMini,
}

public enum MoveType
{
    Move,
    Rotate,
}

public enum BlockLayer
{
    Board,
    Current,
    Ghost,
    Clearing,
}

public readonly record struct CellPoint(int X, int Y);

public readonly record struct LockResult(
    CellPoint Position,
    int LinesCleared,
    SpinType Spin,
    bool PerfectClear,
    int ConsecutiveDifficultClears);

public sealed class Block
{
    private float _animationElapsed;
    private float _animationDuration;
    private float _animationDelay;
    private AnimationKind _animation;

    internal Block(TetraminoType type, BlockLayer layer, float x, float y)
    {
        Type = type;
        Layer = layer;
        RenderPosition = new Vector2(x, y);
        TargetPosition = RenderPosition;
        Scale = 1f;
    }

    public TetraminoType Type { get; }
    public BlockLayer Layer { get; internal set; }
    public Vector2 RenderPosition { get; private set; }
    public Vector2 TargetPosition { get; internal set; }
    public float Scale { get; private set; }
    public bool IsVisible => !IsAnimationComplete;
    public bool IsAnimationComplete { get; private set; }

    internal void Appear(float duration)
    {
        _animation = AnimationKind.Appear;
        _animationElapsed = 0;
        _animationDuration = Math.Max(0, duration);
        _animationDelay = 0;
        Scale = duration <= 0 ? 1f : 0f;
        IsAnimationComplete = false;
    }

    internal void Pop(float duration, float delay = 0)
    {
        _animation = AnimationKind.Pop;
        _animationElapsed = 0;
        _animationDuration = Math.Max(0, duration);
        _animationDelay = Math.Max(0, delay);
        Layer = BlockLayer.Clearing;
        IsAnimationComplete = false;
    }

    internal void Process(float deltaTime, float decay)
    {
        RenderPosition = new Vector2(
            Rendering.Primitives.PrimitivesMath.LerpSmooth(RenderPosition.X, TargetPosition.X, decay, deltaTime),
            Rendering.Primitives.PrimitivesMath.LerpSmooth(RenderPosition.Y, TargetPosition.Y, decay, deltaTime));

        if (_animation == AnimationKind.None || IsAnimationComplete)
        {
            return;
        }

        _animationElapsed += deltaTime;
        if (_animationElapsed < _animationDelay)
        {
            return;
        }

        var elapsed = _animationElapsed - _animationDelay;
        var progress = _animationDuration <= 0 ? 1f : Math.Clamp(elapsed / _animationDuration, 0f, 1f);
        switch (_animation)
        {
            case AnimationKind.Appear:
                // DOTween's default ease is OutQuad.
                Scale = 1f - (1f - progress) * (1f - progress);
                if (progress >= 1f)
                {
                    Scale = 1f;
                    _animation = AnimationKind.None;
                }
                break;
            case AnimationKind.Pop:
                Scale = 1f - MathF.Sqrt(1f - (progress - 1f) * (progress - 1f));
                if (progress >= 1f)
                {
                    Scale = 0f;
                    IsAnimationComplete = true;
                }
                break;
        }
    }

    private enum AnimationKind
    {
        None,
        Appear,
        Pop,
    }
}
