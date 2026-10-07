using System.Runtime.InteropServices;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class InfoConfigurationTab(StatusBarServices services) : IMainDialogTab, IDisposable
{
    private sealed record InfoGroup(string Title, SvgAsset Icon, IReadOnlyList<SystemInfoEntry> Entries);

    private const string REPOSITORY_URL = "https://github.com/TheFikusF/HyprNetShell";

    private readonly SystemInfoService _systemInfo = new();
    private readonly Dictionary<string, ModulesCommon.BoxState> _buttonStates = [];
    private int _selectedPage;


    public string Id => "info";
    public string Title => "Info";
    public SvgAsset Icon => Icons.Info;

    public void Activate()
    {
        _systemInfo.Refresh();
    }

    public void MoveSelection(SelectionDirection direction)
    {
        if (direction is SelectionDirection.Left or SelectionDirection.Right)
        {
            SelectPage(direction == SelectionDirection.Left ? 0 : 1);
        }


    }

    public void ActivateSelection()
    {
    }

    public Node Draw()
    {
        var groups = BuildGroups();
        return new BoxNode(new Style { Spacing = 12 }, ItemsAlignment.Stretch)
        {
            new BoxNode(245)
            {
                Direction = Direction.Vertical,
                HorizontalAlignment = ItemsAlignment.Stretch,
                Style = Style.Spacer,
                Children =
                [
                    BuildPageButton("System", "Hardware and session", 0),
                    BuildPageButton("About", "About HyprNetShell", 1),
                ],
            },
            new BoxNode(new Style { Spacing = 12 }, ItemsAlignment.Stretch)
            {
                Direction = Direction.Vertical,
                Children =
                [
                    MainDialogTabUi.BuildSectionHeader(
                        _selectedPage == 0 ? "System information" : "HyprNetShell",
                        _selectedPage == 0 && _systemInfo.Snapshot.IsRefreshing ? "Refreshing…" : ""),
                    new BoxNode(new Style { Spacing = 12 }, ItemsAlignment.Stretch)
                    {
                        Direction = Direction.Vertical,
                        Children = [.. groups.Where(group => group.Entries.Count > 0).Select(BuildGroup)],
                    },
                ],
            },
        };
    }

    public void Dispose()
    {
        _systemInfo.Dispose();
    }

    private void SelectPage(int page)
    {
        _selectedPage = page;

        if (page == 0)
        {
            _systemInfo.Refresh();
        }
    }

    private BoxNode BuildPageButton(string title, string subtitle, int index)
    {
        var selected = _selectedPage == index;
        var state = _buttonStates.GetState("page-" + index, ThemeManager.Current.Panel)
            .UpdateColor(selected ? ThemeManager.Current.Active : ThemeManager.Current.Panel);
        return new BoxNode
        {
            Direction = Direction.Vertical,
            IsHovered = state.Hovered,
            OnClick = () => SelectPage(index),
            Style = ModulesCommon.ModuleStyle(state.Background) with
            {
                Padding = 12,
                BorderRadius = 8,
                BorderWidth = selected ? ThemeManager.Current.Border.Width : 0,
                Spacing = 3,
            },
            Children =
            [
                new TextNode(title),
                new TextNode(subtitle, ThemeManager.Current.Text.SmallSize, selected ? ThemeManager.Current.Text : ThemeManager.Current.Text.MutedColor),
            ],
        };
    }

    private Node BuildGroup(InfoGroup group) => new BoxNode
    {
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Stretch,
        Style = Style.Spacer,
        Children =
        [
            new BoxNode(24)
            {
                Direction = Direction.Vertical,
                HorizontalAlignment = ItemsAlignment.Center,
                VerticalAlignment = ItemsAlignment.Stretch,
                Style = Style.Spacer,
                Children =
                [
                    new ImageNode(group.Icon, ThemeManager.Current.IconSize, ThemeManager.Current.IconSize, ThemeManager.Current.Active),
                    new BoxNode(2)
                    {
                        Style = new Style { BackgroundColor = ThemeManager.Current.Active },
                    },
                ],
            },
            new BoxNode
            {
                Direction = Direction.Vertical,
                HorizontalAlignment = ItemsAlignment.Stretch,
                Style = Style.Spacer,
                Children =
                [
                    new TextNode(group.Title, color: ThemeManager.Current.Active),
                    .. group.Entries.Select(BuildRow),
                ],
            },
        ],
    };

    private Node BuildRow(SystemInfoEntry entry)
    {
        var isRepository = entry.Label == "Repository";
        var state = isRepository
            ? _buttonStates.GetState("repository", ThemeManager.Current.Panel).UpdateColor(ThemeManager.Current.Panel)
            : null;
        return new BoxNode
        {
            VerticalAlignment = ItemsAlignment.Start,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 12 },
            Children =
            [
                new BoxNode(125)
                {
                    Children = [new TextNode(entry.Label, color: ThemeManager.Current.Text.MutedColor,
                        wrapping: TextWrapping.Wrap)],
                },
                new BoxNode
                {
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    IsHovered = state?.Hovered,
                    OnClick = isRepository ? () => services.UrlLauncher.TryOpen(REPOSITORY_URL) : null,
                    Children = [new TextNode(entry.Value,
                        color: isRepository ? ThemeManager.Current.Active : ThemeManager.Current.Text,
                        wrapping: TextWrapping.Wrap)],
                },
            ],
        };
    }

    private List<InfoGroup> BuildGroups()
    {
        var entries = BuildEntries();
        if (_selectedPage == 1)
        {
            return
            [
                new("Project", Icons.Info, entries.Where(entry => entry.Label is "Shell" or "Version" or "Repository" or "License").ToArray()),
                new("Technology", Icons.Monitor, entries.Where(entry => entry.Label is "Platform" or "Runtime" or "Rendering" or "Native layer").ToArray()),
                new("Paths", Icons.Settings, entries.Where(entry => entry.Label is "Configuration" or "Logs").ToArray()),
            ];
        }

        return
        [
            new("Operating system", Icons.Monitor, entries.Where(entry => entry.Label is "OS" or "Kernel" or "Architecture" or "Hostname" or "Uptime").ToArray()),
            new("Hardware", Icons.Laptop, entries.Where(entry => entry.Label.StartsWith("CPU", StringComparison.Ordinal)
                || entry.Label.StartsWith("Hardware", StringComparison.Ordinal) || entry.Label is "RAM" or "Swap").ToArray()),
            new("Session", Icons.Settings, entries.Where(entry => entry.Label is "Session" or "Desktop" or "Login shell").ToArray()),
        ];
    }

    private List<SystemInfoEntry> BuildEntries()
    {
        if (_selectedPage == 1)
        {
            return
            [
                new("Shell", "HyprNetShell"),
                new("Version", typeof(StatusBarServices).Assembly.GetName().Version?.ToString() ?? "Unknown"),
                new("Platform", "Linux · Hyprland · Wayland layer-shell"),
                new("Runtime", RuntimeInformation.FrameworkDescription),
                new("Rendering", "OpenGL / EGL · embedded fonts and SVG assets"),
                new("Native layer", "C11 · Wayland, keyboard, pointer and scroll input"),
                new("License", "MIT · third-party assets retain upstream licenses"),
                new("Repository", REPOSITORY_URL),
                new("Configuration", Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")),
                new("Logs", Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"),
                    "hyprnetshell", "hyprnetshell.log")),
            ];
        }

        var entries = _systemInfo.Snapshot.Entries.ToList();

        return entries;
    }
}
