using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreAudio
{
    /// <summary>
    /// A single channel of a Core Audio device. Name must be stable and unique within the device,
    /// because channel selections are persisted and restored by name.
    /// </summary>
    internal class Channel : Audio.Channel
    {
        private string name;
        public override string Name { get { return name; } }

        /// <summary>Index of this channel within its scope, used to find the buffer in the callback.</summary>
        public int Index { get; private set; }

        public Channel(string Name, int Index)
        {
            name = Name;
            this.Index = Index;
        }

        public override string ToString() { return name; }
    }

    internal class Device : Audio.Device
    {
        private uint id;
        public uint Id { get { return id; } }

        public Device(uint Id) : base(CoreAudioApi.GetDeviceName(Id))
        {
            id = Id;

            int inputCount = CoreAudioApi.GetChannelCount(Id, CoreAudioApi.kAudioObjectPropertyScopeInput);
            int outputCount = CoreAudioApi.GetChannelCount(Id, CoreAudioApi.kAudioObjectPropertyScopeOutput);

            inputs = Enumerable.Range(0, inputCount)
                .Select(i => (Audio.Channel)new Channel(CoreAudioApi.GetChannelName(Id, CoreAudioApi.kAudioObjectPropertyScopeInput, i), i))
                .ToArray();
            outputs = Enumerable.Range(0, outputCount)
                .Select(i => (Audio.Channel)new Channel(CoreAudioApi.GetChannelName(Id, CoreAudioApi.kAudioObjectPropertyScopeOutput, i), i))
                .ToArray();
        }

        public double SampleRate { get { return CoreAudioApi.GetSampleRate(id); } }

        public override Audio.Stream Open(Audio.Stream.SampleHandler Callback, Audio.Channel[] Input, Audio.Channel[] Output)
        {
            return new Stream(
                this,
                Callback,
                Input.Cast<Channel>().ToArray(),
                Output.Cast<Channel>().ToArray());
        }
    }
}
