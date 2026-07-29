using System;
using Circuit;
using LiveSPICE.PluginCore;

namespace LiveSPICE.Avalonia;

internal sealed class LiveAudioProcessor
{
    private readonly Schematic schematic;
    private SimulationProcessor? processor;

    // Reused between callbacks; the audio path should not allocate per buffer. The single-element
    // channel arrays are what RunSimulation consumes, updated whenever the buffers grow.
    private double[] inputSamples = Array.Empty<double>();
    private double[] outputSamples = Array.Empty<double>();
    private readonly double[][] inputChannels = { Array.Empty<double>() };
    private readonly double[][] outputChannels = { Array.Empty<double>() };
    private long tonePosition;

    public LiveAudioProcessor(Schematic schematic)
    {
        this.schematic = schematic;
    }

    public double InputScale { get; set; } = 1;

    public double OutputScale { get; set; } = 1;

    public void Start(double sampleRate, int oversample, int iterations)
    {
        SimulationProcessor started = new SimulationProcessor
        {
            SampleRate = sampleRate,
            Oversample = oversample,
            Iterations = iterations,
        };
        started.SetSchematic(schematic);
        // Solve and compile now, so the first audio callback finds the simulation ready instead
        // of stalling on Simulation's lazy compile.
        started.EnsureSimulationReady();
        tonePosition = 0;
        processor = started;
    }

    public void Stop()
    {
        processor = null;
    }

    public double[] Process(int count, Audio.SampleBuffer[] input, Audio.SampleBuffer[] output, double rate)
    {
        SimulationProcessor? current = processor;
        if (current == null)
        {
            foreach (Audio.SampleBuffer buffer in output)
                buffer.Clear();
            return Array.Empty<double>();
        }

        if (inputSamples.Length < count)
        {
            inputSamples = new double[count];
            outputSamples = new double[count];
            inputChannels[0] = inputSamples;
            outputChannels[0] = outputSamples;
        }

        if (input.Length > 0)
            Array.Copy(input[0].Samples, inputSamples, count);
        else
            for (int sample = 0; sample < count; sample++)
                inputSamples[sample] = 0.25 * Math.Sin(2 * Math.PI * 440 * ((tonePosition + sample) / rate));
        tonePosition += count;
        for (int sample = 0; sample < count; sample++)
            inputSamples[sample] *= InputScale;

        // Divergence is handled inside RunSimulation (silence + off-thread rebuild); any other
        // exception propagates to the caller's handler, which surfaces it in the UI log.
        current.RunSimulation(inputChannels, outputChannels, count);

        for (int sample = 0; sample < count; sample++)
            outputSamples[sample] *= OutputScale;
        foreach (Audio.SampleBuffer buffer in output)
            Array.Copy(outputSamples, buffer.Samples, count);

        // The returned samples are handed to the UI thread for the waveform display, so they must
        // be a copy - the reused buffer will be overwritten by the next callback.
        double[] display = new double[count];
        Array.Copy(outputSamples, display, count);
        return display;
    }
}
