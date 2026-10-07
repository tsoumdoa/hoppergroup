# HopperGroup

HopperGroup is a Grasshopper plugin that automatically manages group membership when objects are dragged on the canvas.

aectooling by tomoS

## Features

- Adds moved or newly placed objects to the innermost Grasshopper group whose visual boundary contains the object center.
- Removes objects from a group only after the object center leaves that group beyond the configured exit buffer.
- Keeps a carried group's members together and preserves nested groups while their individual members change.
- Adds a dragged group to another group when its whole bounding box fits inside the destination.
- With objects selected and the canvas focused, tap `G` twice within 650 ms, then click one destination group to add them in one step. Press `Esc` to cancel.
- Provides manual refresh and optional debug logging.
- Marks cached group regions dirty when groups are created or deleted.

## Compatibility

- Rhino 8 / Grasshopper.
- Current build targets: `net48`, `net7.0`, and `net7.0-windows`.

## Installation

After the package is published to Yak:

```bash
yak install hoppergroup
```

For manual installation, build the project and copy the appropriate `HopperGroup.gha` output into a Grasshopper libraries or package location.

## Component Reference

The component is available under `Params > Util > Hopper Group`.

### Inputs

- `Enabled`: turns the automation on or off. Default: `true`. Grasshopper's own Disable command also stops the automation.
- `Exit Scale`: multiplier for the removal buffer. At the default `1`, an object must move beyond its group's edge by 1.5 times its size or at least 40 canvas units, whichever is larger. Increase it for a less sensitive exit; set it to `0` for no buffer.
- `Refresh`: toggle to rescan all groups and repair membership for every canvas object.
- `Debug`: writes debug messages to Rhino command history and the `Log` output.

### Outputs

- `Status`: current state and last operation count.
- `Groups`: number of cached group regions.
- `Changes`: membership changes from the last operation.
- `Log`: the most recent 200 debug lines. Rhino command history receives every debug message.

## Build

```bash
dotnet build HopperGroup.csproj
```

Build outputs:

- `bin/Debug/net48/HopperGroup.gha`
- `bin/Debug/net7.0/HopperGroup.gha`
- `bin/Debug/net7.0-windows/HopperGroup.gha`

Close Rhino before rebuilding if the plugin is loaded.

## Release / Yak Packaging

Create a local Yak package with:

```bash
./scripts/release-yak.sh 0.2.7
```

The script first runs the regression checks using the .NET 10 SDK. If they pass, it updates `HopperGroup.csproj` and `yak/manifest.yml`, builds all Release targets, stages the Rhino 8 multi-target package under `artifacts/yak/stage/`, and writes the final `.yak` package to `artifacts/yak/`.

If this is your first Yak publish from this machine, log in first:

```bash
"/Applications/Rhino 8.app/Contents/Resources/bin/yak" login
```

To rehearse publishing on McNeel's daily-wiped test server:

```bash
./scripts/release-yak.sh 0.2.7-beta.1 --push-test
```

To publish to the public Yak server:

```bash
./scripts/release-yak.sh 0.2.7 --push-public
```

Close Rhino before rebuilding if the plugin is loaded. Public Yak package versions cannot be overwritten after publishing; bump the version if a pushed release is wrong. Generated release artifacts live under `artifacts/yak/` and are ignored by git.

## Development Notes

Ordinary object membership is tested using each object's canvas-space center point. A dragged group uses its full bounding box: it joins the innermost destination that fully encloses it. Moving into an inner or sibling destination transfers the direct parent link while preserving the dragged group's descendants. Otherwise, it leaves its current parent when its box clears the buffered parent boundary. Each object uses its own bounding box for the exit buffer, including when multiple objects are moved together. The minimum exit buffer gives small objects room near the edge. Clicking without moving objects does not change membership. Existing nested group links remain intact when only an individual member changes.

On a canvas drag, a group stays intact when the group itself is selected and all its members move, or when a strict majority of its components are selected and move by the same offset. This also covers dragging a selection from a component without selecting the group. A group's only component is a majority: dragging that selected component carries its group, including when it is the last remaining member. Moving into another group nests the carried group only when its whole bounding box fits; moving out of a parent releases the parent link after the exit buffer while keeping the component's own group and nested descendants intact. Moving one component out of a two-component group still removes that component. To remove the only component from its own group, use Grasshopper's manual group membership commands.

Component movement is tracked using pivots, so resizing labels or other layout changes do not count as component drags. Scribbles retain center-based movement tracking because their corner pivots change when rotating in place. Matching movement offsets allow one canvas unit of layout rounding. When the group itself is selected, this also preserves members whose movement rounds to zero during a small group drag. Empty nested groups and members without usable canvas bounds do not prevent the tracked members from carrying their group. Containment and exit distances still use the visible bounds and object centers described above.

Moves made outside a canvas drag, such as keyboard or script repositioning, are reconciled on the next manual `Refresh` or actual canvas move/drop. Groups whose members all translate together keep their membership. Clicking without moving anything leaves pending moves untouched.

When an external group move is followed by a member drag, the drag uses the group's translated boundary at mouse-down. Individual members can still leave that boundary. Completed Undo and Redo operations reset the stored layout so later movement or Refresh preserves the restored memberships.

Disabling the component, either through `Enabled` or Grasshopper's Disable command, detaches the canvas and document handlers. Movement, placement, Refresh, and undo/redo do not trigger membership work while disabled. Re-enabling captures the current layout without replaying moves made while disabled.

Manual additions and removals update the boundaries used for pending external moves, including nested groups. Adding a member at a group's destination after a coherent external move keeps the original members together. Existing members are checked against the revised boundary at their settled positions so a large move can still leave the group. Unrelated groups retain their pending movement history, including when membership is added with GG. Both GG additions and drag destinations reject nesting a group inside its own descendant.

When a new object is manually grouped before the next Refresh or canvas drop, that membership uses its current position unless every member of the group moved together. Clicking a newly grouped selection preserves its manual memberships even over overlapping groups. New ungrouped objects still join the group at their placement point. Refresh after a manual addition establishes the boundary for later individual moves.

Run the isolated membership regression checks with the .NET 10 SDK:

```bash
dotnet run --project tests/HopperGroup.RegressionTests -c Release
```

These checks compile the production manager against a simulated canvas, group geometry, undo-record server, and completed undo/redo notifications. Undo/redo scenarios restore positions and memberships explicitly before raising the host event. Live Grasshopper canvas interaction and undo/redo restoration still require host verification.

On Windows, GG intercepts keys on the focused Grasshopper canvas before the editor can forward them to Rhino. It consumes both taps and their translated characters, requires physical releases between taps, and leaves text fields, modified keys, native canvas interactions, and reserved navigation/menu bindings available to the host. Multiple enabled HopperGroup components share one shortcut owner. Document changes, focus changes, other keys, mouse clicks, and wheel events reset incomplete gestures. Non-Windows builds retain the managed canvas key callbacks.

Run the Windows input checks with the .NET 10 SDK on Windows:

```powershell
dotnet run --project tests/HopperGroup.WindowsInputTests -c Release
```

These checks compile the production manager and native hook against real WinForms controls and a simulated GH document. They exercise queued keyboard messages, `TranslateMessage`, editor `KeyPreview` forwarding, text typing, focus, physical repeat, handle recreation, multiple component ownership, and coexistence with another canvas shortcut hook in both installation orders. A live Rhino/Grasshopper smoke test is still needed for the host's native interactions and plugin integration.

## License

HopperGroup is released under the [MIT License](LICENSE).
