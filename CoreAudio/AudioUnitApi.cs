using System;
using System.Runtime.InteropServices;

namespace CoreAudio
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioComponentDescription
    {
        public uint componentType;
        public uint componentSubType;
        public uint componentManufacturer;
        public uint componentFlags;
        public uint componentFlagsMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioStreamBasicDescription
    {
        public double mSampleRate;
        public uint mFormatID;
        public uint mFormatFlags;
        public uint mBytesPerPacket;
        public uint mFramesPerPacket;
        public uint mBytesPerFrame;
        public uint mChannelsPerFrame;
        public uint mBitsPerChannel;
        public uint mReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioTimeStamp
    {
        public double mSampleTime;
        public ulong mHostTime;
        public double mRateScalar;
        public ulong mWordClockTime;
        public long mSMPTETime1;
        public long mSMPTETime2;
        public long mSMPTETime3;
        public uint mFlags;
        public uint mReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AURenderCallbackStruct
    {
        public AudioUnitApi.AURenderCallback inputProc;
        public IntPtr inputProcRefCon;
    }

    /// <summary>
    /// Helper for building and reading the variable-length AudioBufferList structure by hand.
    /// The layout is { uint mNumberBuffers; AudioBuffer mBuffers[]; } where AudioBuffer is
    /// { uint mNumberChannels; uint mDataByteSize; void* mData; }. The list is pointer-aligned,
    /// so the first buffer starts at IntPtr.Size, not at 4.
    /// </summary>
    internal static class AudioBufferList
    {
        // uint + uint + pointer, rounded up to pointer alignment.
        public static readonly int AudioBufferSize = IntPtr.Size == 8 ? 16 : 12;

        public static int SizeOf(int Buffers)
        {
            return IntPtr.Size + Buffers * AudioBufferSize;
        }

        /// <summary>Allocate a zeroed AudioBufferList for the given number of non-interleaved buffers.</summary>
        public static IntPtr Allocate(int Buffers)
        {
            int size = SizeOf(Buffers);
            IntPtr list = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; ++i)
                Marshal.WriteByte(list, i, 0);
            Marshal.WriteInt32(list, 0, Buffers);
            return list;
        }

        public static int GetCount(IntPtr List)
        {
            return Marshal.ReadInt32(List, 0);
        }

        public static void SetCount(IntPtr List, int Count)
        {
            Marshal.WriteInt32(List, 0, Count);
        }

        private static int Offset(int Index)
        {
            return IntPtr.Size + Index * AudioBufferSize;
        }

        public static void SetBuffer(IntPtr List, int Index, int Channels, int ByteSize, IntPtr Data)
        {
            int offset = Offset(Index);
            Marshal.WriteInt32(List, offset, Channels);
            Marshal.WriteInt32(List, offset + 4, ByteSize);
            Marshal.WriteIntPtr(List, offset + 8, Data);
        }

        public static IntPtr GetData(IntPtr List, int Index)
        {
            return Marshal.ReadIntPtr(List, Offset(Index) + 8);
        }

        public static int GetDataByteSize(IntPtr List, int Index)
        {
            return Marshal.ReadInt32(List, Offset(Index) + 4);
        }

        public static int GetChannels(IntPtr List, int Index)
        {
            return Marshal.ReadInt32(List, Offset(Index));
        }

        public static void SetDataByteSize(IntPtr List, int Index, int ByteSize)
        {
            Marshal.WriteInt32(List, Offset(Index) + 4, ByteSize);
        }
    }

    internal static class AudioUnitApi
    {
        private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int AURenderCallback(
            IntPtr inRefCon, ref uint ioActionFlags, ref AudioTimeStamp inTimeStamp,
            uint inBusNumber, uint inNumberFrames, IntPtr ioData);

        public static uint FourCC(string s) { return CoreAudioApi.FourCC(s); }

        public static readonly uint kAudioUnitType_Output = FourCC("auou");
        public static readonly uint kAudioUnitSubType_HALOutput = FourCC("ahal");
        public static readonly uint kAudioUnitManufacturer_Apple = FourCC("appl");
        public static readonly uint kAudioFormatLinearPCM = FourCC("lpcm");

        public const uint kAudioFormatFlagIsFloat = 1 << 0;
        public const uint kAudioFormatFlagIsPacked = 1 << 3;
        public const uint kAudioFormatFlagIsNonInterleaved = 1 << 5;

        public const uint kAudioUnitProperty_StreamFormat = 8;
        public const uint kAudioUnitProperty_SetRenderCallback = 23;
        public const uint kAudioUnitProperty_MaximumFramesPerSlice = 14;
        public const uint kAudioOutputUnitProperty_CurrentDevice = 2000;
        public const uint kAudioOutputUnitProperty_EnableIO = 2003;
        public const uint kAudioOutputUnitProperty_SetInputCallback = 2005;

        public const uint kAudioUnitScope_Global = 0;
        public const uint kAudioUnitScope_Input = 1;
        public const uint kAudioUnitScope_Output = 2;

        // AUHAL bus numbering: bus 0 is output to the device, bus 1 is input from it.
        public const uint OutputBus = 0;
        public const uint InputBus = 1;

        [DllImport(AudioToolbox)]
        public static extern IntPtr AudioComponentFindNext(IntPtr inComponent, ref AudioComponentDescription inDesc);

        [DllImport(AudioToolbox)]
        public static extern int AudioComponentInstanceNew(IntPtr inComponent, out IntPtr outInstance);

        [DllImport(AudioToolbox)]
        public static extern int AudioComponentInstanceDispose(IntPtr inInstance);

        [DllImport(AudioToolbox)]
        public static extern int AudioUnitInitialize(IntPtr inUnit);

        [DllImport(AudioToolbox)]
        public static extern int AudioUnitUninitialize(IntPtr inUnit);

        [DllImport(AudioToolbox)]
        public static extern int AudioOutputUnitStart(IntPtr ci);

        [DllImport(AudioToolbox)]
        public static extern int AudioOutputUnitStop(IntPtr ci);

        [DllImport(AudioToolbox)]
        public static extern int AudioUnitRender(
            IntPtr inUnit, ref uint ioActionFlags, ref AudioTimeStamp inTimeStamp,
            uint inOutputBusNumber, uint inNumberFrames, IntPtr ioData);

        [DllImport(AudioToolbox)]
        public static extern int AudioUnitSetProperty(
            IntPtr inUnit, uint inID, uint inScope, uint inElement, ref uint inData, uint inDataSize);

        [DllImport(AudioToolbox)]
        public static extern int AudioUnitSetProperty(
            IntPtr inUnit, uint inID, uint inScope, uint inElement, ref AudioStreamBasicDescription inData, uint inDataSize);

        [DllImport(AudioToolbox)]
        public static extern int AudioUnitSetProperty(
            IntPtr inUnit, uint inID, uint inScope, uint inElement, ref AURenderCallbackStruct inData, uint inDataSize);

        [DllImport(AudioToolbox)]
        public static extern int AudioUnitGetProperty(
            IntPtr inUnit, uint inID, uint inScope, uint inElement, ref AudioStreamBasicDescription outData, ref uint ioDataSize);

        /// <summary>The canonical non-interleaved 32 bit float format, one buffer per channel.</summary>
        public static AudioStreamBasicDescription NonInterleavedFloat32(double SampleRate, int Channels)
        {
            return new AudioStreamBasicDescription()
            {
                mSampleRate = SampleRate,
                mFormatID = kAudioFormatLinearPCM,
                mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagIsNonInterleaved,
                // For non-interleaved formats these are per-channel, so they describe one buffer.
                mBytesPerPacket = sizeof(float),
                mFramesPerPacket = 1,
                mBytesPerFrame = sizeof(float),
                mChannelsPerFrame = (uint)Channels,
                mBitsPerChannel = 8 * sizeof(float),
                mReserved = 0,
            };
        }
    }
}
