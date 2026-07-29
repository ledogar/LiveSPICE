using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Util;

namespace CoreAudio
{
    /// <summary>
    /// Core Audio driver for macOS. Audio.Driver.Drivers finds this by reflecting over loaded
    /// assemblies, so this type must stay public with a public parameterless constructor.
    /// </summary>
    public class Driver : Audio.Driver
    {
        public override string Name { get { return "Core Audio"; } }

        public Driver()
        {
            // The P/Invokes below only resolve on macOS. Leaving devices empty is the graceful
            // outcome elsewhere; throwing would be caught and logged as an error on every
            // enumeration.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return;

            uint[] ids;
            try
            {
                ids = CoreAudioApi.EnumerateDevices();
            }
            catch (Exception Ex)
            {
                Log.Global.WriteLine(MessageType.Error, "Error enumerating Core Audio devices: {0}", Ex.Message);
                return;
            }

            foreach (uint id in ids)
            {
                // One bad device shouldn't hide the rest.
                try
                {
                    Device device = new Device(id);
                    if (device.InputChannels.Length > 0 || device.OutputChannels.Length > 0)
                        devices.Add(device);
                }
                catch (Exception Ex)
                {
                    Log.Global.WriteLine(MessageType.Warning, "Error opening Core Audio device {0}: {1}", id, Ex.Message);
                }
            }

            Log.Global.WriteLine(MessageType.Info, "Found {0} Core Audio devices.", devices.Count);
        }
    }
}
