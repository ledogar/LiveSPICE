using System;
using System.IO;

namespace LiveSPICE.CLI
{
    /// <summary>
    /// Minimal WAV reader/writer. Nothing in the repo does file audio I/O (WaveAudio is live device
    /// I/O despite the name), and the offline render path needs it. Reads 16 bit PCM and 32 bit
    /// float; writes 32 bit float so a render round trip is lossless.
    /// </summary>
    public class Wav
    {
        public int SampleRate { get; set; }
        public double[][] Channels { get; set; }

        public int ChannelCount { get { return Channels.Length; } }
        public int SampleCount { get { return Channels.Length > 0 ? Channels[0].Length : 0; } }

        public Wav(int SampleRate, double[][] Channels)
        {
            this.SampleRate = SampleRate;
            this.Channels = Channels;
        }

        private const ushort FormatPcm = 1;
        private const ushort FormatFloat = 3;

        public static Wav Read(string FileName)
        {
            using (FileStream file = File.OpenRead(FileName))
            using (BinaryReader reader = new BinaryReader(file))
            {
                if (new string(reader.ReadChars(4)) != "RIFF")
                    throw new InvalidDataException("Not a RIFF file: " + FileName);
                reader.ReadUInt32();
                if (new string(reader.ReadChars(4)) != "WAVE")
                    throw new InvalidDataException("Not a WAVE file: " + FileName);

                ushort format = 0, channels = 0, bits = 0;
                int rate = 0;
                byte[] data = null;

                while (file.Position + 8 <= file.Length)
                {
                    string id = new string(reader.ReadChars(4));
                    uint size = reader.ReadUInt32();
                    long next = file.Position + size + (size % 2);

                    if (id == "fmt ")
                    {
                        format = reader.ReadUInt16();
                        channels = reader.ReadUInt16();
                        rate = (int)reader.ReadUInt32();
                        reader.ReadUInt32();     // byte rate
                        reader.ReadUInt16();     // block align
                        bits = reader.ReadUInt16();
                    }
                    else if (id == "data")
                    {
                        data = reader.ReadBytes((int)size);
                    }

                    if (next > file.Length) break;
                    file.Position = next;
                }

                if (data == null || channels == 0)
                    throw new InvalidDataException("Missing fmt or data chunk: " + FileName);

                int bytesPerSample = bits / 8;
                int frames = data.Length / (bytesPerSample * channels);
                double[][] result = new double[channels][];
                for (int c = 0; c < channels; ++c)
                    result[c] = new double[frames];

                for (int n = 0; n < frames; ++n)
                {
                    for (int c = 0; c < channels; ++c)
                    {
                        int offset = (n * channels + c) * bytesPerSample;
                        if (format == FormatFloat && bits == 32)
                            result[c][n] = BitConverter.ToSingle(data, offset);
                        else if (format == FormatPcm && bits == 16)
                            result[c][n] = BitConverter.ToInt16(data, offset) / 32767.0;
                        else if (format == FormatPcm && bits == 32)
                            result[c][n] = BitConverter.ToInt32(data, offset) / 2147483647.0;
                        else
                            throw new NotSupportedException(
                                "Unsupported WAV format " + format + " with " + bits + " bits.");
                    }
                }
                return new Wav(rate, result);
            }
        }

        public void Write(string FileName)
        {
            int channels = ChannelCount;
            int frames = SampleCount;
            int dataBytes = frames * channels * sizeof(float);

            using (FileStream file = File.Create(FileName))
            using (BinaryWriter writer = new BinaryWriter(file))
            {
                writer.Write("RIFF".ToCharArray());
                writer.Write((uint)(36 + dataBytes));
                writer.Write("WAVE".ToCharArray());

                writer.Write("fmt ".ToCharArray());
                writer.Write((uint)16);
                writer.Write(FormatFloat);
                writer.Write((ushort)channels);
                writer.Write((uint)SampleRate);
                writer.Write((uint)(SampleRate * channels * sizeof(float)));
                writer.Write((ushort)(channels * sizeof(float)));
                writer.Write((ushort)(8 * sizeof(float)));

                writer.Write("data".ToCharArray());
                writer.Write((uint)dataBytes);
                for (int n = 0; n < frames; ++n)
                    for (int c = 0; c < channels; ++c)
                        writer.Write((float)Channels[c][n]);
            }
        }
    }
}
