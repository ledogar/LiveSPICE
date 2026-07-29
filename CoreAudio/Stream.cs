using System;
using System.Runtime.InteropServices;
using Util;

namespace CoreAudio
{
    /// <summary>
    /// An AUHAL stream. A single AudioUnit bound to one device drives everything: the render
    /// callback on the output bus pulls input from the input bus first, so there is one clock and
    /// no ring buffer. That requires input and output to be the same device, which is what
    /// Audio.Device.Open already assumes.
    /// </summary>
    internal class Stream : Audio.Stream
    {
        private IntPtr unit = IntPtr.Zero;

        private Channel[] input;
        private Channel[] output;
        private Audio.SampleBuffer[] inputBuffers;
        private Audio.SampleBuffer[] outputBuffers;

        // Scratch AudioBufferList that AudioUnitRender fills with the device's input.
        private IntPtr inputList = IntPtr.Zero;
        private int deviceInputChannels;
        private int deviceOutputChannels;

        private int bufferFrames;
        private double sampleRate;
        public override double SampleRate { get { return sampleRate; } }

        private SampleHandler callback;

        // Holding the delegate in a field is what keeps the native thunk alive; if this were only
        // a local, the GC could collect the delegate while Core Audio still held its function
        // pointer. It must outlive AudioComponentInstanceDispose.
        private readonly AudioUnitApi.AURenderCallback renderCallback;

        private volatile bool running = false;
        private volatile int oversizedCallbacks = 0;
        /// <summary>Number of callbacks that asked for more frames than were allocated.</summary>
        public int OversizedCallbacks { get { return oversizedCallbacks; } }

        public Stream(Device Device, SampleHandler Callback, Channel[] Input, Channel[] Output)
            : base(Input, Output)
        {
            callback = Callback;
            input = Input;
            output = Output;

            deviceInputChannels = Device.InputChannels.Length;
            deviceOutputChannels = Device.OutputChannels.Length;

            if (deviceOutputChannels == 0)
                throw new NotSupportedException(
                    "'" + Device.Name + "' has no output channels. Input-only devices need an Aggregate Device; " +
                    "combine the input and output in Audio MIDI Setup and select that instead.");
            if (Input.Length > 0 && deviceInputChannels == 0)
                throw new NotSupportedException("'" + Device.Name + "' has no input channels.");

            sampleRate = CoreAudioApi.GetSampleRate(Device.Id);

            try
            {
                bufferFrames = (int)CoreAudioApi.GetBufferFrameSize(Device.Id);
            }
            catch (CoreAudioException)
            {
                bufferFrames = 512;
            }
            if (bufferFrames <= 0)
                bufferFrames = 512;

            renderCallback = new AudioUnitApi.AURenderCallback(Render);

            try
            {
                Open(Device);
            }
            catch
            {
                Teardown();
                throw;
            }

            Log.Global.WriteLine(MessageType.Info,
                "Core Audio stream opened on '{0}': {1} Hz, {2} frames, {3} in, {4} out",
                Device.Name, sampleRate, bufferFrames, Input.Length, Output.Length);
        }

        private void Open(Device Device)
        {
            AudioComponentDescription desc = new AudioComponentDescription()
            {
                componentType = AudioUnitApi.kAudioUnitType_Output,
                componentSubType = AudioUnitApi.kAudioUnitSubType_HALOutput,
                componentManufacturer = AudioUnitApi.kAudioUnitManufacturer_Apple,
                componentFlags = 0,
                componentFlagsMask = 0,
            };
            IntPtr component = AudioUnitApi.AudioComponentFindNext(IntPtr.Zero, ref desc);
            if (component == IntPtr.Zero)
                throw new CoreAudioException("No HAL output AudioUnit available", 0);

            CoreAudioException.CheckThrow("AudioComponentInstanceNew",
                AudioUnitApi.AudioComponentInstanceNew(component, out unit));

            uint enable = 1;
            uint disable = 0;

            // Output is always enabled: the output bus render callback is what drives the stream.
            CoreAudioException.CheckThrow("EnableIO(output)", AudioUnitApi.AudioUnitSetProperty(
                unit, AudioUnitApi.kAudioOutputUnitProperty_EnableIO, AudioUnitApi.kAudioUnitScope_Output,
                AudioUnitApi.OutputBus, ref enable, sizeof(uint)));

            bool captureInput = input.Length > 0;
            uint inputEnable = captureInput ? enable : disable;
            CoreAudioException.CheckThrow("EnableIO(input)", AudioUnitApi.AudioUnitSetProperty(
                unit, AudioUnitApi.kAudioOutputUnitProperty_EnableIO, AudioUnitApi.kAudioUnitScope_Input,
                AudioUnitApi.InputBus, ref inputEnable, sizeof(uint)));

            uint deviceId = Device.Id;
            CoreAudioException.CheckThrow("CurrentDevice", AudioUnitApi.AudioUnitSetProperty(
                unit, AudioUnitApi.kAudioOutputUnitProperty_CurrentDevice, AudioUnitApi.kAudioUnitScope_Global,
                0, ref deviceId, sizeof(uint)));

            // Non-interleaved float32 in both directions. This is the format Audio.Util's
            // converters expect: one contiguous buffer per channel, unit stride.
            AudioStreamBasicDescription outputFormat =
                AudioUnitApi.NonInterleavedFloat32(sampleRate, deviceOutputChannels);
            CoreAudioException.CheckThrow("StreamFormat(output)", AudioUnitApi.AudioUnitSetProperty(
                unit, AudioUnitApi.kAudioUnitProperty_StreamFormat, AudioUnitApi.kAudioUnitScope_Input,
                AudioUnitApi.OutputBus, ref outputFormat, (uint)Marshal.SizeOf(typeof(AudioStreamBasicDescription))));

            if (captureInput)
            {
                AudioStreamBasicDescription inputFormat =
                    AudioUnitApi.NonInterleavedFloat32(sampleRate, deviceInputChannels);
                CoreAudioException.CheckThrow("StreamFormat(input)", AudioUnitApi.AudioUnitSetProperty(
                    unit, AudioUnitApi.kAudioUnitProperty_StreamFormat, AudioUnitApi.kAudioUnitScope_Output,
                    AudioUnitApi.InputBus, ref inputFormat, (uint)Marshal.SizeOf(typeof(AudioStreamBasicDescription))));
            }

            uint maxFrames = (uint)bufferFrames;
            CoreAudioException.CheckThrow("MaximumFramesPerSlice", AudioUnitApi.AudioUnitSetProperty(
                unit, AudioUnitApi.kAudioUnitProperty_MaximumFramesPerSlice, AudioUnitApi.kAudioUnitScope_Global,
                0, ref maxFrames, sizeof(uint)));

            AURenderCallbackStruct cb = new AURenderCallbackStruct()
            {
                inputProc = renderCallback,
                inputProcRefCon = IntPtr.Zero,
            };
            CoreAudioException.CheckThrow("SetRenderCallback", AudioUnitApi.AudioUnitSetProperty(
                unit, AudioUnitApi.kAudioUnitProperty_SetRenderCallback, AudioUnitApi.kAudioUnitScope_Input,
                AudioUnitApi.OutputBus, ref cb, (uint)Marshal.SizeOf(typeof(AURenderCallbackStruct))));

            // Allocate everything up front. SampleBuffer pins its array for life, so allocating in
            // the render callback would both fragment the heap and block on the GC.
            inputBuffers = new Audio.SampleBuffer[input.Length];
            for (int i = 0; i < inputBuffers.Length; ++i)
                inputBuffers[i] = new Audio.SampleBuffer(bufferFrames);
            outputBuffers = new Audio.SampleBuffer[output.Length];
            for (int i = 0; i < outputBuffers.Length; ++i)
                outputBuffers[i] = new Audio.SampleBuffer(bufferFrames);

            if (captureInput)
            {
                inputList = AudioBufferList.Allocate(deviceInputChannels);
                for (int i = 0; i < deviceInputChannels; ++i)
                {
                    IntPtr data = Marshal.AllocHGlobal(bufferFrames * sizeof(float));
                    AudioBufferList.SetBuffer(inputList, i, 1, bufferFrames * sizeof(float), data);
                }
            }

            CoreAudioException.CheckThrow("AudioUnitInitialize", AudioUnitApi.AudioUnitInitialize(unit));
            running = true;
            CoreAudioException.CheckThrow("AudioOutputUnitStart", AudioUnitApi.AudioOutputUnitStart(unit));
        }

        /// <summary>
        /// The render callback. Runs on Core Audio's realtime thread: no allocation, no logging,
        /// no locking, and no managed exception may escape back into native code.
        /// </summary>
        private int Render(IntPtr inRefCon, ref uint ioActionFlags, ref AudioTimeStamp inTimeStamp,
                           uint inBusNumber, uint inNumberFrames, IntPtr ioData)
        {
            try
            {
                int frames = (int)inNumberFrames;
                if (frames > bufferFrames)
                {
                    // Should not happen given MaximumFramesPerSlice, but truncating is far better
                    // than overrunning the pinned buffers.
                    oversizedCallbacks++;
                    frames = bufferFrames;
                }

                if (!running || ioData == IntPtr.Zero)
                {
                    SilenceOutput(ioData, (int)inNumberFrames);
                    return CoreAudioException.NoError;
                }

                if (inputBuffers.Length > 0)
                {
                    uint flags = 0;
                    // Reset the sizes: AudioUnitRender both reads and writes them.
                    for (int i = 0; i < deviceInputChannels; ++i)
                        AudioBufferList.SetDataByteSize(inputList, i, frames * sizeof(float));

                    int status = AudioUnitApi.AudioUnitRender(
                        unit, ref flags, ref inTimeStamp, AudioUnitApi.InputBus, (uint)frames, inputList);
                    if (status != CoreAudioException.NoError)
                    {
                        for (int i = 0; i < inputBuffers.Length; ++i)
                            inputBuffers[i].Clear();
                    }
                    else
                    {
                        for (int i = 0; i < input.Length; ++i)
                            Audio.Util.LEf32ToLEf64(
                                AudioBufferList.GetData(inputList, input[i].Index),
                                inputBuffers[i].Raw,
                                (uint)frames);
                    }
                }

                callback(frames, inputBuffers, outputBuffers, sampleRate);

                // Zero every device channel first, so channels the user didn't select stay silent.
                SilenceOutput(ioData, (int)inNumberFrames);

                for (int i = 0; i < output.Length; ++i)
                {
                    int ch = output[i].Index;
                    if (ch >= AudioBufferList.GetCount(ioData))
                        continue;
                    // LEf64ToLEf32 does not clamp, and neither does Core Audio.
                    double[] samples = outputBuffers[i].Samples;
                    for (int j = 0; j < frames; ++j)
                    {
                        double s = samples[j];
                        if (s > 1.0) samples[j] = 1.0;
                        else if (s < -1.0) samples[j] = -1.0;
                        else if (double.IsNaN(s)) samples[j] = 0.0;
                    }
                    Audio.Util.LEf64ToLEf32(
                        outputBuffers[i].Raw,
                        AudioBufferList.GetData(ioData, ch),
                        (uint)frames);
                }
            }
            catch (Exception)
            {
                // An exception crossing back into a Core Audio IOProc is fatal to the process.
                SilenceOutput(ioData, (int)inNumberFrames);
            }
            return CoreAudioException.NoError;
        }

        private static void SilenceOutput(IntPtr ioData, int Frames)
        {
            if (ioData == IntPtr.Zero)
                return;
            int count = AudioBufferList.GetCount(ioData);
            for (int i = 0; i < count; ++i)
            {
                IntPtr data = AudioBufferList.GetData(ioData, i);
                if (data != IntPtr.Zero)
                    Audio.Util.ZeroMemory(data, (uint)(Frames * sizeof(float)));
            }
        }

        public override void Stop()
        {
            running = false;
            Teardown();
            Log.Global.WriteLine(MessageType.Info, "Core Audio stream stopped.");
        }

        private void Teardown()
        {
            // Stop the unit before freeing anything the callback touches. AudioOutputUnitStop does
            // not return until the IOProc has finished, so after this the buffers are safe to free.
            // Teardown calls are unchecked so a failure here can't mask the original error.
            if (unit != IntPtr.Zero)
            {
                AudioUnitApi.AudioOutputUnitStop(unit);
                AudioUnitApi.AudioUnitUninitialize(unit);
                AudioUnitApi.AudioComponentInstanceDispose(unit);
                unit = IntPtr.Zero;
            }

            if (inputList != IntPtr.Zero)
            {
                for (int i = 0; i < deviceInputChannels; ++i)
                {
                    IntPtr data = AudioBufferList.GetData(inputList, i);
                    if (data != IntPtr.Zero)
                        Marshal.FreeHGlobal(data);
                }
                Marshal.FreeHGlobal(inputList);
                inputList = IntPtr.Zero;
            }

            if (inputBuffers != null)
            {
                foreach (Audio.SampleBuffer i in inputBuffers)
                    i.Dispose();
                inputBuffers = new Audio.SampleBuffer[] { };
            }
            if (outputBuffers != null)
            {
                foreach (Audio.SampleBuffer i in outputBuffers)
                    i.Dispose();
                outputBuffers = new Audio.SampleBuffer[] { };
            }
        }
    }
}
