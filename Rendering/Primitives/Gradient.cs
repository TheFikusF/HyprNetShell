namespace HyprNetShell.Rendering.Primitives;

public sealed class Gradient
{
    public readonly record struct Stop(float Percent, Color Color);

    private readonly Stop[] _stops;

    public Gradient(IEnumerable<Stop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);

        _stops = stops
            .Select((stop, index) => (Stop: stop, Index: index))
            .OrderBy(item => item.Stop.Percent)
            .ThenBy(item => item.Index)
            .Select(item => item.Stop)
            .ToArray();

        if (_stops.Length == 0)
        {
            throw new ArgumentException("A gradient must contain at least one stop.", nameof(stops));
        }

        foreach (var stop in _stops)
        {
            if (!float.IsFinite(stop.Percent) || stop.Percent is < 0.0f or > 1.0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stops),
                    stop.Percent,
                    "Gradient stop positions must be finite values between 0 and 1.");
            }
        }

        Stops = Array.AsReadOnly(_stops);
    }

    public Gradient(params Stop[] stops)
        : this((IEnumerable<Stop>)stops)
    {
    }

    public IReadOnlyList<Stop> Stops { get; }

    public Color Evaluate(float position)
    {
        if (float.IsNaN(position))
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, "Position cannot be NaN.");
        }

        position = Math.Clamp(position, 0.0f, 1.0f);

        var upperIndex = UpperBound(position);
        if (upperIndex == 0)
        {
            return _stops[0].Color;
        }

        if (upperIndex == _stops.Length)
        {
            return _stops[^1].Color;
        }

        var lower = _stops[upperIndex - 1];
        var upper = _stops[upperIndex];
        var amount = (position - lower.Percent) / (upper.Percent - lower.Percent);
        return Color.Lerp(lower.Color, upper.Color, amount);
    }

    private int UpperBound(float position)
    {
        var low = 0;
        var high = _stops.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_stops[middle].Percent <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}