# Title

Add Linux GUI, audio, and LV2 plugin port

# Description

## Summary

Adds a Linux-focused LiveSPICE port alongside the existing Windows application instead of replacing it.

This branch introduces an Avalonia desktop editor, Linux audio configuration and live simulation support, a shared plugin core for Linux plugin experiments, and native LV2 plugin bundles with a GTK3 UI. The main Windows solution and Windows WPF/VST UI paths are intentionally left unchanged; Linux-specific projects live in `LiveSPICE.Linux.sln`.

## Highlights

- Adds `LiveSPICE.Avalonia` as a Linux desktop editor with schematic loading, editing, property inspection, waveform viewing, and live audio controls.
- Adds Linux audio backends and configuration flow, including JACK-oriented device naming and a virtual audio mode for validation.
- Adds `LiveSPICE.Avalonia.Tests` coverage for editor interaction, launch/open behavior, settings, audio simulation flow, plugin port behavior, and GUI parity checks.
- Adds `LiveSPICE.PluginCore` and `LiveSPICE.PluginLinux` for Linux plugin-facing simulation/control logic without changing the Windows VST UI.
- Adds native LV2 plugin bundles under `Native/LiveSPICE.LV2`, including an MXR Phase 90 plugin and a generic schematic-loader plugin shell.
- Adds a GTK3 LV2 UI with schematic selection, discovered controls, Windows-style plugin presentation, knob scales/ticks/labels, and host smoke-test support.
- Adds `LiveSPICE.Linux.sln` so Linux-specific projects can be built independently from the Windows WPF solution.
- Keeps Windows-facing paths unchanged relative to the `linux` base after the final cleanup commit.

## Validation

- `dotnet build LiveSPICE.Linux.sln --no-restore`
- Review fix validation: `dotnet test LiveSPICE.Avalonia.Tests/LiveSPICE.Avalonia.Tests.csproj --no-build --filter "FullyQualifiedName~PluginProgramParametersRoundTripProcessorSettings|FullyQualifiedName~LinuxPluginStateRoundTripsProcessorSettings|FullyQualifiedName~PluginEditorCreatesOverlayControlsForInteractiveSchematic|FullyQualifiedName~SimulationProcessorPassesThroughWhenNoSchematicIsLoaded|FullyQualifiedName~SimulationProcessorProcessesLoadedRcSchematic|FullyQualifiedName~SimulationAndAudioTests" --logger "console;verbosity=minimal"` passed 17 tests.
- Focused Avalonia tests for schematic interaction, settings, and menu-open behavior passed: 17 tests.
- Native LV2 build/test/UI smoke/install flow passed with `make -C Native/LiveSPICE.LV2 clean all test ui-smoke install`.
- Carla was able to load the generic LV2 plugin with `carla-single lv2 https://livespice.org/plugins/generic`.
- Codacy analysis was clean on edited C# files where supported. `.sln`, `.xaml`, and `.csproj` files were reported unsupported by the configured Codacy tools.
  A later Codacy retry for the review-comment fixes was blocked because the Codacy MCP install tool failed to create `.codacy/cli.sh` with `TypeError [ERR_INVALID_ARG_TYPE]: The "path" argument must be of type string`.

## Notes for reviewers

The Linux GUI/plugin work is intentionally additive. Reviewers can focus on:

- `LiveSPICE.Avalonia/*`
- `LiveSPICE.Avalonia.Tests/*`
- `LiveSPICE.PluginCore/*`
- `LiveSPICE.PluginLinux/*`
- `Native/LiveSPICE.LV2/*`
- `LiveSPICE.Linux.sln`

The native generic LV2 plugin currently provides host-loadable state/UI plumbing and pass-through audio; full schematic DSP execution inside the native LV2 shell remains follow-up work.

The existing Windows WPF application and Windows VST UI are not part of this port and were verified unchanged against the `linux` branch for `LiveSPICE.sln`, `LiveSPICE/`, `LiveSPICEVst/`, `MockVst/`, and `SchematicControls/`.
