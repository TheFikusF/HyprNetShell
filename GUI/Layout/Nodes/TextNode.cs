using System.Globalization;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public enum TextWrapping
{
    NoWrap,
    Wrap,
    Ellipsis,
}

public class TextNode : Node, IWidthBoundNode
{
    private int? _measuredWidth;
    private int? _measuredHeight;
    
    private int? _parentMaxWidth;

    public override int Width
    {
        get
        {
            if (_measuredWidth.HasValue)
            {
                return _measuredWidth.Value;
            }
            
            var lines = GetLines(Layout.Renderer);
            var contentWidth = lines.Count != 0
                ? lines.Max(line => Layout.Renderer.MeasureText(line, FontSize))
                : 0;
            var measuredWidth = (int)MathF.Ceiling(contentWidth + Style.Padding.Left + Style.Padding.Right);
            var maxWidth = EffectiveMaxWidth;
            _measuredWidth = maxWidth.HasValue ? Math.Min(measuredWidth, maxWidth.Value) : measuredWidth;
            return _measuredWidth.Value;
        }
    }

    public override int Height
    {
        get
        {
            if (_measuredHeight.HasValue)
            {
                return _measuredHeight.Value;
            }
            _measuredHeight = GetLines(Layout.Renderer).Count * LineHeight +
                              (int)MathF.Ceiling(Style.Padding.Top + Style.Padding.Bottom);
            return _measuredHeight.Value;
        }
    }

    private string Text { get; }
    private float FontSize { get; }
    private Color Color { get; }
    private int LineHeight => (int)MathF.Ceiling(FontSize);

    public int? MaxWidth { get; init; }
    public TextWrapping Wrapping { get; init; }
    public int? MaxLines { get; init; }
    public Color? ShadowColor { get; init; }
    public float ShadowDistance { get; init; }
    public bool AcceptsWidthBound => true;

    private int? EffectiveMaxWidth => (MaxWidth, _parentMaxWidth) switch
    {
        ({ } ownBound, { } parentBound) => Math.Min(ownBound, parentBound),
        ({ } ownBound, null) => ownBound,
        (null, { } parentBound) => parentBound,
        _ => null,
    };

    public TextNode(
        string text,
        float fontSize = 14.0f,
        Color? color = null,
        int? maxWidth = null,
        TextWrapping wrapping = TextWrapping.NoWrap,
        int? maxLines = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fontSize);
        if (maxWidth is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWidth));
        }

        if (maxLines is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines));
        }

        Text = text;
        FontSize = fontSize;
        Color = color ?? Color.White;
        MaxWidth = maxWidth;
        Wrapping = wrapping;
        MaxLines = maxLines;
    }

    public void SetMaxWidth(int maxWidth, bool stretch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxWidth);
        _parentMaxWidth = maxWidth;
    }

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        UpdateInteractionState(x, y);
        Layout.AddInputRegion(new Rect(x, y, Width, Height));

        var lines = GetLines(renderer);
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var textX = Style.Padding.Left + x;
            var textY = Style.Padding.Top + y + lineIndex * LineHeight + (int)(FontSize * 0.8f);

            if (ShadowColor.HasValue)
            {
                renderer.DrawText(
                    lines[lineIndex],
                    textX,
                    textY + (int)ShadowDistance,
                    FontSize,
                    ShadowColor.Value.PushOpacity(Opacity),
                    0);
            }

            renderer.DrawText(lines[lineIndex], textX, textY, FontSize, Color.PushOpacity(Opacity));
        }
    }

    private IReadOnlyList<string> GetLines(IRenderApi renderer)
    {
        var maxWidth = EffectiveMaxWidth;
        var availableWidth = maxWidth.HasValue
            ? Math.Max(0, maxWidth.Value - (int)MathF.Ceiling(Style.Padding.Left + Style.Padding.Right))
            : (int?)null;

        var lines = Wrapping switch
        {
            TextWrapping.Wrap when availableWidth.HasValue => WrapText(renderer, availableWidth.Value),
            TextWrapping.Ellipsis when availableWidth.HasValue =>
                [Ellipsize(renderer, CollapseToSingleLine(Text), availableWidth.Value)],
            TextWrapping.NoWrap when availableWidth.HasValue => NormalizeLines(Text)
                .Select(line => Truncate(renderer, line, availableWidth.Value))
                .ToArray(),
            _ => NormalizeLines(Text),
        };

        if (MaxLines is not { } maximumLines || lines.Count <= maximumLines)
        {
            return lines;
        }

        var visible = lines.Take(maximumLines).ToArray();
        visible[^1] = availableWidth.HasValue
            ? Ellipsize(renderer, visible[^1], availableWidth.Value, true)
            : visible[^1] + "…";
        return visible;
    }

    private IReadOnlyList<string> WrapText(IRenderApi renderer, int availableWidth)
    {
        if (availableWidth <= 0)
        {
            return [string.Empty];
        }

        var lines = new List<string>();
        var paragraphs = NormalizeLines(Text);
        foreach (var paragraph in paragraphs)
        {
            var words = paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var currentLine = string.Empty;
            foreach (var word in words)
            {
                var candidate = currentLine.Length == 0 ? word : $"{currentLine} {word}";
                if (Fits(renderer, candidate, availableWidth))
                {
                    currentLine = candidate;
                    continue;
                }

                if (currentLine.Length > 0)
                {
                    lines.Add(currentLine);
                }

                if (Fits(renderer, word, availableWidth))
                {
                    currentLine = word;
                    continue;
                }

                var pieces = BreakWord(renderer, word, availableWidth);
                lines.AddRange(pieces.Take(Math.Max(0, pieces.Count - 1)));
                currentLine = pieces[^1];
            }

            if (currentLine.Length > 0)
            {
                lines.Add(currentLine);
            }
        }

        return lines;
    }

    private IReadOnlyList<string> BreakWord(IRenderApi renderer, string word, int availableWidth)
    {
        var elements = GetTextElements(word);
        var pieces = new List<string>();
        var start = 0;
        while (start < elements.Count)
        {
            var count = FindFittingPrefix(renderer, elements, start, availableWidth);
            if (count == 0)
            {
                count = 1;
            }

            pieces.Add(string.Concat(elements.Skip(start).Take(count)));
            start += count;
        }

        return pieces;
    }

    private string Ellipsize(IRenderApi renderer, string text, int availableWidth, bool force = false)
    {
        const string ELLIPSIS = "…";
        if (!force && Fits(renderer, text, availableWidth))
        {
            return text;
        }

        if (!Fits(renderer, ELLIPSIS, availableWidth))
        {
            return string.Empty;
        }

        var elements = GetTextElements(text.TrimEnd());
        var low = 0;
        var high = elements.Count;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var candidate = string.Concat(elements.Take(middle)).TrimEnd() + ELLIPSIS;
            if (Fits(renderer, candidate, availableWidth))
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return string.Concat(elements.Take(low)).TrimEnd() + ELLIPSIS;
    }

    private string Truncate(IRenderApi renderer, string text, int availableWidth)
    {
        if (Fits(renderer, text, availableWidth))
        {
            return text;
        }

        var elements = GetTextElements(text);
        var count = FindFittingPrefix(renderer, elements, 0, availableWidth);
        return string.Concat(elements.Take(count));
    }

    private int FindFittingPrefix(
        IRenderApi renderer,
        IReadOnlyList<string> elements,
        int start,
        int availableWidth)
    {
        var low = 0;
        var high = elements.Count - start;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var candidate = string.Concat(elements.Skip(start).Take(middle));
            if (Fits(renderer, candidate, availableWidth))
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    private bool Fits(IRenderApi renderer, string text, int availableWidth) =>
        renderer.MeasureText(text, FontSize) <= availableWidth;

    private static IReadOnlyList<string> GetTextElements(string text)
    {
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }

        return elements;
    }

    private static string CollapseToSingleLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string[] NormalizeLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
