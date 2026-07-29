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

        if (simulation == null && needRebuild && circuit != null)
        {
            UpdateSimulation();
            needRebuild = false;
        }

        if (circuit == null || simulation == null)
        {
            audioInputs[0].CopyTo(audioOutputs[0], 0);
            return;
        }

        lock (sync)
        {
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
                if (needRebuild || updateSamplesElapsed > delayUpdateSamples)
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
        built.Run(1, new[] { new double[1] }, new[] { new double[1] });
        built.Reset();
        return built;
    }

    /// <summary>
    /// Swap in a built simulation, carrying the running state over so the circuit does not reset
    /// (capacitors keep their charge, the sample clock keeps counting) when a control changes.
    /// The lock only covers the state copy and the reference swap, so the audio thread is never
    /// blocked behind a solve or a compile.
    /// </summary>
    private void Publish(Simulation built, int id)
    {
        lock (sync)
        {
            if (id <= clock)
                return;

            if (simulation != null)
                built.CopyStateFrom(simulation);
            simulation = built;
            clock = id;
        }
    }

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
