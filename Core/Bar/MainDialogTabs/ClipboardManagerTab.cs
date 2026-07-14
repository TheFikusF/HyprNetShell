using System.Diagnostics;
using HyprNetShell.Core.Assets;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class ClipboardManagerTab(Action closeDialog) : IMainDialogTab
{
    private IReadOnlyList<ClipboardEntry> _entries = [];
    private IReadOnlyList<ClipboardEntry> _filteredEntries = [];
    private string _query = "";
    private int _firstIndex;
    private int _selectedIndex;

    public string Title => "Clipboard";
    public SvgAsset Icon => Icons.Clipboard;

    public void Activate()
    {
        _entries = LoadEntries();
        ApplyFilter();
    }

    public void HandleTextInput(string text)
    {
        _query += text;
        ApplyFilter();
    }

    public void HandleBackspace()
    {
        if (_query.Length > 0)
        {
            _query = MainDialogTabUi.RemoveLastTextElement(_query);
            ApplyFilter();
        }
    }

    public void MoveSelection(SelectionDirection direction)
    {
        if (direction is SelectionDirection.Up or SelectionDirection.Down)
        {
            MainDialogTabUi.MoveSelection(
                ref _selectedIndex,
                ref _firstIndex,
                direction == SelectionDirection.Up ? -1 : 1,
                _filteredEntries.Count);
        }
    }

    public void ActivateSelection()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _filteredEntries.Count)
        {
            return;
        }

        _ = Task.Run(() => CopyEntryAsync(_filteredEntries[_selectedIndex].Id));
        closeDialog();
    }

    public Node Draw()
    {
        var visibleEntries = _filteredEntries
            .Skip(_firstIndex)
            .Take(MainDialogTabUi.VisibleRowCount)
            .ToArray();
        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 8 },
            Children =
            [
                MainDialogTabUi.BuildSectionHeader(
                    "Clipboard",
                    MainDialogTabUi.ResultCount(_selectedIndex, _filteredEntries.Count, "Clipboard history is empty")),
                MainDialogTabUi.BuildInput(_query, "Search clipboard history..."),
                ..visibleEntries.Select((entry, visibleIndex) => BuildRow(entry, _firstIndex + visibleIndex)),
            ],
        };
    }

    private Node BuildRow(ClipboardEntry entry, int index)
    {
        var selected = index == _selectedIndex;
        return new BoxNode(height: 66)
        {
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () =>
            {
                _selectedIndex = index;
                ActivateSelection();
            },
            Style = ModulesCommon.ModuleStyle(
                Theme.Default,
                selected ? Theme.Default.Active : Theme.Default.Panel) with
            {
                BorderRadius = 8,
                BorderWidth = selected ? Theme.Default.BorderWidth : 0,
                Padding = new Insets(16, 10),
                Spacing = 14,
            },
            Children =
            [
                new ImageNode(Icons.Copy, 30, 30, Theme.Default.Text),
                new TextNode(MainDialogTabUi.Trim(entry.Preview, 86), 15, Theme.Default.Text),
            ],
        };
    }

    private void ApplyFilter()
    {
        _filteredEntries = string.IsNullOrWhiteSpace(_query)
            ? _entries
            : _entries
                .Where(entry => entry.Preview.Contains(_query, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
        _firstIndex = 0;
        _selectedIndex = 0;
    }

    private static IReadOnlyList<ClipboardEntry> LoadEntries()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cliphist",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "list" },
            });
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1000) || process.ExitCode != 0)
            {
                return [];
            }

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseEntry)
                .Where(entry => entry is not null)
                .Cast<ClipboardEntry>()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static ClipboardEntry? ParseEntry(string line)
    {
        var separator = line.IndexOf('\t');
        return separator <= 0 || separator == line.Length - 1
            ? null
            : new ClipboardEntry(line[..separator], line[(separator + 1)..].Trim());
    }

    private static async Task CopyEntryAsync(string id)
    {
        try
        {
            using var decode = Process.Start(new ProcessStartInfo
            {
                FileName = "cliphist",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "decode", id },
            });
            using var copy = Process.Start(new ProcessStartInfo
            {
                FileName = "wl-copy",
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (decode is null || copy is null)
            {
                return;
            }

            await decode.StandardOutput.BaseStream.CopyToAsync(copy.StandardInput.BaseStream);
            copy.StandardInput.Close();
            await Task.WhenAll(decode.WaitForExitAsync(), copy.WaitForExitAsync());
        }
        catch
        {
            // Clipboard tools are optional; a failed copy should not crash the bar.
        }
    }

    private sealed record ClipboardEntry(string Id, string Preview);
}
