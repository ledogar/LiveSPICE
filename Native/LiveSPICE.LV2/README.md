# LiveSPICE Native LV2

This is the Linux-native plugin target for hosts such as Carla, Ardour, and REAPER that support LV2.

The current plugin is a mono MXR Phase 90-style phaser with `Speed` and `Trimmer` controls. It builds to a real Linux ELF shared object, unlike the AudioPlugSharp `.vst3` bridge that is Windows-only.

## Build

```bash
make -C Native/LiveSPICE.LV2 clean all
```

## Test Discovery

```bash
make -C Native/LiveSPICE.LV2 test
```

Expected URI:

```text
https://livespice.org/plugins/mxr-phase90
```

## Install

```bash
make -C Native/LiveSPICE.LV2 install
```

This copies the bundle to:

```text
~/.lv2/LiveSPICE-MXR-Phase90.lv2
```

## Test With Carla

```bash
carla-single lv2 https://livespice.org/plugins/mxr-phase90
```

If the plugin loads successfully, Carla opens/runs the plugin host instead of immediately failing with a plugin description error.
