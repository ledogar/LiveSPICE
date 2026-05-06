using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Circuit;
using LiveSPICE.PluginCore;

namespace LiveSPICE.Avalonia;

public sealed class PluginEditorWindow : Window
{
    private readonly SimulationProcessor processor;
    private readonly SchematicCanvas schematicCanvas;
    private readonly StackPanel controlsPanel;
    private readonly ComboBox oversampleBox;
    private readonly ComboBox iterationsBox;
    private readonly TextBlock loadedText;
    private string? loadedPath;

    public PluginEditorWindow() : this(new SimulationProcessor())
    {
    }

    public PluginEditorWindow(SimulationProcessor processor)
    {
        this.processor = processor;
        Title = "LiveSPICE Plugin";
        Width = 700;
        Height = 420;
        MinWidth = 520;
        MinHeight = 320;

        schematicCanvas = new SchematicCanvas { IsHitTestVisible = false, Opacity = 0.45, Margin = new Thickness(0) };
        controlsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        oversampleBox = CreateOptionBox(new[] { 1, 2, 4, 8 }, processor.Oversample);
        iterationsBox = CreateOptionBox(new[] { 1, 2, 4, 8, 16, 32, 64 }, processor.Iterations);
        loadedText = new TextBlock { Text = "Load Schematic", TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

        Content = BuildLayout();
        RefreshFromProcessor();
    }

    public void LoadSchematic(string path)
    {
        processor.LoadSchematic(path);
        loadedPath = path;
        RefreshFromProcessor();
    }

    private Control BuildLayout()
    {
        Grid root = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(72, 72, 72)),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
        };

        root.Children.Add(schematicCanvas);
        Grid.SetRowSpan(schematicCanvas, 4);

        Button loadButton = new Button { Content = loadedText, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(14, 12, 14, 4) };
        loadButton.Click += LoadButton_Click;
        root.Children.Add(loadButton);

        StackPanel commandRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(14, 4) };
        Button reloadButton = new Button { Content = "Reload" };
        reloadButton.Click += ReloadButton_Click;
        Button viewButton = new Button { Content = "View" };
        viewButton.Click += ViewButton_Click;
        Button aboutButton = new Button { Content = "About" };
        aboutButton.Click += AboutButton_Click;
        commandRow.Children.Add(reloadButton);
        commandRow.Children.Add(viewButton);
        commandRow.Children.Add(aboutButton);
        root.Children.Add(commandRow);
        Grid.SetRow(commandRow, 1);

        StackPanel settingsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(14, 4) };
        settingsRow.Children.Add(new TextBlock { Text = "Oversample", Foreground = Brushes.White, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        settingsRow.Children.Add(oversampleBox);
        settingsRow.Children.Add(new TextBlock { Text = "Iterations", Foreground = Brushes.White, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        settingsRow.Children.Add(iterationsBox);
        root.Children.Add(settingsRow);
        Grid.SetRow(settingsRow, 2);

        ScrollViewer controlsScroll = new ScrollViewer
        {
            Content = controlsPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(14, 8, 14, 14),
        };
        root.Children.Add(controlsScroll);
        Grid.SetRow(controlsScroll, 3);

        return root;
    }

    private static ComboBox CreateOptionBox(int[] values, int selected)
    {
        ComboBox comboBox = new ComboBox { Width = 76, ItemsSource = values.Cast<object>().ToArray() };
        comboBox.SelectedItem = selected;
        return comboBox;
    }

    private async void LoadButton_Click(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Circuit Schematics") { Patterns = new[] { "*.schx" } },
                FilePickerFileTypes.All,
            },
            Title = "Open schematic",
        });

        string? path = files.FirstOrDefault()?.Path.LocalPath;
        if (!string.IsNullOrEmpty(path))
            LoadSchematic(path);
    }

    private void ReloadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(loadedPath) && File.Exists(loadedPath))
            LoadSchematic(loadedPath);
    }

    private void ViewButton_Click(object? sender, RoutedEventArgs e)
    {
        if (processor.Schematic == null)
            return;

        Window window = new Window
        {
            Title = processor.SchematicName,
            Width = 1000,
            Height = 720,
            Content = new SchematicCanvas { Document = new SchematicDocument(processor.Schematic, loadedPath) },
        };
        window.Show();
    }

    private void AboutButton_Click(object? sender, RoutedEventArgs e)
    {
        Window about = new Window
        {
            Title = "About LiveSPICE Plugin",
            Width = 320,
            Height = 160,
            Content = new TextBlock
            {
                Text = "LiveSPICE Linux Plugin\nlivespice.org",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            },
        };
        about.ShowDialog(this);
    }

    private void RefreshFromProcessor()
    {
        loadedText.Text = processor.Schematic == null ? "Load Schematic" : processor.SchematicName;
        schematicCanvas.Document = new SchematicDocument(processor.Schematic ?? new Schematic(), loadedPath);
        oversampleBox.SelectionChanged -= OversampleBox_SelectionChanged;
        iterationsBox.SelectionChanged -= IterationsBox_SelectionChanged;
        oversampleBox.SelectedItem = processor.Oversample;
        iterationsBox.SelectedItem = processor.Iterations;
        oversampleBox.SelectionChanged += OversampleBox_SelectionChanged;
        iterationsBox.SelectionChanged += IterationsBox_SelectionChanged;
        RebuildControls();
    }

    private void RebuildControls()
    {
        controlsPanel.Children.Clear();
        foreach (IComponentWrapper wrapper in processor.InteractiveComponents)
            controlsPanel.Children.Add(CreateControl(wrapper));
    }

    private Control CreateControl(IComponentWrapper wrapper)
    {
        StackPanel panel = new StackPanel { Width = 96, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = wrapper.Name,
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        switch (wrapper)
        {
            case PotWrapper potWrapper:
                Slider slider = new Slider { Minimum = 0, Maximum = 1, Value = potWrapper.PotValue, Width = 82 };
                slider.PropertyChanged += (_, args) =>
                {
                    if (args.Property == RangeBase.ValueProperty)
                        potWrapper.PotValue = slider.Value;
                };
                panel.Children.Add(slider);
                break;
            case DoubleThrowWrapper doubleThrowWrapper:
                ToggleButton toggle = new ToggleButton { IsChecked = doubleThrowWrapper.Engaged, HorizontalAlignment = HorizontalAlignment.Center };
                toggle.IsCheckedChanged += (_, _) => doubleThrowWrapper.Engaged = toggle.IsChecked == true;
                panel.Children.Add(toggle);
                break;
            case MultiThrowWrapper multiThrowWrapper:
                ComboBox comboBox = CreateOptionBox(new[] { 0, 1, 2 }, multiThrowWrapper.Position);
                comboBox.SelectionChanged += (_, _) =>
                {
                    if (comboBox.SelectedItem is int position)
                        multiThrowWrapper.Position = position;
                };
                panel.Children.Add(comboBox);
                break;
        }

        return panel;
    }

    private void OversampleBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (oversampleBox.SelectedItem is int value)
            processor.Oversample = value;
    }

    private void IterationsBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (iterationsBox.SelectedItem is int value)
            processor.Iterations = value;
    }
}
