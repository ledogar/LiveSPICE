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
        private static Dictionary<string, double[]> ComputeStatistics(Dictionary<Expression, List<double>> Outputs)
        {
            var stats = new Dictionary<string, double[]>();
            foreach (var i in Outputs)
            {
                double mean = i.Value.Sum() / i.Value.Count;
                double min = i.Value.Min();
                double max = i.Value.Max();
                double rms = Math.Sqrt(i.Value.Select(v => v * v).Sum()) / i.Value.Count;
                stats[i.Key.ToString()] = new[] { mean, min, max, rms };
            }
            return stats;
        }

        public void WriteStatistics(string Title, Dictionary<Expression, List<double>> Outputs)
        {
            string cols = "{0}, {1}, {2}, {3}, {4}";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, cols, "var", "mean", "min", "max", "rms"));
            foreach (var i in ComputeStatistics(Outputs))
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, cols, i.Key, i.Value[0], i.Value[1], i.Value[2], i.Value[3]));

            string path = Path.Combine("Stats", Title + ".csv");
            System.IO.Directory.CreateDirectory("Stats");
            File.WriteAllText(path, sb.ToString());
        }

        // Circuits whose solutions deterministically amplify platform floating-point
        // differences (hard clipping recirculated through feedback), checked with a
        // loose tolerance. Their results are run-to-run deterministic on any one
        // platform, but the trajectory differs measurably across platforms.
        private static readonly Dictionary<string, double> looseTolerance = new Dictionary<string, double>
        {
            { "Pro Co Rat", 1e-1 },
        };

        /// <summary>
        /// Compare simulation statistics against the saved baseline in Stats/.
        /// The committed baselines were generated at --sampleRate 44100 (defaults otherwise);
        /// checks only make sense at the configuration that produced the baseline.
        /// Deviations are normalized by each variable's signal scale, max(|min|, |max|),
        /// because the mean of an AC signal is near zero and a plain relative error there
        /// is meaningless.
        /// </summary>
        /// <returns>0 if the circuit matches its baseline, 1 otherwise.</returns>
        public int CheckStatistics(string Title, Dictionary<Expression, List<double>> Outputs, ILog Log)
        {
            string path = Path.Combine("Stats", Title + ".csv");
            if (!File.Exists(path))
            {
                Log.WriteLine(MessageType.Error, "CHECK FAIL {0}: no baseline at '{1}'", Title, path);
                return 1;
            }

            var computed = ComputeStatistics(Outputs);
            var baseline = new Dictionary<string, double[]>();
            foreach (string line in File.ReadAllLines(path).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] fields = line.Split(',');
                baseline[fields[0].Trim()] = fields.Skip(1).Take(4)
                    .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            }

            double tol = looseTolerance.TryGetValue(Title, out double loose) ? loose : 1e-6;
            string[] statNames = { "mean", "min", "max", "rms" };
            int failures = 0;
            double worst = 0;
            string worstAt = "";
            foreach (var v in baseline)
            {
                if (!computed.TryGetValue(v.Key, out double[]? c))
                {
                    Log.WriteLine(MessageType.Error, "CHECK FAIL {0}: baseline variable {1} not produced", Title, v.Key);
                    failures++;
                    continue;
                }
                double scale = Math.Max(Math.Max(Math.Abs(v.Value[1]), Math.Abs(v.Value[2])), 1e-12);
                for (int i = 0; i < 4; ++i)
                {
                    double dev = Math.Abs(c[i] - v.Value[i]) / scale;
                    if (dev > worst) { worst = dev; worstAt = v.Key + "." + statNames[i]; }
                    // NaN deviation must fail, hence the negated comparison.
                    if (!(dev <= tol))
                    {
                        Log.WriteLine(MessageType.Error, "CHECK FAIL {0}: {1}.{2} baseline={3} computed={4} deviation={5:G3}",
                            Title, v.Key, statNames[i], v.Value[i], c[i], dev);
                        failures++;
                    }
                }
            }
            foreach (string k in computed.Keys.Where(k => !baseline.ContainsKey(k)))
            {
                Log.WriteLine(MessageType.Error, "CHECK FAIL {0}: variable {1} missing from baseline", Title, k);
                failures++;
            }

            if (failures == 0)
                Log.WriteLine(MessageType.Info, "CHECK OK {0} (max deviation {1:G3} at {2})", Title, worst, worstAt);
            return failures > 0 ? 1 : 0;
        }
    }
}