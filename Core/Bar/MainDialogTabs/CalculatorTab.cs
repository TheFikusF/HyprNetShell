using System.Diagnostics;
using System.Globalization;
using HyprNetShell.Core.Assets;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class CalculatorTab : IMainDialogTab
{
    private string _expression = "";
    private string _result = "";

    public string Title => "Calculator";
    public SvgAsset Icon => Icons.Calculator;

    public void Activate()
    {
    }

    public void HandleTextInput(string text)
    {
        _expression += new string(text.Where(IsCalculatorCharacter).ToArray());
        _result = ExpressionParser.TryEvaluate(_expression, out var value)
            ? value.ToString("G15", CultureInfo.InvariantCulture)
            : "Invalid expression";
    }

    public void HandleBackspace()
    {
        if (_expression.Length > 0)
        {
            _expression = MainDialogTabUi.RemoveLastTextElement(_expression);
            _result = ExpressionParser.TryEvaluate(_expression, out var value)
                ? value.ToString("G15", CultureInfo.InvariantCulture)
                : "Invalid expression";
        }
    }

    public void MoveSelection(SelectionDirection direction)
    {
    }

    public void ActivateSelection()
    {
        if (!ExpressionParser.TryEvaluate(_expression, out var value))
        {
            _result = "Invalid expression";
            return;
        }

        _result = value.ToString("G15", CultureInfo.InvariantCulture);
        var result = _result;
        _ = Task.Run(() => CopyToClipboard(result));
    }

    public Node Draw() => new BoxNode
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = new Style { Spacing = 8 },
        Children =
        [
            MainDialogTabUi.BuildSectionHeader("Calculator", "Type an expression and press Enter"),
            MainDialogTabUi.BuildInput(_expression, "e.g. (12 + 4) * 3"),
            new BoxNode
            {
                VerticalAlignment = ItemsAlignment.Center,
                HorizontalAlignment = ItemsAlignment.Spread,
                Style = ModulesCommon.ModuleStyle(Theme.Default, Theme.Default.Panel) with
                {
                    BorderRadius = 8,
                    Padding = 24,
                    Spacing = 10,
                },
                Children =
                [
                    new ImageNode(Icons.Calculator, 32, 32, Color.White),
                    new BoxNode(height: 180)
                    {
                        Direction = Direction.Vertical,
                        HorizontalAlignment = ItemsAlignment.End,
                        VerticalAlignment = ItemsAlignment.Center,
                        Style = new Style { Spacing = 10 },
                        Children =
                        [
                            new TextNode(_expression.Length == 0 ? "0" : MainDialogTabUi.Trim(_expression, 64), 24,
                                Theme.Default.Muted),
                            new TextNode(_result.Length == 0 ? "=" : "= " + MainDialogTabUi.Trim(_result, 48), 34,
                                Theme.Default.Text),
                            new TextNode("Press Enter to copy", 18,
                                Theme.Default.Muted),
                        ],
                    },
                ]
            }
        ],
    };

    private static bool IsCalculatorCharacter(char character) =>
        char.IsDigit(character) || character is '.' or ',' or '+' or '-' or '*' or '/' or '(' or ')' or ' ';

    private static void CopyToClipboard(string text)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "wl-copy",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return;
            }

            process.StandardInput.Write(text);
            process.StandardInput.Close();
            process.WaitForExit(800);
        }
        catch
        {
            // wl-copy is optional; calculator evaluation still succeeds without it.
        }
    }

    private sealed class ExpressionParser(string expression)
    {
        private readonly string _expression = expression;
        private int _position;

        public static bool TryEvaluate(string expression, out double value)
        {
            try
            {
                var parser = new ExpressionParser(expression.Replace(',', '.'));
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
            if (_position >= _expression.Length || _expression[_position] != character) return false;
            _position++;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_position < _expression.Length && char.IsWhiteSpace(_expression[_position])) _position++;
        }
    }
}
