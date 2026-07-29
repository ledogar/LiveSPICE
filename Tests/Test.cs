using Circuit;
using ComputerAlgebra;
#if PLOTTING
using Plotting;
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Util;

namespace Tests
{
    internal class Test
    {
        private static double Benchmark(double t, Action fn)
        {
            DateTime begin = DateTime.Now;
            int iterations = 0;
            do
            {
                fn();
                iterations++;
            } while ((DateTime.Now - begin).TotalSeconds < t);
            return (DateTime.Now - begin).TotalSeconds / iterations;
        }

        private static Expression FindInput(Circuit.Circuit C)
        {
            return C.Components.OfType<Input>()
                .Select(i => i.In)
                // If there are no inputs, just make a dummy.
                .DefaultIfEmpty("V[t]")
                // Require exactly one input.
                .Single();
        }

        public Dictionary<Expression, List<double>> Run(
            Circuit.Circuit C,
            Func<double, double> Vin,
            int SampleRate,
            int Samples,
            int Oversample,
            int Iterations,
            Expression? Input = null,
            IEnumerable<Expression>? Outputs = null)
        {
            Analysis analysis = C.Analyze();
            TransientSolution TS = TransientSolution.Solve(analysis, (Real)1 / (SampleRate * Oversample));

            // By default, pass Vin to each input of the circuit.
            if (Input == null)
                Input = C.Components.Where(i => i is Input)
                    .Select(i => Component.DependentVariable(i.Name, Component.t))
                    // If there are no inputs, just make a dummy.
                    .DefaultIfEmpty("V[t]")
                    // Require exactly one input.
                    .Single();

            // By default, produce every node of the circuit as output.
            if (Outputs == null)
                Outputs = C.Nodes.Select(i => i.V);

            Simulation S = new Simulation(TS)
            {
                Oversample = Oversample,
                Iterations = Iterations,
                Input = new[] { Input },
                Output = Outputs,
            };

            Dictionary<Expression, List<double>> outputs = 
                S.Output.ToDictionary(i => i, i => new List<double>(Samples));

            double T = S.TimeStep;
            double t = 0;
            Random rng = new Random();
            int remaining = Samples;
            while (remaining > 0)
            {
                // Using a varying number of samples on each call to S.Run
                int N = Math.Min(remaining, rng.Next(1000, 10000));
                double[] inputBuffer = new double[N];
                List<double[]> outputBuffers = S.Output.Select(i => new double[N]).ToList();
                for (int n = 0; n < N; ++n, t += T)
                    inputBuffer[n] = Vin(t);

                S.Run(inputBuffer, outputBuffers);

                for (int i = 0; i < S.Output.Count(); ++i)
                    outputs[S.Output.ElementAt(i)].AddRange(outputBuffers[i]);

                remaining -= N;
            }

            return outputs;
        }

        /// <summary>
        /// Benchmark a circuit simulation.
        /// By default, benchmarks producing the sum of all output components.
        /// </summary>
        /// <returns>{analyze time, solve time, simulate rate} in seconds or Hz</returns>
        public double[] Benchmark(
            Circuit.Circuit C,
            Func<double, double> Vin,
            int SampleRate,
            int Oversample,
            int Iterations,
            Expression? Input = null,
            IEnumerable<Expression>? Outputs = null,
            ILog? log = null)
        {
            Analysis? analysis = null;
            double analyzeTime = Benchmark(1, () => analysis = C.Analyze());

            TransientSolution? TS = null;
            double solveTime = Benchmark(1, () => TS = TransientSolution.Solve(analysis, (Real)1 / (SampleRate * Oversample), log));

            // By default, pass Vin to each input of the circuit.
            if (Input == null)
                Input = FindInput(C);

            // By default, produce every node of the circuit as output.
            if (Outputs == null)
            {
                Expression sum = 0;
                foreach (Speaker i in C.Components.OfType<Speaker>())
                    sum += i.Out;
                Outputs = new[] { sum };
            }

            Simulation S = new Simulation(TS)
            {
                Oversample = Oversample,
                Iterations = Iterations,
                Input = new[] { Input },
                Output = Outputs,
            };

            int N = 1000;
            double[] inputBuffer = new double[N];
            List<double[]> outputBuffers = Outputs.Select(i => new double[N]).ToList();

            double T = 1.0 / SampleRate;
            double t = 0;
            double runTime = Benchmark(3, () =>
            {
                // This is counting the cost of evaluating Vin during benchmarking...
                for (int n = 0; n < N; ++n, t += T)
                    inputBuffer[n] = Vin(t);

                S.Run(inputBuffer, outputBuffers);
            });
            double rate = N / runTime;
            return new double[] { analyzeTime, solveTime, rate };
        }

#if PLOTTING
        public void PlotAll(string Title, Dictionary<Expression, List<double>> Outputs)
        {
            Plot p = new Plot()
            {
                Title = Title,
                Width = 1200,
                Height = 800,
                x0 = 0,
                x1 = Outputs.Max(i => i.Value.Count),
                xLabel = "Time (s)",
                yLabel = "Voltage (V)",
            };

            p.Series.AddRange(Outputs.Select(i => new Scatter(
                i.Value.Select((k, n) => new KeyValuePair<double, double>(n, k)).ToArray())
            { Name = i.Key.ToString() }));

            System.IO.Directory.CreateDirectory("Plots");
            p.Save(Path.Combine("Plots", Title + ".bmp"));
        }
#endif
        /// <summary>
        /// Statistics over the steady-state portion of the run. Warmup samples are skipped so the
        /// startup transient - which is large, and highly sensitive to exactly where the buffer
        /// boundaries fell - does not dominate the mean.
        /// </summary>
        private static Dictionary<string, double[]> ComputeStatistics(Dictionary<Expression, List<double>> Outputs, int Warmup)
        {
            var stats = new Dictionary<string, double[]>();
            foreach (var i in Outputs)
            {
                double[] steadyState = i.Value.Skip(Warmup).ToArray();
                stats[i.Key.ToString()] = new[] { steadyState.Sum() / steadyState.Length, steadyState.Min(), steadyState.Max() };
            }
            return stats;
        }

        // G5 is deliberate: rounding to five significant figures absorbs the last-digit
        // differences between platforms and CPU architectures, which lets the baselines be
        // compared as text instead of needing per-variable tolerances.
        private const string StatsColumns = "{0}, {1:G5}, {2:G5}, {3:G5}";

        private static string StatsToString(Dictionary<Expression, List<double>> Outputs, int Warmup)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, StatsColumns, "var", "mean", "min", "max"));
            foreach (var i in ComputeStatistics(Outputs, Warmup))
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, StatsColumns, i.Key, i.Value[0], i.Value[1], i.Value[2]));
            return sb.ToString();
        }

        /// <summary>
        /// Compare simulation statistics against the golden file in Stats/, or regenerate it.
        /// Checking is unconditional: a test run that does not assert is not a test.
        /// </summary>
        /// <returns>0 if the circuit matches its golden file, 1 otherwise.</returns>
        public int CheckStatistics(string Title, Dictionary<Expression, List<double>> Outputs, int Warmup, bool Update, ILog Log)
        {
            string stats = StatsToString(Outputs, Warmup);
            string path = Path.Combine("Stats", Title + ".csv");

            if (Update)
            {
                System.IO.Directory.CreateDirectory("Stats");
                File.WriteAllText(path, stats);
                return 0;
            }

            if (!File.Exists(path))
            {
                Log.WriteLine(MessageType.Error, "CHECK FAIL {0}: no golden file at '{1}'. Run with --updateGolden to create one.", Title, path);
                return 1;
            }

            string golden = File.ReadAllText(path);
            if (golden.Replace("\r\n", "\n") == stats.Replace("\r\n", "\n"))
            {
                Log.WriteLine(MessageType.Info, "CHECK OK {0}", Title);
                return 0;
            }

            Log.WriteLine(MessageType.Error, "CHECK FAIL {0}:\n  got:\n{1}\n  expected:\n{2}", Title, stats, golden);
            return 1;
        }
    }
}