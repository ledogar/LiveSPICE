using System;
using System.IO;
using System.Linq;
using System.Threading;
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
