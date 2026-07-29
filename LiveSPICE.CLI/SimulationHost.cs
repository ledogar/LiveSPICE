using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Circuit;
using ComputerAlgebra;
using Util;

namespace LiveSPICE.CLI
{
    /// <summary>
    /// Glue between a loaded schematic and an audio callback: build the transient solution off the
    /// audio thread, then run it per buffer. Mono in, mono out (all speakers summed), which is what
    /// a guitar signal chain needs.
    /// </summary>
    public class SimulationHost
    {
        private readonly object sync = new object();
        private readonly ILog log;

        private Circuit.Circuit circuit;
        private Simulation simulation;

        public int Oversample { get; set; } = 8;
        public int Iterations { get; set; } = 8;
        public double InputGain { get; set; } = 1;
        public double OutputGain { get; set; } = 1;

        public string Name { get; private set; }
        /// <summary>Set when a background build failed; rethrown from the next Process call.</summary>
        private Exception buildException;
        private int rebuilds = 0;
        public int Rebuilds { get { return rebuilds; } }

        public SimulationHost(ILog Log) { log = Log; }

        public void Load(string FileName)
        {
            Schematic schematic = Schematic.Load(FileName, log);
            circuit = schematic.Build(log);
            Name = System.IO.Path.GetFileNameWithoutExtension(FileName);
        }

        /// <summary>The circuit's single input expression, or null if it has none.</summary>
        private Expression FindInput()
        {
            List<Expression> ins = circuit.Components.OfType<Input>().Select(i => i.In).ToList();
            if (ins.Count == 0)
                throw new NotSupportedException("Circuit '" + Name + "' has no input component.");
            if (ins.Count > 1)
                throw new NotSupportedException(
                    "Circuit '" + Name + "' has " + ins.Count + " inputs; only one is supported.");
            return ins[0];
        }

        /// <summary>All speakers summed to mono, matching the VST and benchmark conventions.</summary>
        private Expression FindOutput()
        {
            Expression sum = 0;
            foreach (Speaker i in circuit.Components.OfType<Speaker>())
                sum += i.Out;
            if (sum.EqualsZero())
                throw new NotSupportedException("Circuit '" + Name + "' has no speaker output.");
            return sum;
        }

        /// <summary>
        /// Build and fully warm a simulation. Must not run on the audio thread: this does the
        /// symbolic solve AND forces the Linq expression tree to compile.
        /// </summary>
        private Simulation Build(double SampleRate)
        {
            Analysis analysis = circuit.Analyze();
            TransientSolution ts = TransientSolution.Solve(analysis, (Real)1 / (SampleRate * Oversample), log);

            Simulation s = new Simulation(ts)
            {
                Log = log,
                Oversample = Oversample,
                Iterations = Iterations,
                Input = new[] { FindInput() },
                Output = new[] { FindOutput() },
            };

            // Simulation.Run compiles the process on first call. Left to the audio thread that is a
            // multi-millisecond stall inside the callback, so spend it here instead.
            s.Run(new double[64], new[] { new double[64] });
            return s;
        }

        /// <summary>Build synchronously. Used by the offline render path.</summary>
        public void BuildNow(double SampleRate)
        {
            Simulation s = Build(SampleRate);
            lock (sync)
                simulation = s;
        }

        /// <summary>
        /// Build in the background, leaving Process to emit silence until it is ready. The live
        /// path needs this because the sample rate isn't known until the stream is open.
        /// </summary>
        public Task BuildAsync(double SampleRate)
        {
            return Task.Run(() =>
            {
                try
                {
                    Simulation s = Build(SampleRate);
                    lock (sync)
                        simulation = s;
                    log.WriteLine(MessageType.Info, "Simulation ready ({0} Hz, oversample {1}, iterations {2}).",
                        SampleRate, Oversample, Iterations);
                }
                catch (Exception Ex)
                {
                    lock (sync)
                        buildException = Ex;
                }
            });
        }

        public bool Ready { get { lock (sync) return simulation != null; } }

        /// <summary>
        /// Run one buffer. Safe to call from an audio callback: no allocation, and the lock is only
        /// contended by a publish at the end of a background build.
        /// </summary>
        public void Process(int Count, double[] In, double[] Out, double SampleRate)
        {
            Exception pending = null;
            lock (sync)
            {
                if (buildException != null)
                {
                    pending = buildException;
                    buildException = null;
                }
            }
            if (pending != null)
                throw pending;

            if (InputGain != 1)
                for (int i = 0; i < Count; ++i)
                    In[i] *= InputGain;

            lock (sync)
            {
                if (simulation == null)
                {
                    Array.Clear(Out, 0, Count);
                    return;
                }

                try
                {
                    simulation.Run(Count, new[] { In }, new[] { Out });
                }
                catch (SimulationDiverged Ex)
                {
                    // Diverging early means the circuit is genuinely unstable; later means a
                    // transient worth recovering from.
                    log.WriteLine(MessageType.Error, "Simulation diverged: {0}", Ex.Message);
                    Array.Clear(Out, 0, Count);
                    bool retry = Ex.At > SampleRate;
                    simulation = null;
                    if (retry)
                    {
                        rebuilds++;
                        BuildAsync(SampleRate);
                    }
                    return;
                }
                catch (Exception Ex)
                {
                    log.WriteLine(MessageType.Error, "Simulation error: {0}", Ex.Message);
                    Array.Clear(Out, 0, Count);
                    simulation = null;
                    return;
                }
            }

            if (OutputGain != 1)
                for (int i = 0; i < Count; ++i)
                    Out[i] *= OutputGain;
        }
    }
}
