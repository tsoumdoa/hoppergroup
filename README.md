# HopperGroup

HopperGroup is a Grasshopper plugin that automatically manages group membership when objects are dragged on the canvas.

aectooling by tomoS

## Features

- Adds moved or newly placed objects to the innermost Grasshopper group whose visual boundary contains the object center.
- Removes objects from a group only after the object center leaves that group beyond the configured exit buffer.
- Keeps a carried group's members together, and repairs nested groups when a group moves or Refresh is used.
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
- `Exit Scale`: multiplier for each moved object's size used as its removal buffer. Default: `1`.
- `Refresh`: toggle to rescan all groups and repair membership for every canvas object.
- `Debug`: writes debug messages to Rhino command history and the `Log` output.

### Outputs

- `Status`: current state and last operation count.
- `Groups`: number of cached group regions.
- `Changes`: membership changes from the last operation.
- `Log`: debug log.

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
./scripts/release-yak.sh 0.2.0
```

The script updates `HopperGroup.csproj` and `yak/manifest.yml`, builds all Release targets, stages the Rhino 8 multi-target package under `artifacts/yak/stage/`, and writes the final `.yak` package to `artifacts/yak/`.

If this is your first Yak publish from this machine, log in first:

```bash
"/Applications/Rhino 8.app/Contents/Resources/bin/yak" login
```

To rehearse publishing on McNeel's daily-wiped test server:

```bash
./scripts/release-yak.sh 0.2.0-beta.1 --push-test
```

To publish to the public Yak server:

```bash
./scripts/release-yak.sh 0.2.0 --push-public
```

Close Rhino before rebuilding if the plugin is loaded. Public Yak package versions cannot be overwritten after publishing; bump the version if a pushed release is wrong. Generated release artifacts live under `artifacts/yak/` and are ignored by git.

## Development Notes

Membership is tested using each object's canvas-space center point. Each object uses its own bounding box for the exit buffer, including when multiple objects are moved together. Clicking without moving objects does not change membership. Group hierarchy is based on complete rectangle containment: a smaller group becomes a child of the smallest larger group whose cached bounds fully contain it.

Moves made outside a canvas drag, such as keyboard or script repositioning, are reconciled on the next manual `Refresh` or actual canvas move/drop. Groups whose members all translate together keep their membership. Clicking without moving anything leaves pending moves untouched.

When an external group move is followed by a member drag, the drag uses the group's translated boundary at mouse-down. Individual members can still leave that boundary. Completed Undo and Redo operations reset the stored layout so later movement or Refresh preserves the restored memberships.

Disabling the component, either through `Enabled` or Grasshopper's Disable command, detaches the canvas and document handlers. Movement, placement, Refresh, and undo/redo do not trigger membership work while disabled. Re-enabling captures the current layout without replaying moves made while disabled.

Manual additions and removals update the boundaries used for pending external moves, including nested groups. Existing members are checked against the revised boundary at their settled positions so a large move can still leave the group. Unrelated groups retain their pending movement history.

Run the isolated membership regression checks with the .NET 10 SDK:

```bash
dotnet run --project tests/HopperGroup.RegressionTests -c Release
```

These checks compile the production manager against a simulated canvas, group geometry, undo-record server, and completed undo/redo notifications. Undo/redo scenarios restore positions and memberships explicitly before raising the host event. Live Grasshopper canvas interaction and undo/redo restoration still require host verification.

## License

HopperGroup is released under the [MIT License](LICENSE).
