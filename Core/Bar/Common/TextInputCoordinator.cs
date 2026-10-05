using HyprNetShell.GUI;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Features.System;

using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Common;

internal sealed class TextInputCoordinator(ClipboardHistoryService clipboard)
{
    internal sealed class Input(
        string label,
        string value,
        string placeholder,
        int maximumLength,
        Action<string>? valueChanged,
        Func<string, string>? transform,
        bool clearOnEscape,
        bool alwaysActive,
        bool pasteReplacesValue,
        float? textSize)
    {
        internal string Label { get; } = label;
        internal string Value { get; set; } = value;
        internal string Placeholder { get; } = placeholder;
        internal int MaximumLength { get; } = maximumLength;
        internal Action<string>? ValueChanged { get; set; } = valueChanged;
        internal Func<string, string>? Transform { get; } = transform;
        internal Func<string, bool>? Submit { get; set; }
        internal bool ClearOnEscape { get; } = clearOnEscape;
        internal bool AlwaysActive { get; } = alwaysActive;
        internal bool PasteReplacesValue { get; } = pasteReplacesValue;
        internal float? TextSize { get; } = textSize;
    }

    private Input? _activeInput;

    internal bool HasActiveInput => _activeInput is not null;

    internal Input Create(
        string label,
        string value,
        string placeholder,
        int maximumLength,
        Action<string>? valueChanged = null,
        Func<string, string>? transform = null,
        bool clearOnEscape = false,
        bool alwaysActive = false,
        bool pasteReplacesValue = false,
        float? textSize = null) =>
        new(
            label,
            value,
            placeholder,
            maximumLength,
            valueChanged,
            transform,
            clearOnEscape,
            alwaysActive,
            pasteReplacesValue,
            textSize);

    internal bool IsActive(Input input) => ReferenceEquals(_activeInput, input);

    internal void SetValue(Input input, string value, bool notify = false)
    {
        input.Value = NormalizeValue(input, value);
        if (notify)
        {
            input.ValueChanged?.Invoke(input.Value);
        }
    }

    internal void Configure(
        Input input,
        Action<string>? valueChanged = null,
        Func<string, bool>? submit = null)
    {
        input.ValueChanged = valueChanged;
        input.Submit = submit;
    }

    internal void Activate(Input input)
    {
        _activeInput = input;
    }

    internal BoxNode Build(Input input)
    {
        var active = ReferenceEquals(_activeInput, input);
        var hasLabel = input.Label.Length > 0;
        var caret = active && Math.Sin(Environment.TickCount64 / 200.0) > 0 ? "|" : "";
        var displayedValue = input.Value.Length == 0
            ? input.Placeholder
            : active ? input.Value + caret : input.Value;
        var inputColor = active || input.Value.Length > 0 ? ThemeManager.Current.Text : ThemeManager.Current.Text.MutedColor;
        var background = active && !input.AlwaysActive ? ThemeManager.Current.Active : ThemeManager.Current.Panel;
        if (hasLabel)
        {
            return new BoxNode
            {
                Direction = Direction.Vertical,
                HorizontalAlignment = ItemsAlignment.Stretch,
                VerticalAlignment = ItemsAlignment.Start,
                OnClick = () => _activeInput = input,
                Style = ModulesCommon.ModuleStyle(background) with
                {
                    Padding = 12,
                    BorderRadius = 8,
                    BorderWidth = active && !input.AlwaysActive ? ThemeManager.Current.Border.Width : 0,
                    Spacing = 5,
                },
                Children =
                [
                    new TextNode(input.Label, ThemeManager.Current.Text.SmallSize, active ? ThemeManager.Current.Text : ThemeManager.Current.Text.MutedColor),
                    new TextNode(displayedValue, input.TextSize ?? ThemeManager.Current.Text.Size, inputColor),
                ],
            };
        }

        return new BoxNode(height: 46)
        {
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => _activeInput = input,
            Style = ModulesCommon.ModuleStyle(background) with
            {
                Padding = new Insets(ThemeManager.Current.Text.Size, 8),
                BorderRadius = 8,
                BorderWidth = active && !input.AlwaysActive ? ThemeManager.Current.Border.Width : 0,
            },
            Children = [new TextNode(displayedValue, input.TextSize ?? 16, inputColor)],
        };
    }

    internal bool HandleKey(DialogKey key)
    {
        if (_activeInput is null || key != DialogKey.PhysicalV)
        {
            return false;
        }

        _ = PasteAsync(_activeInput);
        return true;
    }

    internal bool HandleTextInput(string text)
    {
        if (_activeInput is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(text))
        {
            SetActiveValue(_activeInput.Value + text);
        }

        return true;
    }

    internal bool HandleBackspace(bool deleteWord)
    {
        if (_activeInput is null)
        {
            return false;
        }

        if (_activeInput.Value.Length > 0)
        {
            SetActiveValue(deleteWord
                ? RemoveLastWord(_activeInput.Value)
                : MainDialogTabUi.RemoveLastTextElement(_activeInput.Value));
        }

        return true;
    }

    internal bool HandleEnter()
    {
        if (_activeInput is null)
        {
            return false;
        }

        if (_activeInput.Submit is not null)
        {
            if (!_activeInput.Submit(_activeInput.Value))
            {
                return true;
            }
        }
        else if (_activeInput.AlwaysActive)
        {
            return false;
        }

        _activeInput = null;
        return true;
    }

    internal bool HandleEscape()
    {
        if (_activeInput is null || _activeInput.AlwaysActive)
        {
            return false;
        }

        if (_activeInput.ClearOnEscape)
        {
            SetActiveValue("");
        }

        _activeInput = null;
        return true;
    }

    internal void Deactivate()
    {
        _activeInput = null;
    }

    private async Task PasteAsync(Input input)
    {
        var text = await clipboard.ReadTextAsync();
        if (string.IsNullOrEmpty(text) || !ReferenceEquals(_activeInput, input))
        {
            return;
        }

        SetActiveValue(input.PasteReplacesValue ? text : input.Value + text);
    }

    private void SetActiveValue(string value)
    {
        if (_activeInput is null)
        {
            return;
        }

        _activeInput.Value = NormalizeValue(_activeInput, value);
        _activeInput.ValueChanged?.Invoke(_activeInput.Value);
    }

    private static string NormalizeValue(Input input, string value)
    {
        value = input.Transform?.Invoke(value) ?? value;
        return value.Length <= input.MaximumLength ? value : value[..input.MaximumLength];
    }

    private static string RemoveLastWord(string value)
    {
        var index = value.Length;
        while (index > 0 && char.IsWhiteSpace(value[index - 1]))
        {
            index--;
        }

        while (index > 0 && !char.IsWhiteSpace(value[index - 1]))
        {
            index--;
        }

        return value[..index];
    }
}
