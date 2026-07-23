namespace HyprNetShell.GUI.Layout.Nodes;

public sealed class RefFloat(float value = 0.0f)
{
    public float Value { get; set; } = value;

    public static implicit operator float(RefFloat value) => value.Value;
    public static implicit operator RefFloat(float value) => new(value);
}
