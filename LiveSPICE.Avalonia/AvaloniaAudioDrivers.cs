using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace LiveSPICE.Avalonia;

internal static class AvaloniaAudioDrivers
{
    public static IReadOnlyList<Audio.Driver> Available()
    {
        // Audio.Driver.Drivers discovers backends by reflecting over loaded assemblies, and
        // .NET only loads an assembly when a type from it is first used - a ProjectReference
        // alone is not enough. Touch the macOS backend so it is loaded before the scan.
        // (A discarded typeof gets elided in Release builds; GC.KeepAlive is a real call.)
        GC.KeepAlive(typeof(global::CoreAudio.Driver));

        List<Audio.Driver> drivers = new List<Audio.Driver>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            AddIfUsable(drivers, () => new LinuxAudioDriver());

        AddIfUsable(drivers, () => new VirtualAudioDriver());

        foreach (Audio.Driver driver in Audio.Driver.Drivers)
            if (!drivers.Any(i => string.Equals(i.Name, driver.Name, StringComparison.OrdinalIgnoreCase)))
                drivers.Add(driver);

        return drivers;
    }

    private static void AddIfUsable(List<Audio.Driver> drivers, Func<Audio.Driver> factory)
    {
        try
        {
            Audio.Driver driver = factory();
            if (!drivers.Any(i => string.Equals(i.Name, driver.Name, StringComparison.OrdinalIgnoreCase)))
                drivers.Add(driver);
        }
        catch
        {
        }
    }
}
