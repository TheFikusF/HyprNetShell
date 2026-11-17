using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Logging;
using HyprNetShell.GUI;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class AuthenticatorTab(ClipboardHistoryService clipboard, TextInputCoordinator inputs) : IMainDialogTab, IDisposable
{
    private const int MAX_URI_LENGTH = 8192;
    private readonly AuthenticatorService _service = new();
    private readonly Dictionary<string, ModulesCommon.BoxState> _states = [];
    private readonly Dictionary<string, ActionButton> _buttons = [];
    private readonly Dictionary<string, AnimatedIconButton> _removeButtons = [];
    private readonly TextInputCoordinator.Input _searchInput = inputs.Create(
        "", "", "Search authenticator accounts...", 512, alwaysActive: true);
    private readonly TextInputCoordinator.Input _importInput = inputs.Create(
        "", "", "Paste an otpauth:// or otpauth-migration:// URI...", MAX_URI_LENGTH,
        clearOnEscape: true, pasteReplacesValue: true);
    private AuthenticatorCodeCopy? _codeCopy;
    private string _copyStatus = "";
    private int _selectedIndex;
    private int _firstIndex;
    private bool _importMode;
    private bool _disposed;

    public string Id => "authenticator";
    public string Title => "Authenticator";
    public SvgAsset Icon => Icons.Lock;
    private AuthenticatorCodeCopy CodeCopy => _codeCopy ??= new(_service.GenerateHotpAsync,
        code => clipboard.CopySensitiveTextAsync(code));

    public void Activate()
    {
        SetImportMode(false);
        inputs.Configure(_searchInput, _ =>
        {
            _selectedIndex = 0;
            _firstIndex = 0;
        });
        inputs.Configure(_importInput, submit: _ =>
        {
            SubmitImport();
            return false;
        });
        if (!_service.IsBusy)
        {
            StartOperation(_service.LoadAsync);
        }
    }

    public void MoveSelection(SelectionDirection direction)
    {
        if (!_importMode && direction is SelectionDirection.Up or SelectionDirection.Down)
        {
            BoundedListUi.MoveSelection(ref _selectedIndex, ref _firstIndex,
                direction == SelectionDirection.Up ? -1 : 1, FilterAccounts().Count);
        }
    }

    public void ActivateSelection()
    {
        if (_importMode)
        {
            SubmitImport();
            return;
        }

        var accounts = FilterAccounts();
        BoundedListUi.Normalize(ref _selectedIndex, ref _firstIndex, accounts.Count);
        if (accounts.Count > 0)
        {
            _ = CopyAccountAsync(accounts[_selectedIndex]);
        }
    }

    public bool HandleEscape()
    {
        if (!_importMode)
        {
            return false;
        }

        SetImportMode(false);
        return true;
    }

    public Node Draw()
    {
        var accounts = FilterAccounts();
        BoundedListUi.Normalize(ref _selectedIndex, ref _firstIndex, accounts.Count);
        var children = new List<Node>
        {
            new BoxNode(Style.Spacer, ItemsAlignment.Spread, ItemsAlignment.Center)
            {
                Children =
                [
                    MainDialogTabUi.BuildSectionHeader(
                        Title,
                        _service.IsBusy ? "Working…" : _service.Status),
                    ..(!_service.IsBusy && _service.Status != "Ready." && _service.Status != "Not loaded."
                        ? new[] { ActionButton.Build(_buttons, "retry", "Retry", () => StartOperation(_service.LoadAsync), Icons.Reboot) }
                        : Array.Empty<Node>()),
                    ..(!_importMode
                        ? new[] { ActionButton.Build(_buttons, "import-toggle", "Import account", () => SetImportMode(true), Icons.Add) }
                        : Array.Empty<Node>()),
                ],
            },
        };
        if (_importMode)
        {
            children.Add(new TextNode(
                "Google Authenticator: Transfer accounts → Export accounts. Decode a QR offline and paste its migration URI directly here. Import each page separately. Standard TOTP and HOTP otpauth URIs are also supported. Never upload QR codes or secrets to a website.",
                maxWidth: 800, wrapping: TextWrapping.Wrap));
            children.Add(new BoxNode(Style.Spacer) {
                HorizontalAlignment = ItemsAlignment.Stretch,
                VerticalAlignment = ItemsAlignment.Center,
                Children =
                [
                    new BoxNode
                    {
                        Flex = 1,
                        HorizontalAlignment = ItemsAlignment.Stretch,
                        Children = [inputs.Build(_importInput, masked: true)],
                    },
                    ActionButton.Build(_buttons, "import-submit", "Add",
                        SubmitImport, Icons.Add, !_service.IsBusy && !string.IsNullOrWhiteSpace(_importInput.Value)),
                    ActionButton.Build(_buttons, "import-cancel", "Cancel", () => SetImportMode(false), Icons.Delete),
                ],
            });
        }
        else
        {
            children.Add(inputs.Build(_searchInput));
            children.Add(BoundedListUi.BuildList(accounts, BuildAccount, _firstIndex,
                BoundedListUi.DEFAULT_VISIBLE_ITEM_COUNT,
                delta => MoveSelection(delta > 0 ? SelectionDirection.Up : SelectionDirection.Down)));
            if (accounts.Count == 0)
            {
                children.Add(new TextNode("No matching accounts. Import an account or unlock the desktop keyring and retry."));
            }
        }

        if (_copyStatus.Length > 0)
        {
            children.Add(new TextNode(_copyStatus));
        }

        return new BoxNode(Style.Spacer) {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Children = children,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        inputs.SetValue(_importInput, "");
        if (inputs.IsActive(_importInput) || inputs.IsActive(_searchInput))
        {
            inputs.Deactivate();
        }

        _codeCopy?.Dispose();
        _service.Dispose();
    }

    private IReadOnlyList<AuthenticatorAccountSnapshot> FilterAccounts() => _service.Accounts
        .Where(account => string.IsNullOrWhiteSpace(_searchInput.Value) ||
            $"{account.Issuer} {account.Account}".Contains(_searchInput.Value, StringComparison.OrdinalIgnoreCase))
        .ToArray();

    private Node BuildAccount(AuthenticatorAccountSnapshot account, int index)
    {
        var selected = index == _selectedIndex;
        var state = _states.GetState(account.Id, ThemeManager.Current.Panel)
            .UpdateColor(selected ? ThemeManager.Current.Active : ThemeManager.Current.Panel);
        if (!_removeButtons.TryGetValue(account.Id, out var removeButton))
        {
            removeButton = new AnimatedIconButton();
            _removeButtons[account.Id] = removeButton;
        }

        return new BoxNode(Style.Spacer) {
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            IsHoveredThrough = state.Hovered,
            Style = ModulesCommon.ModuleStyle(state.Background) with {
                Padding = new Insets(16, 8),
                BorderRadius = 8,
                BorderWidth = selected ? ThemeManager.Current.Border.Width : 0,
                Spacing = 8,
            },
            Children =
            [
                new BoxNode(Style.Spacer)
                {
                    Flex = 1,
                    Direction = Direction.Vertical,
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    Children =
                    [
                        new TextNode(account.Issuer, maxWidth: 360),
                        new TextNode(account.Account, maxWidth: 360),
                    ],
                },
                ActionButton.Build(_buttons, $"copy-{account.Id}", account.Type == "hotp" ? "Generate & copy" : $"{account.Code} · {account.Seconds}s", () =>
                {
                    _selectedIndex = index;
                    ActivateSelection();
                }, Icons.Clipboard, !CodeCopy.IsBusy(account.Id) && !CodeCopy.CanRetry(account.Id)),
                ..(account.Type == "hotp" && CodeCopy.CanRetry(account.Id)
                    ? new[] { ActionButton.Build(_buttons, $"retry-copy-{account.Id}", "Retry copy",
                        () => _ = CopyAccountAsync(account, retry: true), Icons.Clipboard, !CodeCopy.IsBusy(account.Id)) }
                    : Array.Empty<Node>()),
                removeButton.Build(Icons.Delete, selected || state.Hovered.Value,
                    () => StartOperation(() => _service.RemoveAsync(account.Id)), !_service.IsBusy),
            ],
        };
    }


    private void SetImportMode(bool enabled)
    {
        inputs.SetValue(_importInput, "");
        _importMode = enabled;
        inputs.Activate(enabled ? _importInput : _searchInput);
    }

    private void SubmitImport()
    {
        var uri = _importInput.Value;
        inputs.SetValue(_importInput, "");
        if (!_disposed && !_service.IsBusy && !string.IsNullOrWhiteSpace(uri))
        {
            StartOperation(() => _service.ImportAsync(uri));
        }
    }

    private void StartOperation(Func<Task> operation)
    {
        _copyStatus = "";
        _ = Task.Run(async () =>
        {
            try
            {
                await operation();
            }
            catch (ObjectDisposedException) when (_disposed)
            {
            }
            catch (Exception)
            {
                AppLogger.Warning("Authenticator", "Could not complete authenticator operation. Retry after unlocking the desktop keyring.");
                _copyStatus = "Operation failed. Unlock the desktop keyring and retry.";
            }
        });
    }

    private async Task CopyAccountAsync(AuthenticatorAccountSnapshot account, bool retry = false)
    {
        try
        {
            var copied = account.Type == "hotp"
                ? retry ? await CodeCopy.RetryCopyAsync(account.Id) : await CodeCopy.GenerateAndCopyAsync(account.Id)
                : await clipboard.CopySensitiveTextAsync(account.Code);
            _copyStatus = copied
                ? "Code copied with a privacy hint. External clipboard managers may ignore it."
                : account.Type == "hotp" && CodeCopy.CanRetry(account.Id)
                    ? "Copy failed. Retry copy reuses this code for up to 5 minutes without advancing the counter."
                    : "Copy failed. Check native Wayland data-control support and the authenticator status.";
        }
        catch (Exception)
        {
            AppLogger.Warning("Authenticator", "Could not copy authenticator code with the sensitive clipboard operation.");
            _copyStatus = "Copy failed. Check native Wayland data-control support. For HOTP, use Retry copy if available; generating again consumes another counter.";
        }
    }
}
