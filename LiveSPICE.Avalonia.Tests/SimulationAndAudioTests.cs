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

        Assert.Equal("Virtual Audio", driver.Name);
        Assert.Equal("Managed loopback", device.Name);
        Assert.Single(device.InputChannels);
        Assert.Single(device.OutputChannels);
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