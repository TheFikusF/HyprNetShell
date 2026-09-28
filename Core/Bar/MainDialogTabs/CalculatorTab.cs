
using System.Globalization;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class CalculatorTab(
    ClipboardHistoryService clipboard,
    TextInputCoordinator inputs,
    Theme theme) : IMainDialogTab
{
    private readonly TextInputCoordinator.Input _expressionInput = inputs.Create(
        "",
        "",
        "e.g. (12 + 4) * 3",
        4096,
        transform: FilterExpression,
        alwaysActive: true);
    private string _result = "";


    public string Id => "calculator";
    public string Title => "Calculator";
    public SvgAsset Icon => Icons.Calculator;

    public void Activate()
    {
        inputs.Configure(_expressionInput, UpdateResult);
        inputs.Activate(_expressionInput);
    }


    public void MoveSelection(SelectionDirection direction)
    {
    }

    public void ActivateSelection()
    {
        if (!ExpressionEvaluator.TryEvaluate(_expressionInput.Value, out var value))
        {
            _result = "Invalid expression";
            return;
        }

        _result = value.ToString("G15", CultureInfo.InvariantCulture);
        _ = clipboard.CopyTextAsync(_result);
    }

    public Node Draw() => new BoxNode
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = new Style { Spacing = 8 },
        Children =
        [
            MainDialogTabUi.BuildSectionHeader("Calculator", "Type an expression and press Enter"),
            inputs.Build(_expressionInput),
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
                            new TextNode(_expressionInput.Value.Length == 0 ? "0" : _expressionInput.Value, 24,
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

    private void UpdateResult(string expression)
    {
        _result = ExpressionEvaluator.TryEvaluate(expression, out var value)
            ? value.ToString("G15", CultureInfo.InvariantCulture)
            : "Invalid expression";
    }

    private static string FilterExpression(string expression) =>
        new(expression.Where(IsCalculatorCharacter).ToArray());

    private static bool IsCalculatorCharacter(char character) =>
        char.IsDigit(character) || character is '.' or ',' or '+' or '-' or '*' or '/' or '(' or ')' or ' ';


}
