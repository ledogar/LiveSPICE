# LiveSPICE Cross-Platform GUI Port Plan

## Current state

The local `linux` branch already has a working non-Windows simulation path through `LiveSPICE.Headless`. That branch builds a console runner around the shared `Circuit` library and `Circuit/AudioSimulationFactory.cs`, and the README documents Linux smoke tests and WAV processing.

The remaining portability gap is the GUI. The standalone editor is a WPF Windows desktop app in `LiveSPICE/LiveSPICE.csproj`, and the VST UI is also WPF-based in `LiveSPICEVst/LiveSPICEVst.csproj` through `AudioPlugSharpWPF`. Both target `net*-windows`, enable WPF/Windows Forms, and depend on Windows-only UI concepts such as `Microsoft.Win32` dialogs, WPF commands, WPF drawing, AvalonDock, and WinMM/ASIO-backed audio configuration.

## Branch strategy

Create a second branch from the local Linux branch so the headless Linux work stays intact and the GUI port can move independently:

```bash
git switch linux
git pull --ff-only origin linux
git switch -c linux-gui-port
```

Before running those commands locally, review the current dirty worktree. At the time this plan was written, `.gitignore` was modified and `.codacy/`, `PR_DESCRIPTION.md`, and `out/` were untracked. Either commit, stash, or intentionally carry those changes before switching branches.

## Recommended UI approach

Use Avalonia as the default open-source port target for the standalone GUI.

Avalonia is the closest fit because it keeps a XAML/C# mental model, supports Windows, macOS, and Linux, and has direct replacements for many WPF primitives used by LiveSPICE: windows, menus, command bindings, layout panels, data binding, custom controls, pointer input, drawing contexts, file pickers, and theming.

Do not start with Electron unless the goal changes to a web-first shell. The existing editor is deeply tied to C# circuit objects, custom schematic rendering, and direct manipulation of schematic elements, so an Electron port would add an interprocess API and rewrite most UI logic instead of adapting it.

Treat Avalonia XPF as an optional commercial shortcut, not the default plan. It may run more existing WPF XAML with fewer edits, but it adds licensing and still leaves platform-specific APIs, WPF-only dependencies, and audio/plugin details to solve.

## Architecture target

Keep the existing simulation core and serialization shared:

- `Circuit` remains the cross-platform schematic, component, solver, and simulation library.
- `ComputerAlgebra` and `Util` remain shared support libraries.
- `LiveSPICE.Headless` remains the Linux command-line validation target.
- A new Avalonia app, tentatively `LiveSPICE.Avalonia`, hosts the cross-platform editor UI.
- The existing WPF `LiveSPICE` app stays buildable on Windows until the Avalonia app reaches feature parity.

Avoid trying to retarget `LiveSPICE/LiveSPICE.csproj` in place at first. A sibling project makes it possible to port control by control while preserving the Windows app as a reference implementation.

## Porting phases

### Phase 1: Establish the Avalonia shell

Create `LiveSPICE.Avalonia` targeting `net8.0` or newer without a `-windows` target framework. Add it to `LiveSPICE.sln` and reference `Circuit`, `ComputerAlgebra`, and `Util`.

Build the first shell with:

- main window
- menu bar and toolbar
- status bar
- open/save file pickers for `.schx`
- single-document schematic viewer
- basic settings path under the user's platform-appropriate application data directory

Defer docking, MRU polish, audio config, and simulation UI until a schematic can be loaded and rendered.

### Phase 2: Port schematic rendering

Port `SchematicControls` concepts to Avalonia rather than trying to reuse WPF controls directly.

The core work is replacing WPF rendering types in `SchematicControls`:

- `Control.OnRender(DrawingContext)` becomes Avalonia custom control rendering.
- `System.Windows.Point`, `Vector`, `Matrix`, `Rect`, `Size` become Avalonia equivalents.
- WPF `Pen`, `Brushes`, `FormattedText`, geometry, and text APIs become Avalonia drawing APIs.
- Tooltips and hit testing need Avalonia pointer/event equivalents.

Start with read-only rendering of symbols and wires. Use the existing `Circuit.SymbolLayout` data as the model, because it is already UI-framework neutral. Once rendering is correct, add selection highlighting, terminal tooltips, and zoom/pan.

### Phase 3: Port schematic editing

Port `SchematicControl`, `SchematicViewer`, and `SchematicEditor` behavior into Avalonia equivalents.

Primary features:

- grid snapping and coordinate conversion
- selection rectangle and multi-select
- move, wire, symbol, and probe tools
- clipboard copy/paste using XML serialization
- undo/redo through `EditStack`
- save/save-as and external modification detection
- component library insertion

Keep edit operations model-driven. The existing `AddElements`, `RemoveElements`, `PropertyEdit`, and schematic serialization should continue to be the behavioral source of truth.

### Phase 4: Replace WPF-only desktop dependencies

Replace AvalonDock with a simpler first-pass layout: left component list, right property panel, central tabbed documents. Add a richer docking library only after the editor is functional.

Replace `DotNetProjects.Extended.Wpf.Toolkit` property grid usage with either:

- a small LiveSPICE-specific property editor generated from browsable component properties, or
- an Avalonia-compatible property grid package if one proves mature enough.

Prefer the small custom property editor initially. LiveSPICE mostly needs predictable editing of component values, not a general-purpose WPF toolkit clone.

### Phase 5: Audio on Linux and macOS

For the standalone GUI, keep audio driver selection behind the existing `Audio.Driver` abstraction. The current GUI references `Asio` and `WaveAudio`; `WaveAudio` is WinMM-only and `Asio` is Windows-oriented.

Implement new drivers as separate assemblies so `Audio.Driver.Drivers` can discover them like the existing drivers:

- `JackAudio` for Linux JACK, using a maintained .NET binding or a small native interop layer.
- `CoreAudio` for macOS only after the GUI can load, edit, and render schematics.

Initial Linux GUI milestones can run without real-time audio by reusing the headless WAV path. Real-time JACK should be a later milestone because it has native library, callback-thread, buffer-size, and packaging concerns.

### Phase 6: Plugin UI strategy

Handle the VST UI separately from the standalone editor.

AudioPlugSharp itself is intended to support non-Windows audio/plugin targets, but this repository's plugin UI currently inherits from `AudioPluginWPF` and references `AudioPlugSharpWPF`. Public AudioPlugSharp docs emphasize built-in WPF UI support, so cross-platform plugin UI support needs verification before committing to an implementation.

Investigate in this order:

1. Check whether current AudioPlugSharp has a non-WPF UI base class or host-embeddable native view API.
2. Build a minimal no-editor plugin on Linux/macOS using the existing `SimulationProcessor` logic.
3. Build a tiny cross-platform plugin editor proof of concept before porting LiveSPICE's plugin UI.
4. If AudioPlugSharp cannot host Avalonia directly, keep the plugin editor Windows-only for the first GUI branch and ship the standalone Avalonia editor plus headless/audio processing on Linux.

The plugin UI is much smaller than the full desktop app, but it has harder host-embedding constraints. Do not let it block the standalone GUI port.

## Milestones and validation

### Milestone A: Branch and baseline

- Create `linux-gui-port` from `linux`.
- Confirm `dotnet build Circuit/Circuit.csproj` passes.
- Confirm `dotnet build LiveSPICE.Headless/LiveSPICE.Headless.csproj` passes.
- Run the README headless smoke test against `Tests/Circuits/Passive 1stOrder Highpass RC.schx`.

### Milestone B: Avalonia app opens a schematic

- Create `LiveSPICE.Avalonia`.
- Load a `.schx` file.
- Display schematic metadata or a placeholder document tab.
- Save settings without Windows registry or WPF settings dependencies.

### Milestone C: Read-only schematic renderer

- Render wires, symbols, text, terminals, and schematic grid.
- Validate against known examples under `Tests/Examples`.
- Add screenshot comparison fixtures if practical.

### Milestone D: Basic editor parity

- Select, move, add, delete, undo, redo, copy, paste, save, and save-as.
- Component library and property editor are usable.
- Existing `.schx` files round-trip without serialization changes.

### Milestone E: Simulation workflow

- Run simulation from the Avalonia UI using `AudioSimulationFactory`.
- Display probe/scope output or an initial non-real-time render result.
- Keep Linux validation available without requiring JACK.

### Milestone F: Real-time audio

- Add JACK driver assembly for Linux.
- Validate callback stability, buffer sizes, sample rate selection, xrun behavior, and clean shutdown.
- Add CoreAudio driver or document that macOS support currently depends on plugin-host processing.

### Milestone G: Plugin investigation

- Prove whether AudioPlugSharp can host a non-WPF editor on Linux/macOS.
- Decide between Avalonia plugin UI, no-editor plugin, or Windows-only plugin UI for the first release.

## Technical risks

- Custom drawing is the largest standalone GUI task. `SymbolControl` and `WireControl` depend directly on WPF drawing APIs.
- Docking and property grid packages are WPF-specific and should be replaced, not ported first.
- File dialogs, clipboard, tooltips, command routing, keyboard modifiers, and mouse capture all need Avalonia-specific implementations.
- Real-time audio should not run on UI abstractions. Keep audio callback code isolated from Avalonia dispatching.
- Plugin UI portability may be constrained by the plugin host API more than by Avalonia itself.
- Packaging needs per-platform handling for native audio libraries, app icons, file associations, and macOS signing/notarization.

## First implementation checklist

1. Create `linux-gui-port` from `linux` after cleaning or stashing unrelated worktree changes.
2. Add `LiveSPICE.Avalonia` with a minimal window and solution entry.
3. Add a tiny `SchematicDocumentViewModel` that loads `Circuit.Schematic` from disk.
4. Port read-only drawing into a new Avalonia schematic canvas.
5. Add open-file flow and zoom/pan.
6. Add focused editor tools and property editing.
7. Add non-real-time simulation validation before JACK/CoreAudio.
8. Investigate AudioPlugSharp non-WPF editor support in a separate proof-of-concept branch or folder.