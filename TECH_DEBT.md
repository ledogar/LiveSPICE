# Technical debt

Known problems in this fork, recorded so they are not rediscovered. Produced by a design and
documentation adherence review on 2026-07-29 against `GUI_PORT_PLAN.md` and `README.md`, and kept
current as items are fixed.

Ownership is marked because it affects where a fix should go:

- **(fork)** — introduced by the macOS port in this fork. Fix here.
- **(upstream)** — came from [PR #272](https://github.com/dsharlet/LiveSPICE/pull/272). Worth
  reporting back rather than only fixing locally.

Already fixed, for context: a diverged circuit could not be revived by a control change; the state
handoff silently transferred nothing across a sample-rate change while still carrying the sample
clock; and the pre-compile warm-up could throw past the divergence handler into the audio callback.
All three were fixed in `d94a12a` with tests verified to fail without them.

---

## 1. Real-time safety

The design constraint is stated in `GUI_PORT_PLAN.md:176` — *"Real-time audio should not run on UI
abstractions. Keep audio callback code isolated from Avalonia dispatching."*

### 1.1 The GUI dispatches to Avalonia from the Core Audio IOProc — **(upstream)**

`LiveSPICE.Avalonia/WaveformWindow.cs:221` calls `Dispatcher.UIThread.Post(...)` on the real-time
thread, roughly 94 times a second. Each call costs a closure allocation, a `DispatcherOperation`
allocation, a lock the UI thread also takes, and a `CFRunLoopWakeUp` syscall. Interacting with the
schematic while audio runs can hold that lock long enough to miss the audio deadline. The queue is
unbounded and the UI cannot sustain the post rate — `WaveformWindow.cs:401` scans every sample per
render — so the queue and the buffers it retains grow without limit.

This is the clearest breach of the documented design in the tree.

**Fix:** a lock-free single-producer ring the UI drains on a `DispatcherTimer` at ~30 Hz. This also
resolves 1.2's largest allocation, so the two should be done together.

### 1.2 Allocation on the audio thread

- `LiveSPICE.Avalonia/LiveAudioProcessor.cs:88` allocates `new double[count]` **every callback**
  to hand samples to the UI — directly below the same file's comment at `:12` saying the audio path
  must not allocate, and immediately after `:60-66` carefully pools the working buffers. **(fork)**
- `LiveSPICE.PluginCore/SimulationProcessor.cs:131` iterates `InteractiveComponents`, an
  `ObservableCollection<T>`, whose only enumerator is behind an interface — so `foreach` boxes it
  once per buffer, even with zero interactive components. **(upstream)**
- `SimulationProcessor.UpdateSimulation()` is reachable *from* the audio thread and allocates a
  `Task` plus closures, then takes a second lock inside `RedundantTaskScheduler.QueueTask`.

**Fix for the second:** publish an `IComponentWrapper[]` snapshot with `Volatile.Write` and iterate
by index; arrays have a non-allocating `foreach`. That also fixes 2.2.

### 1.3 The audio thread can block behind the state handoff — **(fork)**

`SimulationProcessor.Publish` holds `lock (sync)` across `Simulation.CopyStateFrom`, which walks the
whole `globals` dictionary doing `TryGetValue` against `Expression` keys whose hashing and equality
are structural over a symbolic tree. The audio thread takes that same lock around `simulation.Run`.
A pot move therefore makes the real-time thread wait on a non-real-time thread for
O(state-variables) symbolic hashes — a priority inversion.

Note the solve and the compile *are* off the audio thread; it is the handoff itself that is not free.

**Fix:** hand the built simulation over through an `Interlocked.Exchange` slot the audio thread
picks up, so `CopyStateFrom` runs entirely off the real-time thread.

### 1.4 `CoreAudio/Stream.cs` native boundary — **(fork)**

- **The exception handler is itself unprotected.** The `catch` calls `SilenceOutput`, which does raw
  `Marshal.Read*` on `ioData`. A malformed pointer — device disconnect, partially torn-down unit —
  throws *from the handler* into the IOProc, which is a native crash rather than a catchable
  failure. Needs a nested `try`.
- **`SilenceOutput` ignores `mDataByteSize`** and is called with the *untruncated* `inNumberFrames`
  even on the path that exists to defend against an oversized callback.
  `AudioBufferList.GetDataByteSize` already exists in `AudioUnitApi.cs` and is unused.
- **The truncation path truncates the input pull too**, under-draining the device's input ring every
  cycle and desynchronising it — turning a transient into permanent corruption.
- **`bufferFrames` and `sampleRate` are sampled once at construction**, with no
  `AudioObjectAddPropertyListener`. Buffer size is a *shared* device property on macOS: another
  application changing it leaves this stream truncating every callback indefinitely, and the CLI's
  rate guard compares against the stale cached value so it never fires.
- **`Stop()` sets `running = false` before `AudioOutputUnitStop`**, so an in-flight callback silences
  a buffer it had already computed — an audible click on every stop. The stop already quiesces the
  IOProc, so the flag is redundant there.

---

## 2. Concurrency

### 2.1 Unsynchronized shared state in `SimulationProcessor`

`needRebuild`, `needUpdate`, `simulationUpdateException`, `circuit` and `sampleRate` are plain
fields written from up to three threads with inconsistent lock coverage and no `volatile`. Two
concrete consequences:

- A UI-thread `Oversample` change can be swallowed by the audio thread clearing `needRebuild`, with
  nothing re-checking — the setting silently does nothing.
- `simulationUpdateException`'s unlocked read-then-clear can lose a second build failure entirely.

Additionally **(fork)**: `LiveSPICE.CLI/Program.cs` calls the `SampleRate` **setter from inside the
audio callback**, which performs three unsynchronized writes. The background builder can then solve
at the stale rate with nothing to correct it. A public property that kicks off a rebuild should not
be callable from an IOProc; the CLI should set a flag instead.

### 2.2 `InteractiveComponents` is mutated without the lock — **(upstream)**

The audio thread enumerates it under `lock (sync)`, but `SetCircuit`/`ClearSchematic` mutate it from
the UI thread without taking that lock. Loading a schematic while live — or `PluginEditorWindow`'s
`FileSystemWatcher` firing on a save — throws `InvalidOperationException: Collection was modified`
out of the audio thread, once per buffer. Fixed by the snapshot in 1.2.

### 2.3 The live path solves and compiles twice, on the UI thread — **(upstream, amplified)**

`LiveSPICE.Avalonia/WaveformWindow.cs:190-193` builds at a hardcoded 48000, opens the stream (so
callbacks begin), then rebuilds at the device's real rate. Since `LiveAudioProcessor.Start` now
pre-compiles, both builds are a full solve *plus* compile — so this fork roughly doubled the cost of
an already-wrong pattern. The first result is discarded, and audio runs at the wrong rate until the
second finishes.

---

## 3. `GUI_PORT_PLAN.md` Milestone F — largely unmet

Clean shutdown and callback hygiene are solid. Of the five validations named at
`GUI_PORT_PLAN.md:163`, three are unimplemented — all **(fork)**:

- **Xrun/dropout detection: absent.** `CoreAudio/Stream.cs` never inspects `ioActionFlags` or checks
  `inTimeStamp` continuity, the two ways AUHAL surfaces a dropout. `AudioUnitRender` failure — which
  *is* an input overrun — is silently swallowed with its status discarded. Note this needs a
  contract change: `Audio/Stream.cs` has no API for a driver to *report* an xrun at all.
- **Buffer-size selection: absent.** `CoreAudioApi.SetProperty<T>` was written for it and has **zero
  call sites**.
- **Sample-rate selection: absent.** The nominal rate is only ever read.

Related dead surface: `Stream.OversizedCallbacks` has **zero readers**. The one counter that would
diagnose the buffer-size failure above is incremented and discarded.

---

## 4. Structure and solution membership

- **`LiveSPICE.Avalonia` is not in `LiveSPICE.sln`**, which `GUI_PORT_PLAN.md:47` requires and `:183`
  repeats. There is a good reason — that solution holds five `-windows` WPF projects and cannot
  build off Windows — but the plan was never amended. PR #272 *did* add `LiveSPICE.Headless` to it,
  so the instruction was followed for one project and silently dropped for the other. Either
  condition the WPF projects on Windows and follow the plan, or amend the plan.
- **Membership is inconsistent both ways.** `CoreAudio` is in no solution but `LiveSPICE.Core.sln`
  yet is project-referenced by `LiveSPICE.Avalonia`; `LiveSPICE.PluginCore` is the mirror image,
  referenced by `LiveSPICE.CLI` but absent from `Core.sln`. Both build transitively but never appear
  in an IDE solution tree. The two console hosts never appear in the same solution.
- **`LiveSPICE.Linux.sln` is misnamed** — CI builds it on `macos-latest` and it pulls in the macOS
  backend. It is the cross-platform GUI solution.
- **`GUI_PORT_PLAN.md:104` requires drivers be separate assemblies; the JACK driver is not**
  **(upstream)**. `LiveSPICE.Avalonia/LinuxAudioDriver.cs` lives inside the GUI app and is
  hand-constructed, so no non-Avalonia host can see it. `CoreAudio` does comply — though its
  discovery relies on `GC.KeepAlive(typeof(CoreAudio.Driver))` in `AvaloniaAudioDrivers`, a
  compile-time dependency on a specific driver that meets the letter but not the spirit of
  reflective discovery.

---

## 5. Correctness, lower severity

- **The bypass path writes only channel 0** in `SimulationProcessor` — hosts with more outputs keep
  replaying a stale buffer while the simulation builds. *(Partially fixed in `d94a12a`; verify
  against a multi-channel host.)*
- **`ClearSchematic` leaves `simulation` non-null** — `SimulationReady` keeps returning true after
  the schematic is cleared, which is what the CLI polls. **(upstream)**
- **`LiveAudioProcessor` never reconciles the device sample rate** **(fork)** — it takes `rate` per
  callback and uses it only for the test tone. The CLI reconciles; the WPF app rebuilds on change.
  The Avalonia live path would run the wrong time step indefinitely.
- **The `AudioSimulationFactory` multi-input message was not applied to its two duplicates** —
  `WaveformWindow.cs:130` and `LiveSPICEVst/SimulationProcessor.cs` still use the old pattern. Note
  the old `SingleOrDefault` already threw on 2+ inputs, so this is message quality only.

---

## 6. Test coverage

- `CoreAudio`'s `Device`, `Channel` and `Stream` are `internal` with no `InternalsVisibleTo`, so the
  only test touching the backend asserts the driver is *present*. The `FourCC` formatter is public
  and untested.
- `LiveSPICE.CLI` has **no test project** — `Wav`, the argument parser and `SelectChannels` are
  untested. CI does smoke-test the critical path (`tone` → `render` exercises `Wav` both ways plus
  the full simulation chain), and the audio path was verified by BlackHole loopback and by ear, but
  none of that is regression-protected.
- **The `LIVESPICE_SCREENSHOT` hook in `MainWindow.cs` works headlessly and nothing uses it.**
  Wiring it into CI would give a schematic-rendering regression test and would close
  `GUI_PORT_PLAN.md:146` ("Add screenshot comparison fixtures if practical") properly.

---

## 7. Documentation

- **`README.md` never mentions macOS.** It is untouched since before the port: no `livespice`, no
  `CoreAudio`, no `LiveSPICE.Avalonia`, no `--check`, no solution files. A contributor reading only
  the README cannot discover how CI validates a change. Highest-severity documentation item, since
  it is the entry point. *Agreed approach: a short pointer in `README.md` plus a full
  `docs/macos.md`, keeping the upstream-owned README nearly unchanged to minimise re-sync conflicts.*
- **`README.md:29` is factually wrong** — it claims the headless runner "reuses the same input/output
  wiring logic as the VST path". It calls `AudioSimulationFactory` and runs its own loop; they share
  only the factory. More misleading now that a real shared host layer exists which `LiveSPICE.Headless`
  alone does not use.
- **`livespice --help` omits the `loopback` command entirely** **(fork)** — it is dispatched but
  absent from the usage text, along with its `--record` flag. Since the CLI has no other
  documentation, both are undiscoverable. Two-line fix.
- **`PR_DESCRIPTION_LINUX_GUI_PORT.md` is vestigial** — a merged PR's body checked into the tree,
  including tooling complaints and instructions to a concluded review. Its closing "Windows verified
  unchanged" claim is outdated.
- **`GUI_PORT_PLAN.md` has no status markers.** Phases 1-4 are done but still written in the
  imperative; the branch-strategy section and implementation checklist are spent, and the unmet items
  are buried among completed ones. It also predates half the tree — `LiveSPICE.PluginCore`,
  `LiveSPICE.PluginLinux`, `Native/LiveSPICE.LV2` and `LiveSPICE.CLI` appear nowhere in it, so its
  architecture section lists five components where the tree has ten.
- **`AudioPlugSharp/README.md`** is a single orphan line instructing you to drop a binary release
  into a directory nothing reads; the projects consume AudioPlugSharp via NuGet.
- **`GUI_PORT_PLAN.md` Milestone G is unresolved** — nothing records whether AudioPlugSharp can host
  a non-WPF editor. The LV2 route sidestepped the question rather than answering it, and the decision
  that was effectively made is recorded only in the vestigial PR description above.

---

## Suggested order

1. **1.1 + 1.2** together — one change (the ring buffer) removes the explicit design violation and
   the largest audio-thread allocation. This is also the one a user can *hear*.
2. **7** — the documentation items are cheap and the README is the entry point. `livespice --help`
   is two lines.
3. **1.3, 2.1** — the remaining real-time hazards, in that order.
4. **1.4** — the native-boundary hardening; the unprotected handler first, since it is the only item
   here that can crash the process.
5. **3** — Milestone F needs an `Audio.Stream` contract change, so it is the largest single piece and
   worth doing deliberately rather than piecemeal.
