using Circuit;
using LiveSPICE.Avalonia;
using Xunit;

namespace LiveSPICE.Avalonia.Tests;

public sealed class SimulationAndAudioTests
{
    [Fact]
    public void AudioSimulationFactoryRunsPassiveHighpass()
    {
        Circuit.Circuit circuit = Schematic.Load(FindFixture("Tests/Circuits/Passive 1stOrder Highpass RC.schx")).Build();
        Simulation simulation = AudioSimulationFactory.Create(circuit, 48000, 4, 8);
        double[] input = Enumerable.Range(0, 512).Select(i => Math.Sin(2 * Math.PI * 440 * i / 48000)).ToArray();
        double[] output = new double[input.Length];

        simulation.Run(input.Length, new[] { input }, new[] { output });

        Assert.Contains(output, i => Math.Abs(i) > 1e-9);
        Assert.DoesNotContain(output, double.IsNaN);
    }

    [Fact]
    public void AudioSimulationFactoryRejectsCircuitWithoutInput()
    {
        Circuit.Circuit circuit = new Circuit.Circuit();

        Assert.Throws<NotSupportedException>(() => AudioSimulationFactory.Create(circuit, 48000, 4, 8));
    }

    [Fact]
    public void VirtualAudioDriverProvidesLoopbackDeviceAndChannels()
    {
        VirtualAudioDriver driver = new VirtualAudioDriver();
        Audio.Device device = driver.Devices.Single();

        Assert.Equal("LiveSPICE Virtual Audio", driver.Name);
        Assert.Equal("Managed loopback", device.Name);
        Assert.Single(device.InputChannels);
        Assert.Single(device.OutputChannels);
    }

    [Fact]
    public void LinuxAudioDiscoveryFiltersNonAudioPorts()
    {
        LinuxAudioDriver driver = new LinuxAudioDriver();

        Assert.Equal("PipeWire/JACK", driver.Name);
        foreach (Audio.Device device in driver.Devices)
            Assert.DoesNotContain(device.InputChannels.Concat(device.OutputChannels), i => i.Name.StartsWith("Midi-Bridge:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AvaloniaAudioDriversListsLinuxAndVirtualDriversOnce()
    {
        Audio.Driver[] drivers = AvaloniaAudioDrivers.Available().ToArray();
        string[] names = drivers.Select(i => i.Name).ToArray();

        Assert.Contains("PipeWire/JACK", names);
        Assert.Contains("LiveSPICE Virtual Audio", names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void WaveformWindowDiscoversNodeProbeCandidates()
    {
        Circuit.Circuit circuit = Schematic.Load(FindFixture("Tests/Circuits/Passive 1stOrder Highpass RC.schx")).Build();

        ProbeCandidate[] candidates = WaveformWindow.ProbeCandidates(circuit).ToArray();

        Assert.NotEmpty(candidates);
        Assert.DoesNotContain(candidates, i => string.IsNullOrWhiteSpace(i.Name) || i.Name == "0");
        Assert.DoesNotContain(candidates, i => i.Expression.EqualsZero());
    }

    [Fact]
    public void WaveformWindowGeneratesFallbackSineInput()
    {
        double[] input = new double[32];

        WaveformWindow.FillInputBuffer(input, 48000, 440, 1, null);

        Assert.Contains(input, i => Math.Abs(i) > 1e-9);
    }

    [Fact]
    public void WaveformWindowGeneratesExpressionInput()
    {
        double[] input = new double[8];

        WaveformWindow.FillInputBuffer(input, 48000, 440, 2, "0.125");

        Assert.All(input, i => Assert.Equal(0.25, i, 12));
    }

    [Fact]
    public void VirtualAudioStreamInvokesCallbackUntilStopped()
    {
        VirtualAudioDriver driver = new VirtualAudioDriver();
        Audio.Device device = driver.Devices.Single();
        using ManualResetEventSlim called = new ManualResetEventSlim(false);
        int callbackCount = 0;

        Audio.Stream stream = device.Open((count, input, output, rate) =>
        {
            callbackCount++;
            Assert.Equal(256, count);
            Assert.Equal(48000, rate);
            Assert.Single(input);
            Assert.Single(output);
            output[0][0] = 0.25;
            called.Set();
        }, device.InputChannels, device.OutputChannels);

        Assert.True(called.Wait(TimeSpan.FromSeconds(2)));
        stream.Stop();
        Assert.True(callbackCount > 0);
    }

    [Fact]
    public async Task LiveAudioProcessorCanRunOffUiThread()
    {
        Schematic schematic = Schematic.Load(FindFixture("Tests/Circuits/Passive 1stOrder Highpass RC.schx"));
        LiveAudioProcessor processor = new LiveAudioProcessor(schematic);
        processor.Start(48000, 8, 8);
        using Audio.SampleBuffer input = new Audio.SampleBuffer(128);
        using Audio.SampleBuffer output = new Audio.SampleBuffer(128);
        for (int i = 0; i < input.Samples.Length; i++)
            input[i] = Math.Sin(2 * Math.PI * 440 * i / 48000);

        await Task.Run(() => processor.Process(128, new[] { input }, new[] { output }, 48000));

        Assert.Contains(output.Samples, i => Math.Abs(i) > 1e-12);
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