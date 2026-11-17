# UI design conventions

## Look at existing tabs before designing

Before adding or changing a tab, **read other existing tabs and follow the way this application already does design**. Do not invent a new layout, header, button, or input style because it seems reasonable in isolation. Matching the existing UI is a requirement, not an optional polish pass.

Start with:

- `Core/Bar/MainDialogTabs/WeatherTab.cs`: reference for section headers and header actions.
- `Core/Bar/Common/MainDialogTabUi.cs`: shared section headers, buttons, messages, and status helpers.
- `Core/Bar/MainDialogTabs/ClipboardManagerTab.cs`: searchable lists and selection.
- `Core/Bar/Common/TextInputCoordinator.cs`: standard text inputs, focus, paste, Enter, and Escape.
- `Core/Bar/Common/BoundedListUi.cs`: list navigation and viewports.

Read the actual implementations before choosing a pattern. Use the closest existing example for the task; do not assume that every tab uses the same specialized layout.

## Headers: follow Weather

The Authenticator header showing `Authenticator` with `Ready.` stacked underneath is the specific example of what **not** to do. It replaces the existing header convention with an ad hoc vertical title/status block.

`WeatherTab.BuildHeader` does this correctly:

- Use `MainDialogTabUi.BuildSectionHeader(title, status)`.
- The shared helper places the title and muted status **on the same horizontal row**, with spread alignment and centered vertical alignment.
- Put header actions alongside the shared header using the existing outer horizontal layout, as Weather does with **Open forecast**.
- Do not reconstruct the helper as a custom column of `TextNode` instances.
- Do not place routine actions in a detached toolbar above the primary content.

Reuse the existing helper rather than merely copying its font sizes and colors. Keep status text useful to the user and avoid unnecessary permanent loading/retry controls.

## Buttons and inputs

- Prefer existing shared builders and established controls over tab-specific lookalikes.
- Match the reference tab's padding, corner radii, border treatment, hover state, disabled state, and sizing behavior.
- Do not add icons to every button by default; follow the comparable existing action.
- Use the standard text-input builder. Sensitive inputs may mask their value, but should retain the standard input appearance and behavior.
- Keep form actions adjacent to the field they submit. Keep the main tab action in the header when that is the existing comparable pattern.
- Show recovery actions when relevant, not as permanent primary actions competing with normal use.

## Layout and interaction

- Preserve the existing hierarchy: section header, primary search or form controls, then content/results.
- Match comparable tabs' spacing, alignment, row density, and text sizing.
- Use `ThemeManager.Current`; do not introduce a separate visual theme.
- Follow existing selection, scrolling, Enter, Escape, and paste conventions.
- Ensure long account names, errors, empty states, and busy states fit without displacing unrelated actions.

## Review before calling the design finished

1. Compare the changed layout directly against the reference tab, especially Weather's header.
2. Check that shared helpers were reused wherever applicable.
3. Check action placement and input appearance, not just colors and rounded corners.
4. Run the repository's required formatting and build validation for source changes.
5. Inspect the UI in a running Hyprland session when available. A successful build does not establish that a layout looks correct. If visual validation was not performed, say so explicitly.
