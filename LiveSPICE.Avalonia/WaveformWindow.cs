using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Circuit;
using APoint = Avalonia.Point;

namespace LiveSPICE.Avalonia;

public sealed class WaveformWindow : Window
{
    private readonly Schematic schematic;
    private readonly AppSettings settings;
    private readonly WaveformView waveform = new WaveformView();
    private readonly TextBox oversample = new TextBox { Text = "8", Width = 52 };
    private readonly TextBox iterations = new TextBox { Text = "8", Width = 52 };
    private readonly TextBox samples = new TextBox { Text = "4096", Width = 72 };
    private readonly TextBox frequency = new TextBox { Text = "440", Width = 72 };
    private readonly Slider inputGain = new Slider { Minimum = -40, Maximum = 40, Value = 0, Width = 160 };
    private readonly Slider outputGain = new Slider { Minimum = -40, Maximum = 40, Value = 0, Width = 160 };
    private readonly TextBlock audioConfig = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox log = new TextBox { IsReadOnly = true, AcceptsReturn = true, MinHeight = 100, TextWrapping = TextWrapping.Wrap };

    public WaveformWindow(Schematic schematic, AppSettings settings)
    {
        this.schematic = schematic;
        this.settings = settings;
        Title = "Simulation Scope";
        Width = 1000;
        Height = 700;
        MinWidth = 520;
        MinHeight = 420;
        Content = BuildContent();
        UpdateAudioSummary();
        RunSimulation();
    }

    private Control BuildContent()
    {
        DockPanel root = new DockPanel();

        StackPanel toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new global::Avalonia.Thickness(8),
            VerticalAlignment = VerticalAlignment.Center
        };
        toolbar.Children.Add(Label("Oversample"));
        toolbar.Children.Add(oversample);
        toolbar.Children.Add(Label("Iterations"));
        toolbar.Children.Add(iterations);
        toolbar.Children.Add(Label("Samples"));
        toolbar.Children.Add(samples);
        toolbar.Children.Add(Label("Hz"));
        toolbar.Children.Add(frequency);
        toolbar.Children.Add(Button("Run", (_, _) => RunSimulation()));
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        Grid main = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), RowDefinitions = new RowDefinitions("*,150") };

        StackPanel audio = new StackPanel { Spacing = 10, Margin = new global::Avalonia.Thickness(10) };
        audio.Children.Add(Header("Audio"));
        audio.Children.Add(audioConfig);
        audio.Children.Add(Button("Configure", (_, _) =>
        {
            AudioConfigWindow window = new AudioConfigWindow(settings);
            window.Closed += (_, _) => UpdateAudioSummary();
            window.Show();
        }));
        audio.Children.Add(Label("Input gain (dB)"));
        audio.Children.Add(inputGain);
        audio.Children.Add(Label("Output gain (dB)"));
        audio.Children.Add(outputGain);
        audio.Children.Add(Header("Input"));
        audio.Children.Add(new TextBlock { Text = "Generated sine" });
        audio.Children.Add(Header("Output"));
        audio.Children.Add(new TextBlock { Text = "First output channel" });
        Grid.SetColumn(audio, 0);
        Grid.SetRowSpan(audio, 2);
        main.Children.Add(audio);

        Border plotBorder = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new global::Avalonia.Thickness(1), Child = waveform };
        Grid.SetColumn(plotBorder, 1);
        Grid.SetRow(plotBorder, 0);
        main.Children.Add(plotBorder);

        log.Margin = new global::Avalonia.Thickness(8);
        Grid.SetColumn(log, 1);
        Grid.SetRow(log, 1);
        main.Children.Add(log);

        root.Children.Add(main);
        return root;
    }

    private void RunSimulation()
    {
        try
        {
            int oversampleValue = ParseInt(oversample.Text, 8, 1, 64);
            int iterationValue = ParseInt(iterations.Text, 8, 1, 64);
            int sampleCount = ParseInt(samples.Text, 4096, 64, 262144);
            double frequencyValue = ParseDouble(frequency.Text, 440, 0.1, 96000);
            int sampleRate = 48000;

            Circuit.Circuit circuit = schematic.Build();
            Simulation simulation = AudioSimulationFactory.Create(circuit, sampleRate, 1, oversampleValue);
            simulation.Iterations = iterationValue;

            double inGain = DbToLinear(inputGain.Value);
            double outGain = DbToLinear(outputGain.Value);
            double[] input = new double[sampleCount];
            double[] output = new double[sampleCount];
            for (int i = 0; i < input.Length; i++)
                input[i] = inGain * 0.25 * Math.Sin(2 * Math.PI * frequencyValue * i / sampleRate);

            simulation.Run(input.Length, new[] { input }, new[] { output });
            for (int i = 0; i < output.Length; i++)
                output[i] *= outGain;

            waveform.SetSamples(output, sampleRate);
            log.Text = $"Build succeeded\nAudio driver: {AudioName(settings.AudioDriver)}\nDevice: {AudioName(settings.AudioDevice)}\nInputs: {ChannelNames(settings.AudioInputs)}\nOutputs: {ChannelNames(settings.AudioOutputs)}\nSample rate: {sampleRate}\nOversample: {oversampleValue}\nIterations: {iterationValue}\nSamples: {sampleCount}\nFrequency: {frequencyValue}\nPeak: {output.Select(Math.Abs).DefaultIfEmpty().Max():R}";
        }
        catch (Exception ex)
        {
            log.Text = ex.ToString();
        }
    }

    private static TextBlock Header(string text)
    {
        return new TextBlock { Text = text, FontWeight = FontWeight.Bold, Margin = new global::Avalonia.Thickness(0, 8, 0, 0) };
    }

    private void UpdateAudioSummary()
    {
        audioConfig.Text = $"{AudioName(settings.AudioDriver)} / {AudioName(settings.AudioDevice)}\nIn: {ChannelNames(settings.AudioInputs)}\nOut: {ChannelNames(settings.AudioOutputs)}";
    }

    private static string AudioName(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "None" : value;
    }

    private static string ChannelNames(System.Collections.Generic.IReadOnlyCollection<string> channels)
    {
        return channels.Count == 0 ? "None" : string.Join(", ", channels);
    }

    private static TextBlock Label(string text)
    {
        return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
    }

    private static Button Button(string text, EventHandler<global::Avalonia.Interactivity.RoutedEventArgs> click)
    {
        Button button = new Button { Content = text, MinWidth = 70 };
        button.Click += click;
        return button;
    }

    private static int ParseInt(string? text, int fallback, int min, int max)
    {
        return int.TryParse(text, out int value) ? Math.Clamp(value, min, max) : fallback;
    }

    private static double ParseDouble(string? text, double fallback, double min, double max)
    {
        return double.TryParse(text, out double value) ? Math.Clamp(value, min, max) : fallback;
    }

    private static double DbToLinear(double db)
    {
        return Math.Pow(10, db / 20);
    }
}

public sealed class WaveformView : Control
{
    private static readonly Typeface TextTypeface = new Typeface("Inter");
    private double[] samples = Array.Empty<double>();
    private int sampleRate = 48000;

    public void SetSamples(double[] value, int rate)
    {
        samples = value;
        sampleRate = rate;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, Bounds);
        Rect plot = new Rect(48, 28, Math.Max(1, Bounds.Width - 72), Math.Max(1, Bounds.Height - 72));
        context.DrawRectangle(null, new Pen(Brushes.LightGray, 1), plot);
        context.DrawLine(new Pen(Brushes.Gray, 1), new APoint(plot.Left, plot.Center.Y), new APoint(plot.Right, plot.Center.Y));

        if (samples.Length == 0)
            return;

        double peak = Math.Max(samples.Select(Math.Abs).DefaultIfEmpty().Max(), 1e-9);
        Pen waveform = new Pen(Brushes.DodgerBlue, 1.4);
        APoint previous = SamplePoint(0, peak, plot);
        for (int i = 1; i < samples.Length; i++)
        {
            APoint next = SamplePoint(i, peak, plot);
            context.DrawLine(waveform, previous, next);
            previous = next;
        }

        DrawText(context, $"{samples.Length / (double)sampleRate:0.000}s  peak {peak:0.000000}", new APoint(48, 6));
        DrawText(context, "+peak", new APoint(6, plot.Top));
        DrawText(context, "0", new APoint(24, plot.Center.Y - 8));
        DrawText(context, "-peak", new APoint(6, plot.Bottom - 16));
    }

    private APoint SamplePoint(int index, double peak, Rect plot)
    {
        double x = plot.Left + (samples.Length == 1 ? 0 : index * plot.Width / (samples.Length - 1));
        double y = plot.Center.Y - samples[index] / peak * plot.Height / 2;
        return new APoint(x, y);
    }

    private static void DrawText(DrawingContext context, string text, APoint point)
    {
        FormattedText formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextTypeface, 12, Brushes.DimGray);
        context.DrawText(formatted, point);
    }
}