namespace HyprNetShell.GUI.Helpers;

public class Ref<T> where T : struct
{
    private T _value;

    public T Value
    {
        get => _value;
        set => _value = value;
    }

    public Ref() => _value = default;
    public Ref(T value) => _value = value;

    public static implicit operator T(Ref<T> value) => value.Value;
    public static implicit operator Ref<T>(T value) => new(value);
}
