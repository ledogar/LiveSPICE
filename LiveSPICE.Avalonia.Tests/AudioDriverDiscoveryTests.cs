using System.Linq;
using System.Runtime.InteropServices;
using Xunit;

namespace LiveSPICE.Avalonia.Tests;

public class AudioDriverDiscoveryTests
{
    [Fact]
    public void CoreAudioDriverIsDiscoveredOnMacOS()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        // The driver must appear even with no audio hardware (CI runners), so assert on the
        // driver, not its devices.
        var drivers = AvaloniaAudioDrivers.Available();
        Assert.Contains(drivers, d => d.Name == "Core Audio");
    }

    [Fact]
    public void VirtualDriverIsAlwaysAvailable()
    {
        var drivers = AvaloniaAudioDrivers.Available();
        Assert.Contains(drivers, d => d.Devices.Any(dev => dev.OutputChannels.Length > 0));
    }
}
