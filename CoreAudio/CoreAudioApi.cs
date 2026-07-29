using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CoreAudio
{
    /// <summary>
    /// Most Core Audio results are four character codes rather than a small dense enum, so unlike
    /// MMRESULT in WaveAudio there is nothing useful to name them with. Render the code as ASCII
    /// when it is printable, and fall back to the numeric value.
    /// </summary>
    public class CoreAudioException : Exception
    {
        private int status;
        public int Status { get { return status; } }

        public CoreAudioException(int Status) : base(FourCC(Status)) { status = Status; }
        public CoreAudioException(string Message, int Status) : base(Message + " (" + FourCC(Status) + ")") { status = Status; }

        public static void CheckThrow(int Status)
        {
            if (Status != NoError)
                throw new CoreAudioException(Status);
        }

        public static void CheckThrow(string Message, int Status)
        {
            if (Status != NoError)
                throw new CoreAudioException(Message, Status);
        }

        public const int NoError = 0;

        public static string FourCC(int Status)
        {
            byte[] bytes = new byte[]
            {
                (byte)((Status >> 24) & 0xFF),
                (byte)((Status >> 16) & 0xFF),
                (byte)((Status >> 8) & 0xFF),
                (byte)(Status & 0xFF),
            };
            foreach (byte i in bytes)
            {
                // Not a printable four character code, so the number is all we have.
                if (i < 0x20 || i > 0x7E)
                    return Status.ToString();
            }
            return "'" + Encoding.ASCII.GetString(bytes) + "' (" + Status.ToString() + ")";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioObjectPropertyAddress
    {
        public uint mSelector;
        public uint mScope;
        public uint mElement;

        public AudioObjectPropertyAddress(uint Selector, uint Scope)
        {
            mSelector = Selector;
            mScope = Scope;
            mElement = CoreAudioApi.kAudioObjectPropertyElementMain;
        }
    }

    /// <summary>
    /// The Core Audio HAL property API, used for device discovery. The AudioUnit API used to
    /// actually move samples is in AudioUnitApi.cs.
    /// </summary>
    internal static class CoreAudioApi
    {
        private const string CoreAudioFramework = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
        private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        public const uint kAudioObjectSystemObject = 1;
        public const uint kAudioObjectPropertyElementMain = 0;

        public static uint FourCC(string s)
        {
            return ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | (uint)s[3];
        }

        // Selectors and scopes. These are four character codes in the headers; spelling them out
        // here keeps them greppable against Apple's documentation.
        public static readonly uint kAudioHardwarePropertyDevices = FourCC("dev#");
        public static readonly uint kAudioHardwarePropertyDefaultInputDevice = FourCC("dIn ");
        public static readonly uint kAudioHardwarePropertyDefaultOutputDevice = FourCC("dOut");
        public static readonly uint kAudioObjectPropertyName = FourCC("lnam");
        public static readonly uint kAudioObjectPropertyElementName = FourCC("lchn");
        public static readonly uint kAudioDevicePropertyStreamConfiguration = FourCC("slay");
        public static readonly uint kAudioDevicePropertyNominalSampleRate = FourCC("nsrt");
        public static readonly uint kAudioDevicePropertyBufferFrameSize = FourCC("fsiz");

        public static readonly uint kAudioObjectPropertyScopeGlobal = FourCC("glob");
        public static readonly uint kAudioObjectPropertyScopeInput = FourCC("inpt");
        public static readonly uint kAudioObjectPropertyScopeOutput = FourCC("outp");

        [DllImport(CoreAudioFramework)]
        public static extern int AudioObjectGetPropertyDataSize(
            uint inObjectID, ref AudioObjectPropertyAddress inAddress,
            uint inQualifierDataSize, IntPtr inQualifierData, out uint outDataSize);

        [DllImport(CoreAudioFramework)]
        public static extern int AudioObjectGetPropertyData(
            uint inObjectID, ref AudioObjectPropertyAddress inAddress,
            uint inQualifierDataSize, IntPtr inQualifierData, ref uint ioDataSize, IntPtr outData);

        [DllImport(CoreAudioFramework)]
        public static extern int AudioObjectSetPropertyData(
            uint inObjectID, ref AudioObjectPropertyAddress inAddress,
            uint inQualifierDataSize, IntPtr inQualifierData, uint inDataSize, IntPtr inData);

        [DllImport(CoreFoundationFramework)]
        private static extern IntPtr CFStringGetCStringPtr(IntPtr theString, uint encoding);

        [DllImport(CoreFoundationFramework)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, long bufferSize, uint encoding);

        [DllImport(CoreFoundationFramework)]
        private static extern void CFRelease(IntPtr cf);

        private const uint kCFStringEncodingUTF8 = 0x08000100;

        /// <summary>Read a fixed-size property into a blittable struct.</summary>
        public static T GetProperty<T>(uint Object, uint Selector, uint Scope, uint Element = kAudioObjectPropertyElementMain) where T : struct
        {
            AudioObjectPropertyAddress address = new AudioObjectPropertyAddress(Selector, Scope) { mElement = Element };
            uint size = (uint)Marshal.SizeOf(typeof(T));
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                CoreAudioException.CheckThrow(AudioObjectGetPropertyData(Object, ref address, 0, IntPtr.Zero, ref size, buffer));
                return (T)Marshal.PtrToStructure(buffer, typeof(T));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static void SetProperty<T>(uint Object, uint Selector, uint Scope, T Value) where T : struct
        {
            AudioObjectPropertyAddress address = new AudioObjectPropertyAddress(Selector, Scope);
            uint size = (uint)Marshal.SizeOf(typeof(T));
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.StructureToPtr(Value, buffer, false);
                CoreAudioException.CheckThrow(AudioObjectSetPropertyData(Object, ref address, 0, IntPtr.Zero, size, buffer));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>Read a variable-size property into raw memory. Caller frees via Marshal.FreeHGlobal.</summary>
        public static IntPtr GetPropertyRaw(uint Object, uint Selector, uint Scope, out uint Size, uint Element = kAudioObjectPropertyElementMain)
        {
            AudioObjectPropertyAddress address = new AudioObjectPropertyAddress(Selector, Scope) { mElement = Element };
            CoreAudioException.CheckThrow(AudioObjectGetPropertyDataSize(Object, ref address, 0, IntPtr.Zero, out Size));
            IntPtr buffer = Marshal.AllocHGlobal((int)Math.Max(Size, 1));
            try
            {
                uint size = Size;
                CoreAudioException.CheckThrow(AudioObjectGetPropertyData(Object, ref address, 0, IntPtr.Zero, ref size, buffer));
                Size = size;
                return buffer;
            }
            catch
            {
                Marshal.FreeHGlobal(buffer);
                throw;
            }
        }

        /// <summary>Read a CFString property and marshal it to a managed string.</summary>
        public static string GetStringProperty(uint Object, uint Selector, uint Scope, uint Element = kAudioObjectPropertyElementMain)
        {
            IntPtr cfstring = IntPtr.Zero;
            AudioObjectPropertyAddress address = new AudioObjectPropertyAddress(Selector, Scope) { mElement = Element };
            uint size = (uint)IntPtr.Size;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                int status = AudioObjectGetPropertyData(Object, ref address, 0, IntPtr.Zero, ref size, buffer);
                if (status != CoreAudioException.NoError)
                    return null;
                cfstring = Marshal.ReadIntPtr(buffer);
                if (cfstring == IntPtr.Zero)
                    return null;
                return CFStringToString(cfstring);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                if (cfstring != IntPtr.Zero)
                    CFRelease(cfstring);
            }
        }

        private static string CFStringToString(IntPtr CFString)
        {
            // Fast path: the CFString may already have a UTF-8 backing store we can read directly.
            IntPtr ptr = CFStringGetCStringPtr(CFString, kCFStringEncodingUTF8);
            if (ptr != IntPtr.Zero)
                return Marshal.PtrToStringAnsi(ptr);

            byte[] bytes = new byte[1024];
            if (!CFStringGetCString(CFString, bytes, bytes.Length, kCFStringEncodingUTF8))
                return null;
            int length = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, length < 0 ? bytes.Length : length);
        }

        public static uint[] EnumerateDevices()
        {
            uint size;
            IntPtr buffer = GetPropertyRaw(kAudioObjectSystemObject, kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal, out size);
            try
            {
                uint[] devices = new uint[size / sizeof(uint)];
                for (int i = 0; i < devices.Length; ++i)
                    devices[i] = (uint)Marshal.ReadInt32(buffer, i * sizeof(uint));
                return devices;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Total channel count for a scope, summed over the device's streams. The property is an
        /// AudioBufferList: a uint count followed by that many AudioBuffer structs.
        /// </summary>
        public static int GetChannelCount(uint Device, uint Scope)
        {
            uint size;
            IntPtr buffer;
            try
            {
                buffer = GetPropertyRaw(Device, kAudioDevicePropertyStreamConfiguration, Scope, out size);
            }
            catch (CoreAudioException)
            {
                // A device with no streams in this scope may refuse the property outright.
                return 0;
            }
            try
            {
                if (size < sizeof(uint))
                    return 0;
                int count = Marshal.ReadInt32(buffer);
                int channels = 0;
                // AudioBuffer is { uint mNumberChannels; uint mDataByteSize; void* mData; }, but it
                // is laid out after a pointer-aligned mNumberBuffers field.
                int offset = IntPtr.Size;
                for (int i = 0; i < count; ++i)
                {
                    channels += Marshal.ReadInt32(buffer, offset);
                    offset += AudioBufferList.AudioBufferSize;
                }
                return channels;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static double GetSampleRate(uint Device)
        {
            return GetProperty<double>(Device, kAudioDevicePropertyNominalSampleRate, kAudioObjectPropertyScopeGlobal);
        }

        public static uint GetBufferFrameSize(uint Device)
        {
            return GetProperty<uint>(Device, kAudioDevicePropertyBufferFrameSize, kAudioObjectPropertyScopeGlobal);
        }

        public static string GetDeviceName(uint Device)
        {
            return GetStringProperty(Device, kAudioObjectPropertyName, kAudioObjectPropertyScopeGlobal) ?? ("Device " + Device);
        }

        /// <summary>
        /// Per-channel name. Most devices do not set one, so fall back to a positional name. Element
        /// numbering for channels is 1-based.
        /// </summary>
        public static string GetChannelName(uint Device, uint Scope, int Index)
        {
            string name = null;
            try
            {
                name = GetStringProperty(Device, kAudioObjectPropertyElementName, Scope, (uint)(Index + 1));
            }
            catch (CoreAudioException)
            {
            }
            string direction = Scope == kAudioObjectPropertyScopeInput ? "In" : "Out";
            string positional = direction + " " + (Index + 1);
            // Many devices set the element name to just the channel number, which adds nothing.
            if (string.IsNullOrEmpty(name) || name == (Index + 1).ToString())
                return positional;
            return positional + ": " + name;
        }
    }
}
