using System.Globalization;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class ExpressionEvaluator(string expression)
{
    private readonly string _expression = expression.Replace(',', '.');
    private int _position;

    internal static bool TryEvaluate(string expression, out double value)
    {
        try
        {
            var parser = new ExpressionEvaluator(expression);
            value = parser.ParseExpression();
            parser.SkipWhitespace();
            return parser._position == parser._expression.Length && double.IsFinite(value);
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private double ParseExpression()
    {
        var value = ParseTerm();
        while (true)
        {
            SkipWhitespace();
            if (Take('+')) value += ParseTerm();
            else if (Take('-')) value -= ParseTerm();
            else return value;
        }
    }

    private double ParseTerm()
    {
        var value = ParseFactor();
        while (true)
        {
            SkipWhitespace();
            if (Take('*')) value *= ParseFactor();
            else if (Take('/')) value /= ParseFactor();
            else return value;
        }
    }

    private double ParseFactor()
    {
        SkipWhitespace();
        if (Take('+')) return ParseFactor();
        if (Take('-')) return -ParseFactor();
        if (Take('('))
        {
            var value = ParseExpression();
            SkipWhitespace();
            if (!Take(')')) throw new FormatException();
            return value;
        }

        var start = _position;
        while (_position < _expression.Length &&
               (char.IsDigit(_expression[_position]) || _expression[_position] == '.'))
        {
            _position++;
        }

        if (start == _position || !double.TryParse(
                _expression[start.._position],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number))
        {
            throw new FormatException();
        }

        return number;
    }

    private bool Take(char character)
    {
        if (_position >= _expression.Length || _expression[_position] != character)
        {
            return false;
        }

        _position++;
        return true;
    }

    private void SkipWhitespace()
    {
        while (_position < _expression.Length && char.IsWhiteSpace(_expression[_position]))
        {
            _position++;
        }
    }
}
