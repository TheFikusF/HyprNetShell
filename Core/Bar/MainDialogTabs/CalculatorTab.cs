using System.Diagnostics;
using System.Globalization;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class CalculatorTab(Theme theme) : IMainDialogTab
{
    private string _expression = "";
    private string _result = "";

    public string Id => "calculator";
    public string Title => "Calculator";
    public SvgAsset Icon => Icons.Calculator;

    public void Activate()
    {
    }

    public void HandleTextInput(string text)
    {
        _expression += new string(text.Where(IsCalculatorCharacter).ToArray());
        _result = ExpressionEvaluator.TryEvaluate(_expression, out var value)
            ? value.ToString("G15", CultureInfo.InvariantCulture)
            : "Invalid expression";
    }

    public void HandleBackspace()
    {
        if (_expression.Length > 0)
        {
            _expression = MainDialogTabUi.RemoveLastTextElement(_expression);
            _result = ExpressionEvaluator.TryEvaluate(_expression, out var value)
                ? value.ToString("G15", CultureInfo.InvariantCulture)
                : "Invalid expression";
        }
    }

    public void MoveSelection(SelectionDirection direction)
    {
    }

    public void ActivateSelection()
    {
        if (!ExpressionEvaluator.TryEvaluate(_expression, out var value))
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
                Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
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
                            new TextNode(_expression.Length == 0 ? "0" : _expression, 24,
                                theme.Text.MutedColor),
                            new TextNode(_result.Length == 0 ? "=" : "= " + _result, 34,
                                theme.Text),
                            new TextNode("Press Enter to copy", 18,
                                theme.Text.MutedColor),
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

}
