using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveSPICE.PluginCore;

namespace LiveSPICE.CLI
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "help")
            {
                Usage();
                return args.Length == 0 ? 1 : 0;
            }

            Args a = new Args(args.Skip(1));
            try
            {
                switch (args[0])
                {
                    case "list": return List();
                    case "tone": return Tone(a);
                    case "render": return Render(a);
                    case "play": return Play(a);
                    case "loopback": return Loopback(a);
                    default:
                        Console.Error.WriteLine("Unknown command '" + args[0] + "'.");
                        Usage();
                        return 1;
                }
            }
            catch (Exception Ex)
            {
                Console.Error.WriteLine("Error: " + Ex.Message);
                return 1;
            }
        }

        static void Usage()
        {
            Console.WriteLine(@"LiveSPICE headless player.

  livespice list
      List audio drivers, devices and channels.

  livespice tone --output <file.wav> [--frequency 82] [--seconds 1]
                 [--rate 44100] [--amplitude 0.5] [--harmonics 1]
      Generate a test tone.

  livespice render --schematic <file.schx> --input <in.wav> --output <out.wav>
                   [--oversample 8] [--iterations 8] [--input-gain 1] [--output-gain 1]
      Render a wav through a circuit offline. No audio device needed.

  livespice play --schematic <file.schx> [--device <name>] [--inputs 0] [--outputs 0,1]
                 [--oversample 8] [--iterations 8] [--input-gain 1] [--output-gain 1]
                 [--seconds <n>]
      Play live through a circuit. Runs until Ctrl-C unless --seconds is given.
      --outputs defaults to the first two channels. On an Aggregate Device those may
      be the loopback rather than the speakers - check 'livespice list'.");
        }

        // ---------------------------------------------------------------- list

        /// <summary>
        /// Audio.Driver.Drivers discovers backends by reflecting over loaded assemblies, and .NET
        /// only loads an assembly when a type from it is first used. A ProjectReference alone is
        /// not enough, so touch the type. LiveSPICE's WPF app solves this with App.LoadAssemblies.
        /// </summary>
        static void LoadBackends()
        {
            if (typeof(CoreAudio.Driver) == null)
                throw new InvalidOperationException();
        }

        static int List()
        {
            LoadBackends();
            int devices = 0;
            foreach (Audio.Driver driver in Audio.Driver.Drivers)
            {
                Console.WriteLine(driver.Name);
                foreach (Audio.Device device in driver.Devices)
                {
                    devices++;
                    Console.WriteLine("  {0}  ({1} in, {2} out)",
                        device.Name, device.InputChannels.Length, device.OutputChannels.Length);
                    foreach (Audio.Channel i in device.InputChannels)
                        Console.WriteLine("      in  [{0}] {1}", Array.IndexOf(device.InputChannels, i), i.Name);
                    foreach (Audio.Channel i in device.OutputChannels)
                        Console.WriteLine("      out [{0}] {1}", Array.IndexOf(device.OutputChannels, i), i.Name);
                }
            }
            if (devices == 0)
                Console.WriteLine("No audio devices found.");
            return 0;
        }

        // ---------------------------------------------------------------- tone

        static int Tone(Args a)
        {
            string output = a.Required("output");
            double frequency = a.Double("frequency", 82);
            double seconds = a.Double("seconds", 1);
            int rate = (int)a.Double("rate", 44100);
            double amplitude = a.Double("amplitude", 0.5);
            int harmonics = (int)a.Double("harmonics", 1);

            int count = (int)(seconds * rate);
            double[] samples = new double[count];
            for (int n = 0; n < count; ++n)
            {
                double t = (double)n / rate;
                double s = 0;
                for (int h = 1; h <= harmonics; ++h)
                    s += Math.Sin(2 * Math.PI * frequency * h * t) / harmonics;
                samples[n] = amplitude * s;
            }
            new Wav(rate, new[] { samples }).Write(output);
            Console.WriteLine("Wrote {0}: {1} Hz, {2} samples at {3} Hz.", output, frequency, count, rate);
            return 0;
        }

        // -------------------------------------------------------------- render

        static int Render(Args a)
        {
            string schematic = a.Required("schematic");
            string inputFile = a.Required("input");
            string outputFile = a.Required("output");

            SimulationProcessor processor = NewProcessor(a);
            double inputGain = a.Double("input-gain", 1);
            double outputGain = a.Double("output-gain", 1);
            processor.LoadSchematic(schematic);

            Wav wav = Wav.Read(inputFile);
            double[] input = wav.Channels[0];
            double[] output = new double[input.Length];

            Console.WriteLine("Building simulation at {0} Hz...", wav.SampleRate);
            DateTime begin = DateTime.Now;
            processor.SampleRate = wav.SampleRate;
            processor.EnsureSimulationReady();
            Console.WriteLine("Built in {0:F2} s.", (DateTime.Now - begin).TotalSeconds);

            // Chunked, both to mirror the live path and to prove buffer boundaries don't matter.
            const int Block = 1024;
            begin = DateTime.Now;
            double[] inBlock = new double[Block];
            double[] outBlock = new double[Block];
            double[][] inChannels = { inBlock };
            double[][] outChannels = { outBlock };
            for (int n = 0; n < input.Length; n += Block)
            {
                int count = Math.Min(Block, input.Length - n);
                Array.Copy(input, n, inBlock, 0, count);
                ApplyGain(inBlock, count, inputGain);
                processor.RunSimulation(inChannels, outChannels, count);
                ApplyGain(outBlock, count, outputGain);
                Array.Copy(outBlock, 0, output, n, count);
            }
            double elapsed = (DateTime.Now - begin).TotalSeconds;

            new Wav(wav.SampleRate, new[] { output }).Write(outputFile);

            double peak = output.Length > 0 ? output.Max(Math.Abs) : 0;
            double rms = output.Length > 0 ? Math.Sqrt(output.Sum(i => i * i) / output.Length) : 0;
            Console.WriteLine("Wrote {0}: {1} samples, peak {2:G4}, rms {3:G4}.", outputFile, output.Length, peak, rms);
            Console.WriteLine("Rendered {0:F2} s of audio in {1:F2} s ({2:F1}x realtime).",
                (double)input.Length / wav.SampleRate, elapsed,
                elapsed > 0 ? ((double)input.Length / wav.SampleRate) / elapsed : 0);
            return peak > 0 ? 0 : 2;
        }

        // ---------------------------------------------------------------- play

        static int Play(Args a)
        {
            string schematic = a.Required("schematic");
            string deviceName = a.String("device", null);
            double seconds = a.Double("seconds", 0);

            SimulationProcessor processor = NewProcessor(a);
            double inputGain = a.Double("input-gain", 1);
            double outputGain = a.Double("output-gain", 1);
            processor.LoadSchematic(schematic);

            Audio.Device device = FindDevice(deviceName);
            Audio.Channel[] inputs = SelectChannels(device.InputChannels, a.String("inputs", null), 1);
            Audio.Channel[] outputs = SelectChannels(device.OutputChannels, a.String("outputs", null), DefaultOutputChannels);
            WarnUnselectedOutputs(device, outputs);

            Console.WriteLine("Device: {0}", device.Name);
            Console.WriteLine("Inputs: {0}", inputs.Length > 0 ? string.Join(", ", inputs.Select(i => i.Name)) : "(none)");
            Console.WriteLine("Outputs: {0}", outputs.Length > 0 ? string.Join(", ", outputs.Select(i => i.Name)) : "(none)");

            double[] silence = null;
            double[][] inChannels = new double[1][];
            double[][] outChannels = new double[1][];
            long callbacks = 0;
            long errors = 0;

            Audio.Stream stream = null;
            Audio.Stream.SampleHandler handler = (Count, In, Out, Rate) =>
            {
                callbacks++;
                if (Out.Length == 0)
                    return;

                // The setter is a no-op when the rate is unchanged; a change flags a rebuild that
                // RunSimulation kicks off on a background task. Until the simulation is ready,
                // RunSimulation passes the (dry) input through.
                if (processor.SampleRate != Rate)
                    processor.SampleRate = Rate;

                double[] inBuffer;
                if (In.Length > 0)
                {
                    inBuffer = In[0].Samples;
                }
                else
                {
                    if (silence == null || silence.Length < Count)
                        silence = new double[Count];
                    inBuffer = silence;
                }

                ApplyGain(inBuffer, Count, inputGain);
                inChannels[0] = inBuffer;
                outChannels[0] = Out[0].Samples;
                try
                {
                    processor.RunSimulation(inChannels, outChannels, Count);
                }
                catch (Exception)
                {
                    // A background solve failed; its exception surfaces here. Keep the stream
                    // alive and silent - the count is reported at shutdown.
                    Array.Clear(Out[0].Samples, 0, Count);
                    errors++;
                }
                ApplyGain(Out[0].Samples, Count, outputGain);

                // Same signal to every selected output channel.
                for (int i = 1; i < Out.Length; ++i)
                    Array.Copy(Out[0].Samples, Out[i].Samples, Count);
            };

            stream = device.Open(handler, inputs, outputs);
            Console.WriteLine("Playing '{0}' at {1} Hz. Press Ctrl-C to stop.", processor.SchematicName, stream.SampleRate);
            if (inputs.Length > 0)
                Console.WriteLine("(If input is silent, grant microphone access to your terminal in " +
                                  "System Settings > Privacy & Security > Microphone.)");

            ManualResetEventSlim done = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; done.Set(); };

            // Report readiness without touching the console from the audio thread.
            Task.Run(async () =>
            {
                while (!done.IsSet && !processor.SimulationReady)
                    await Task.Delay(100);
                if (processor.SimulationReady)
                    Console.WriteLine("Simulation ready ({0} Hz, oversample {1}, iterations {2}).",
                        processor.SampleRate, processor.Oversample, processor.Iterations);
            });

            if (seconds > 0)
                done.Wait(TimeSpan.FromSeconds(seconds));
            else
                done.Wait();
            done.Set();

            stream.Stop();
            Console.WriteLine("Stopped after {0} callbacks.", callbacks);
            if (errors > 0)
                Console.WriteLine("{0} callback(s) failed; last build error above.", errors);
            return 0;
        }

        // ------------------------------------------------------------ loopback

        /// <summary>
        /// Play a tone and record what comes back, exercising the audio path with no simulation in
        /// the way. On a loopback device (BlackHole) the recording should reproduce the tone, which
        /// verifies enumeration, format negotiation, the render callback and both conversions.
        /// </summary>
        static int Loopback(Args a)
        {
            string deviceName = a.String("device", null);
            double frequency = a.Double("frequency", 440);
            double seconds = a.Double("seconds", 1);
            double amplitude = a.Double("amplitude", 0.5);
            string record = a.String("record", null);

            Audio.Device device = FindDevice(deviceName);
            Audio.Channel[] inputs = SelectChannels(device.InputChannels, a.String("inputs", null), 1);
            Audio.Channel[] outputs = SelectChannels(device.OutputChannels, a.String("outputs", null), DefaultOutputChannels);
            WarnUnselectedOutputs(device, outputs);
            Console.WriteLine("Device: {0} ({1} in, {2} out)", device.Name, inputs.Length, outputs.Length);

            List<double> captured = new List<double>();
            double phase = 0;
            long frames = 0;
            object sync = new object();

            Audio.Stream.SampleHandler handler = (Count, In, Out, Rate) =>
            {
                double step = 2 * Math.PI * frequency / Rate;
                for (int i = 0; i < Count; ++i, phase += step)
                {
                    double s = amplitude * Math.Sin(phase);
                    for (int c = 0; c < Out.Length; ++c)
                        Out[c].Samples[i] = s;
                }
                if (In.Length > 0)
                {
                    lock (sync)
                        for (int i = 0; i < Count; ++i)
                            captured.Add(In[0].Samples[i]);
                }
                frames += Count;
            };

            Audio.Stream stream = device.Open(handler, inputs, outputs);
            Console.WriteLine("Running {0} s at {1} Hz...", seconds, stream.SampleRate);
            Thread.Sleep((int)(seconds * 1000));
            stream.Stop();

            double[] samples;
            lock (sync)
                samples = captured.ToArray();

            Console.WriteLine("Rendered {0} frames, captured {1}.", frames, samples.Length);
            if (samples.Length == 0)
            {
                Console.WriteLine("No input captured (no input channels selected).");
                return 0;
            }

            if (record != null)
                new Wav((int)stream.SampleRate, new[] { samples }).Write(record);

            // Skip the first buffers: the loop takes a moment to come up.
            int skip = Math.Min(samples.Length / 4, (int)stream.SampleRate / 10);
            double peak = 0, sum = 0;
            for (int i = skip; i < samples.Length; ++i)
            {
                peak = Math.Max(peak, Math.Abs(samples[i]));
                sum += samples[i] * samples[i];
            }
            int n = samples.Length - skip;
            double rms = Math.Sqrt(sum / Math.Max(n, 1));

            // Single frequency fit at the tone: how much of the capture is the tone we played?
            double re = 0, im = 0;
            for (int i = 0; i < n; ++i)
            {
                double w = 2 * Math.PI * frequency * i / stream.SampleRate;
                re += samples[skip + i] * Math.Cos(w);
                im -= samples[skip + i] * Math.Sin(w);
            }
            double toneAmplitude = 2 * Math.Sqrt(re * re + im * im) / n;

            Console.WriteLine("Captured peak {0:G4}, rms {1:G4}, {2:F1} Hz component {3:G4} (played {4:G4}).",
                peak, rms, frequency, toneAmplitude, amplitude);

            if (peak == 0)
            {
                Console.WriteLine("Input was silent. If this is a loopback device, check that the terminal has " +
                                  "microphone access in System Settings > Privacy & Security > Microphone.");
                return 2;
            }
            // A loopback device should return most of what we played.
            bool ok = toneAmplitude > 0.5 * amplitude;
            Console.WriteLine(ok ? "PASS: loopback reproduced the tone." : "FAIL: captured signal does not match the tone.");
            return ok ? 0 : 3;
        }

        // --------------------------------------------------------------- utils

        static SimulationProcessor NewProcessor(Args a)
        {
            return new SimulationProcessor()
            {
                Oversample = (int)a.Double("oversample", 8),
                Iterations = (int)a.Double("iterations", 8),
            };
        }

        static void ApplyGain(double[] samples, int count, double gain)
        {
            if (gain == 1)
                return;
            for (int i = 0; i < count; ++i)
                samples[i] *= gain;
        }

        static Audio.Device FindDevice(string Name)
        {
            LoadBackends();
            List<Audio.Device> all = Audio.Driver.Drivers.SelectMany(i => i.Devices).ToList();
            if (all.Count == 0)
                throw new NotSupportedException("No audio devices found.");
            if (string.IsNullOrEmpty(Name))
            {
                // Prefer something that can actually do duplex.
                return all.FirstOrDefault(i => i.InputChannels.Length > 0 && i.OutputChannels.Length > 0)
                    ?? all.First(i => i.OutputChannels.Length > 0);
            }
            Audio.Device device = all.FirstOrDefault(i => i.Name == Name)
                ?? all.FirstOrDefault(i => i.Name.IndexOf(Name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (device == null)
                throw new NotSupportedException("No device matching '" + Name + "'. Try 'livespice list'.");
            return device;
        }

        /// <summary>
        /// Default to a stereo pair rather than every output. Writing to every channel of a device
        /// that also provides input - an Aggregate Device built around a loopback like BlackHole -
        /// feeds the output straight back into the input.
        /// </summary>
        const int DefaultOutputChannels = 2;

        static void WarnUnselectedOutputs(Audio.Device Device, Audio.Channel[] Selected)
        {
            if (Device.OutputChannels.Length <= Selected.Length)
                return;
            Console.WriteLine("Note: this device has {0} output channels and {1} were selected. " +
                              "On an Aggregate Device the first channels are not necessarily the ones " +
                              "you hear - use --outputs to pick. Available:",
                Device.OutputChannels.Length, Selected.Length);
            for (int i = 0; i < Device.OutputChannels.Length; ++i)
                Console.WriteLine("    [{0}] {1}{2}", i, Device.OutputChannels[i].Name,
                    Selected.Contains(Device.OutputChannels[i]) ? "  (selected)" : "");
        }

        static Audio.Channel[] SelectChannels(Audio.Channel[] Available, string Spec, int DefaultCount)
        {
            if (Available.Length == 0)
                return new Audio.Channel[] { };
            if (string.IsNullOrEmpty(Spec))
                return Available.Take(Math.Min(DefaultCount, Available.Length)).ToArray();
            if (Spec == "none")
                return new Audio.Channel[] { };

            List<Audio.Channel> selected = new List<Audio.Channel>();
            foreach (string i in Spec.Split(','))
            {
                int index;
                if (!int.TryParse(i.Trim(), out index) || index < 0 || index >= Available.Length)
                    throw new ArgumentException("Channel '" + i.Trim() + "' is out of range (0.." + (Available.Length - 1) + ").");
                selected.Add(Available[index]);
            }
            return selected.ToArray();
        }

        /// <summary>
        /// Minimal --name value parser. System.CommandLine would be the consistent choice, but the
        /// version this repo pins is a 2.0 beta and this is four commands.
        /// </summary>
        class Args
        {
            private readonly Dictionary<string, string> values = new Dictionary<string, string>();

            public Args(IEnumerable<string> args)
            {
                string key = null;
                foreach (string i in args)
                {
                    if (i.StartsWith("--"))
                    {
                        if (key != null) values[key] = "true";
                        key = i.Substring(2);
                    }
                    else if (key != null)
                    {
                        values[key] = i;
                        key = null;
                    }
                }
                if (key != null) values[key] = "true";
            }

            public string String(string Name, string Default)
            {
                string value;
                return values.TryGetValue(Name, out value) ? value : Default;
            }

            public string Required(string Name)
            {
                string value = String(Name, null);
                if (string.IsNullOrEmpty(value))
                    throw new ArgumentException("Missing required argument --" + Name + ".");
                return value;
            }

            public double Double(string Name, double Default)
            {
                string value = String(Name, null);
                if (value == null) return Default;
                double result;
                if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out result))
                    throw new ArgumentException("--" + Name + " expects a number, got '" + value + "'.");
                return result;
            }
        }
    }
}
