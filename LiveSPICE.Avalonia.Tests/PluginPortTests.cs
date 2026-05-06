using AudioPlugSharp;
using LiveSPICE.PluginCore;
using LiveSPICE.PluginLinux;
using Xunit;

namespace LiveSPICE.Avalonia.Tests;

public class PluginPortTests
{
    [Fact]
    public void LinuxPluginInitializesMonoPorts()
    {
        LiveSPICELinuxPlugin plugin = new LiveSPICELinuxPlugin();

        plugin.Initialize();

        Assert.Single(plugin.InputPorts);
        Assert.Single(plugin.OutputPorts);
        Assert.Equal(EAudioChannelConfiguration.Mono, plugin.InputPorts[0].ChannelConfiguration);
        Assert.Equal(EAudioChannelConfiguration.Mono, plugin.OutputPorts[0].ChannelConfiguration);
        Assert.False(plugin.HasUserInterface);
    }

    [Fact]
    public void PluginProgramParametersRoundTripProcessorSettings()
    {
        SimulationProcessor processor = new SimulationProcessor
        {
            Oversample = 4,
            Iterations = 16,
        };

        PluginProgramParameters parameters = PluginProgramParameters.FromProcessor(processor);
        SimulationProcessor restored = new SimulationProcessor();
        parameters.ApplyTo(restored);

        Assert.Equal(4, restored.Oversample);
        Assert.Equal(16, restored.Iterations);
    }

    [Fact]
    public void LinuxPluginStateRoundTripsProcessorSettings()
    {
        LiveSPICELinuxPlugin plugin = new LiveSPICELinuxPlugin();
        plugin.SimulationProcessor.Oversample = 8;
        plugin.SimulationProcessor.Iterations = 32;

        byte[] state = plugin.SaveState();
        LiveSPICELinuxPlugin restored = new LiveSPICELinuxPlugin();
        restored.RestoreState(state);

        Assert.Equal(8, restored.SimulationProcessor.Oversample);
        Assert.Equal(32, restored.SimulationProcessor.Iterations);
    }
}
