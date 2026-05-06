using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Serialization;
using AudioPlugSharp;
using AudioPlugSharpWPF;
using LiveSPICE.PluginCore;

namespace LiveSPICEVst
{
    /// <summary>
    /// Managed VST class to be loaded by SharpSoundDevice
    /// </summary>
    public class LiveSPICEPlugin : AudioPluginWPF
    {
        public SimulationProcessor SimulationProcessor { get; private set; }
        new public EditorView EditorView { get; set; }
        public string SchematicPath { get { return SimulationProcessor.SchematicPath; } }

        AudioIOPortManaged monoInput;
        AudioIOPortManaged monoOutput;

        bool haveSimulationError = false;

        public LiveSPICEPlugin()
        {
            Company = "";
            Website = "livespice.org";
            Contact = "";
            PluginName = "LiveSPICEVst";
            PluginCategory = "Fx";
            PluginVersion = "1.1.0";

            // Unique 64bit ID for the plugin
            PluginID = 0xDC8558DC41A44872;

            HasUserInterface = true;
            EditorWidth = 350;
            EditorHeight = 200;

            //GCSettings.LatencyMode = GCLatencyMode.LowLatency;

            SimulationProcessor = new SimulationProcessor();
        }

        public override void Initialize()
        {
            base.Initialize();

            InputPorts = new AudioIOPortManaged[] { monoInput = new AudioIOPortManaged("Mono Input", EAudioChannelConfiguration.Mono) };
            OutputPorts = new AudioIOPortManaged[] { monoOutput = new AudioIOPortManaged("Mono Output", EAudioChannelConfiguration.Mono) };
        }

        public override void InitializeProcessing()
        {
            base.InitializeProcessing();

            Logger.Log("Initialize Processing");
            Logger.Log("Sample rate: " + Host.SampleRate + " Max Buffer Size: " + Host.MaxAudioBufferSize);

            SimulationProcessor.SampleRate = Host.SampleRate;
        }

        public override UserControl GetEditorView()
        {
            return new EditorView(this);
        }

        /// <summary>
        /// Save our current plugin state
        /// </summary>
        /// <returns>A byte array of data</returns>
        public override byte[] SaveState()
        {
            byte[] stateData = null;

            PluginProgramParameters programParameters = PluginProgramParameters.FromProcessor(SimulationProcessor);
            XmlSerializer serializer = new XmlSerializer(typeof(PluginProgramParameters));

            using (MemoryStream memoryStream = new MemoryStream())
            {
                serializer.Serialize(memoryStream, programParameters);

                stateData = memoryStream.ToArray();
            }

            return stateData;
        }

        /// <summary>
        /// Restore plugin state
        /// </summary>
        /// <param name="stateData">Byte array of data to restore</param>
        public override void RestoreState(byte[] stateData)
        {
            XmlSerializer serializer = new XmlSerializer(typeof(PluginProgramParameters));

            try
            {
                using (MemoryStream memoryStream = new MemoryStream(stateData))
                {
                    PluginProgramParameters programParameters = serializer.Deserialize(memoryStream) as PluginProgramParameters;

                    if (string.IsNullOrEmpty(programParameters.SchematicPath))
                    {
                        haveSimulationError = false;

                        SimulationProcessor.ClearSchematic();
                    }
                    else
                    {
                        LoadSchematic(programParameters.SchematicPath);
                    }

                    programParameters.ApplyTo(SimulationProcessor);

                    if (EditorView != null)
                        EditorView.UpdateSchematic();
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Load state failed: " + ex.Message);
            }
        }

        public void LoadSchematic(string path)
        {
            haveSimulationError = false;

            try
            {
                SimulationProcessor.LoadSchematic(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(String.Format("Error loading schematic from: {0}\n\n{1}", path, ex.ToString()), "Schematic Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public override void Process()
        {
            if (haveSimulationError)
            {
                monoInput.PassThroughTo(monoOutput);
            }
            else
            {
                double[][] inBuffers = monoInput.GetAudioBuffers();
                double[][] outBuffers = monoOutput.GetAudioBuffers();

                try
                {
                    SimulationProcessor.RunSimulation(inBuffers, outBuffers, inBuffers[0].Length);
                }
                catch (Exception ex)
                {
                    haveSimulationError = true;

                    new Thread(() =>
                    {
                        MessageBox.Show("Error running circuit simulation.\n\n" + ex.Message, "Simulation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }).Start();
                }
            }
        }
    }
}
