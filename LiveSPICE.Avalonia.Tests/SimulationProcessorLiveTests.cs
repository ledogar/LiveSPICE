using System;
using System.IO;
using System.Linq;
using System.Threading;
using Circuit;
using LiveSPICE.PluginCore;
using Xunit;

namespace LiveSPICE.Avalonia.Tests;

public class SimulationProcessorLiveTests
{
    [Fact]
    public void EnsureSimulationReadyProducesRealAudioImmediately()
    {
        SimulationProcessor processor = new SimulationProcessor { Oversample = 4, Iterations = 8 };
        processor.LoadSchematic(FindFixture("Tests/Circuits/Passive 1stOrder Highpass RC.schx"));
        processor.SampleRate = 48000;

        processor.EnsureSimulationReady();
        Assert.True(processor.SimulationReady);

        double[] input = Enumerable.Range(0, 512).Select(i => 0.5 * Math.Sin(2 * Math.PI * 440 * i / 48000.0)).ToArray();
        double[] output = new double[512];
        processor.RunSimulation(new[] { input }, new[] { output }, 512);

        // Real simulation output on the very first call - not the not-ready bypass, which would
        // copy the input through verbatim.
        Assert.Contains(output, i => Math.Abs(i) > 1e-9);
        Assert.NotEqual(input, output);
        Assert.DoesNotContain(output, double.IsNaN);
    }

    [Fact]
    public void PotChangeRebuildsOffThreadWhileAudioKeepsRunning()
    {
        SimulationProcessor processor = new SimulationProcessor { Oversample = 2, Iterations = 8 };
        processor.LoadSchematic(FindFixture("Tests/Circuits/59 Bassman Preamp.schx"));
        processor.SampleRate = 48000;
        processor.EnsureSimulationReady();

        PotWrapper pot = processor.InteractiveComponents.OfType<PotWrapper>().First();

        double[] input = Enumerable.Range(0, 512).Select(i => 0.1 * Math.Sin(2 * Math.PI * 220 * i / 48000.0)).ToArray();
        double[] output = new double[512];

        // Move a pot mid-stream, then keep the "audio thread" running long enough to cover the
        // update debounce (0.1 s = ~10 buffers) and the background solve + publish. The swap
        // carries state across (CopyStateFrom), so audio must stay continuous, finite, and free
        // of exceptions throughout.
        pot.PotValue = 0.25;
        for (int buffer = 0; buffer < 60; ++buffer)
        {
            processor.RunSimulation(new[] { input }, new[] { output }, 512);
            Assert.DoesNotContain(output, double.IsNaN);
            Thread.Sleep(5);
        }
        Assert.True(processor.SimulationReady);
        Assert.Contains(output, i => Math.Abs(i) > 1e-12);
    }

    [Fact]
    public void PotChangeActuallyChangesTheAudioAndCarriesStateAcross()
    {
        // The weaker version of this test (NaN-free and non-silent) would pass even if the pot
        // change were dropped, the rebuild never happened, or the state handoff copied nothing.
        SimulationProcessor processor = new SimulationProcessor { Oversample = 2, Iterations = 8 };
        processor.LoadSchematic(FindFixture("Tests/Examples/Ibanez Tube Screamer TS-9.schx"));
        processor.SampleRate = 48000;
        processor.EnsureSimulationReady();

        PotWrapper pot = processor.InteractiveComponents.OfType<PotWrapper>().First();
        pot.PotValue = 0.9;
        double[] before = RunUntilStable(processor, 40);

        pot.PotValue = 0.1;
        double[] after = RunUntilStable(processor, 40);

        // The rebuild must have taken effect: a large pot swing has to move the output.
        double delta = before.Zip(after, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(delta > 1e-6, $"Pot change did not alter the output (max delta {delta:G4}).");

        // And the handoff must not have reset the circuit: a state reset shows up as a step
        // discontinuity at the swap far larger than the signal's own sample-to-sample motion.
        double biggestStep = 0, typicalStep = 0;
        for (int i = 1; i < after.Length; ++i)
        {
            double step = Math.Abs(after[i] - after[i - 1]);
            biggestStep = Math.Max(biggestStep, step);
            typicalStep += step;
        }
        typicalStep /= after.Length - 1;
        Assert.True(biggestStep < typicalStep * 50 + 1e-9,
            $"Output has a discontinuity suggesting the circuit state was reset " +
            $"(largest step {biggestStep:G4} vs typical {typicalStep:G4}).");
    }

    [Fact]
    public void StateHandoffCarriesTheClockOnlyWhenTheTimeStepMatches()
    {
        // Simulation state is keyed by expressions with the timestep baked in as a literal, so a
        // differing timestep transfers no state at all. Carrying the sample counter anyway would
        // jump t discontinuously - a 60 s session at 44.1 kHz would resume at 55 s under 48 kHz.
        Circuit.Circuit circuit = Schematic.Load(FindFixture("Tests/Circuits/Passive 1stOrder Highpass RC.schx")).Build();

        Simulation source = AudioSimulationFactory.Create(circuit, 44100, 2, 8);
        source.Run(new double[1024], new double[1024]);
        Assert.Equal(1024, source.At);

        Simulation sameRate = AudioSimulationFactory.Create(circuit, 44100, 2, 8);
        Assert.True(sameRate.CopyStateFrom(source), "Matching timesteps should report a successful handoff.");
        Assert.Equal(source.At, sameRate.At);

        Simulation differentRate = AudioSimulationFactory.Create(circuit, 48000, 2, 8);
        Assert.False(differentRate.CopyStateFrom(source), "A differing timestep cannot transfer state.");
        Assert.Equal(0, differentRate.At);

        Simulation differentOversample = AudioSimulationFactory.Create(circuit, 44100, 4, 8);
        Assert.False(differentOversample.CopyStateFrom(source), "A differing oversample changes the timestep too.");
        Assert.Equal(0, differentOversample.At);
    }

    [Fact]
    public void ControlChangesAreObservedEvenWhileBypassed()
    {
        // While no simulation is published RunSimulation bypasses. It must still consume the
        // interactive-component flags, otherwise a diverged circuit can never be revived by
        // turning a pot down - the only in-session route back to a rebuild.
        SimulationProcessor processor = new SimulationProcessor { Oversample = 2, Iterations = 8 };
        processor.LoadSchematic(FindFixture("Tests/Examples/Ibanez Tube Screamer TS-9.schx"));
        processor.SampleRate = 48000;
        Assert.False(processor.SimulationReady);

        PotWrapper pot = processor.InteractiveComponents.OfType<PotWrapper>().First();
        pot.PotValue = 0.4;
        Assert.True(pot.NeedUpdate);

        double[] input = new double[512];
        double[] output = new double[512];
        processor.RunSimulation(new[] { input }, new[] { output }, 512);

        Assert.False(pot.NeedUpdate, "The control flag was never consumed, so the change is lost.");
    }

    /// <summary>Run several buffers and return the last one, letting background rebuilds land.</summary>
    private static double[] RunUntilStable(SimulationProcessor processor, int buffers)
    {
        double[] input = Enumerable.Range(0, 512)
            .Select(i => 0.2 * Math.Sin(2 * Math.PI * 220 * i / 48000.0)).ToArray();
        double[] output = new double[512];
        for (int buffer = 0; buffer < buffers; ++buffer)
        {
            processor.RunSimulation(new[] { input }, new[] { output }, 512);
            Thread.Sleep(5);
        }
        return (double[])output.Clone();
    }

    private static string FindFixture(string relativePath)
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate test fixture.", relativePath);
    }
}
