using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Circuit;
using Util;
using ButtonWrapper = LiveSPICE.PluginCore.ComponentWrapper<Circuit.IButtonControl>;

namespace LiveSPICE.PluginCore;

public class SimulationProcessor
{
    private double sampleRate;
    private int oversample = 2;
    private int iterations = 8;
    private Circuit.Circuit? circuit;
    private Simulation? simulation;
    private bool needUpdate;
    private bool needRebuild;
    private int updateSamplesElapsed;
    private int delayUpdateSamples;
    private Exception? simulationUpdateException;
    private int clock = -1;
    private int update;
    private readonly TaskScheduler scheduler = new RedundantTaskScheduler(1);
    private readonly object sync = new object();

    public SimulationProcessor()
    {
        InteractiveComponents = new ObservableCollection<IComponentWrapper>();
        SampleRate = 44100;
    }

    public ObservableCollection<IComponentWrapper> InteractiveComponents { get; }

    public Schematic? Schematic { get; private set; }

    public string SchematicPath { get; private set; } = string.Empty;

    public string SchematicName => System.IO.Path.GetFileNameWithoutExtension(SchematicPath);

    public double SampleRate
    {
        get => sampleRate;
        set
        {
            if (sampleRate == value)
                return;

            sampleRate = value;
            needRebuild = true;
            delayUpdateSamples = (int)(sampleRate * .1);
        }
    }

    public int Oversample
    {
        get => oversample;
        set
        {
            if (oversample == value)
                return;

            oversample = value;
            needRebuild = true;
        }
    }

    public int Iterations
    {
        get => iterations;
        set
        {
            if (iterations == value)
                return;

            iterations = value;
            needRebuild = true;
        }
    }

    /// <summary>True once a simulation has been built and published; until then RunSimulation bypasses.</summary>
    public bool SimulationReady => simulation != null;

    public void LoadSchematic(string path)
    {
        SetSchematic(Circuit.Schematic.Load(path), path);
    }

    /// <summary>Use an already-loaded schematic, e.g. one being edited in memory.</summary>
    public void SetSchematic(Schematic schematic, string path = "")
    {
        Circuit.Circuit newCircuit = schematic.Build();
        SetCircuit(newCircuit);
        Schematic = schematic;
        SchematicPath = path;
    }

    public void ClearSchematic()
    {
        Schematic = null;
        SchematicPath = string.Empty;
        circuit = null;
        InteractiveComponents.Clear();
    }

    public void RunSimulation(double[][] audioInputs, double[][] audioOutputs, int numSamples)
    {
        if (simulationUpdateException != null)
        {
            Exception toThrow = simulationUpdateException;
            simulationUpdateException = null;
            throw toThrow;
        }

        if (circuit == null)
        {
            Bypass(audioInputs, audioOutputs, numSamples);
            return;
        }

        lock (sync)
        {
            // This scan must run even while bypassed. It is the only route by which a control
            // change reaches needRebuild, so if it sat below the bypass return, a simulation that
            // had been dropped (a divergence, a failed build) could never be revived by turning a
            // pot down - the one thing a player would naturally try.
            foreach (IComponentWrapper component in InteractiveComponents)
            {
                if (component.NeedUpdate)
                {
                    needUpdate = true;
                    component.NeedUpdate = false;
                    updateSamplesElapsed = 0;
                }

                if (component.NeedRebuild)
                {
                    needRebuild = true;
                    component.NeedRebuild = false;
                }
            }

            if (needUpdate || needRebuild)
            {
                // With no simulation there is nothing to debounce against - rebuild immediately so
                // a bypassed processor recovers on the first control change.
                if (needRebuild || simulation == null || updateSamplesElapsed > delayUpdateSamples)
                {
                    UpdateSimulation();
                    needRebuild = false;
                    needUpdate = false;
                }
                else
                {
                    updateSamplesElapsed += numSamples;
                }
            }

            if (simulation == null)
            {
                Bypass(audioInputs, audioOutputs, numSamples);
                return;
            }

            try
            {
                simulation.Run(numSamples, audioInputs, audioOutputs);
            }
            catch (SimulationDiverged diverged)
            {
                // The circuit hit a NaN/Inf. Mirror the WPF app's policy: divergence after more
                // than a second of audio is likely a transient, so rebuild and keep going;
                // divergence almost immediately means the circuit is genuinely unstable, so stay
                // bypassed rather than thrash rebuilding every buffer.
                foreach (double[] channel in audioOutputs)
                    Array.Clear(channel, 0, numSamples);
                bool retry = diverged.At > sampleRate;
                simulation = null;
                if (retry)
                    needRebuild = true;
            }
        }
    }

    /// <summary>
    /// Pass the input through unprocessed. Every output channel is written, so a host with more
    /// outputs than the simulation drives does not keep replaying a stale buffer.
    /// </summary>
    private static void Bypass(double[][] audioInputs, double[][] audioOutputs, int numSamples)
    {
        for (int channel = 0; channel < audioOutputs.Length; ++channel)
        {
            double[] source = channel < audioInputs.Length ? audioInputs[channel] : audioInputs[0];
            // Copy numSamples, not the whole array: the two need not be the same length.
            Array.Copy(source, audioOutputs[channel], Math.Min(numSamples, Math.Min(source.Length, audioOutputs[channel].Length)));
        }
    }

    /// <summary>
    /// Build and publish the simulation synchronously. Offline rendering needs this (RunSimulation
    /// bypasses until a simulation exists), and live hosts can call it before starting the stream
    /// so the first audio callback finds the simulation compiled and ready.
    /// </summary>
    public void EnsureSimulationReady()
    {
        if (circuit == null)
            return;

        int id = Interlocked.Increment(ref update);
        Publish(BuildSimulation(), id);
        needRebuild = false;
        needUpdate = false;
    }

    private void SetCircuit(Circuit.Circuit newCircuit)
    {
        circuit = newCircuit;
        InteractiveComponents.Clear();

        Dictionary<string, ButtonWrapper> buttonGroups = new Dictionary<string, ButtonWrapper>();
        Dictionary<string, PotWrapper> potGroups = new Dictionary<string, PotWrapper>();

        foreach (Circuit.Component component in circuit.Components)
        {
            if (component is IPotControl pot)
                AddPotControl(component, pot, potGroups);
            else if (component is IButtonControl button)
                AddButtonControl(component, button, buttonGroups);
        }

        needRebuild = true;
    }

    private void AddPotControl(Circuit.Component component, IPotControl pot, Dictionary<string, PotWrapper> potGroups)
    {
        if (string.IsNullOrEmpty(pot.Group))
        {
            InteractiveComponents.Add(new PotWrapper(pot, component.Name));
        }
        else if (potGroups.TryGetValue(pot.Group, out PotWrapper? wrapper))
        {
            wrapper.AddSection(pot);
        }
        else
        {
            wrapper = new PotWrapper(pot, pot.Group);
            potGroups.Add(pot.Group, wrapper);
            InteractiveComponents.Add(wrapper);
        }
    }

    private void AddButtonControl(Circuit.Component component, IButtonControl button, Dictionary<string, ButtonWrapper> buttonGroups)
    {
        ButtonWrapper wrapper;
        if (string.IsNullOrEmpty(button.Group))
        {
            wrapper = button.NumPositions == 2
                ? new DoubleThrowWrapper(button, component.Name)
                : new MultiThrowWrapper(button, component.Name);
            InteractiveComponents.Add(wrapper);
        }
        else if (buttonGroups.ContainsKey(button.Group))
        {
            wrapper = buttonGroups[button.Group];
            wrapper.AddSection(button);
        }
        else
        {
            wrapper = button.NumPositions == 2
                ? new DoubleThrowWrapper(button, button.Group)
                : new MultiThrowWrapper(button, button.Group);
            buttonGroups[button.Group] = wrapper;
            InteractiveComponents.Add(wrapper);
        }
    }

    /// <summary>
    /// Build a simulation for the current circuit and settings, compiled and reset. Runs off the
    /// audio thread: Simulation compiles its inner loop lazily on first Run, a multi-millisecond
    /// stall if left to the audio callback. Warm it with one scratch sample to force the compile,
    /// then Reset so a freshly built simulation starts from the solution's initial conditions.
    /// </summary>
    private Simulation BuildSimulation()
    {
        TransientSolution solution = AudioSimulationFactory.Solve(circuit!, sampleRate, oversample);
        Simulation built = AudioSimulationFactory.Create(circuit!, solution, oversample, iterations);
        try
        {
            built.Run(1, new[] { new double[1] }, new[] { new double[1] });
        }
        catch (SimulationDiverged)
        {
            // The divergence guard also fires on sample 0, so a circuit with a bad DC operating
            // point throws here. Swallow it: the compile is what we came for, and the guard will
            // fire again during real playback where RunSimulation's policy can handle it. Letting
            // it escape would surface as a build failure rethrown into the audio callback,
            // bypassing that policy entirely.
        }
        built.Reset();
        return built;
    }

    /// <summary>
    /// Swap in a built simulation, carrying the running state over so the circuit does not reset
    /// (capacitors keep their charge, the sample clock keeps counting) when a control changes.
    /// The solve and the compile happen before this, off the audio thread; the lock here covers
    /// only the state copy and the reference swap. Note the copy is not free - it is proportional
    /// to the number of state variables - so the audio thread can briefly wait on it.
    ///
    /// A sample rate or oversample change alters the time step, which no state can survive; in
    /// that case the new simulation deliberately starts from its initial conditions rather than
    /// inheriting a sample clock that would place it at the wrong instant.
    /// </summary>
    private void Publish(Simulation built, int id)
    {
        lock (sync)
        {
            if (id <= clock)
                return;

            if (simulation != null && !built.CopyStateFrom(simulation))
                StateHandoffSkipped = true;
            simulation = built;
            clock = id;
        }
    }

    /// <summary>
    /// Set when a rebuild could not carry the previous simulation's state across, which means the
    /// circuit restarted from its initial conditions and a transient is expected.
    /// </summary>
    public bool StateHandoffSkipped { get; private set; }

    private void UpdateSimulation()
    {
        int id = Interlocked.Increment(ref update);
        new Task(() =>
        {
            try
            {
                if (circuit == null)
                    return;

                Publish(BuildSimulation(), id);
            }
            catch (Exception ex)
            {
                simulationUpdateException = ex;
            }
        }).Start(scheduler);
    }
}
