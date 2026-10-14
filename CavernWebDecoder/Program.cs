using System;
using System.IO;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Cavern;
using Cavern.Channels;
using Cavern.Format;
using Cavern.Format.Renderers;

[assembly: SupportedOSPlatform("browser")]

public partial class Program
{
    private static bool _cancelRequested = false;

    public static void Main()
    {
        Console.WriteLine("[CavernWASM] E-AC-3 JOC 22.2 Web Decoder initialized.");
    }

    [JSImport("workerCallbacks.onProgress", "worker.js")]
    internal static partial void OnProgress(int framesProcessed, int totalFrames, double fps);

    [JSImport("workerCallbacks.onLog", "worker.js")]
    internal static partial void OnLog(string message, bool isError);

    [JSExport]
    public static void RequestCancel()
    {
        _cancelRequested = true;
    }

    /// <summary>
    /// Decodes an E-AC-3 JOC (or MP4/M4A/MKV/WAV) byte array and renders it into the target CICP layout.
    /// CICP 13 = 22.2 surround (24 channels: 11/11.2 SMPTE 2036-2 / NHK).
    /// </summary>
    [JSExport]
    public static byte[]? DecodeEac3Joc(
        byte[] inputBytes,
        string fileName,
        int cicpLayout,
        int pcmBitDepth,
        int targetSampleRate,
        double targetLoudnessDb,
        int drcEffectType,
        bool printStderr)
    {
        _cancelRequested = false;

        try
        {
            if (printStderr)
                OnLog($"[Cavern] Opening '{fileName}' ({inputBytes.Length} bytes)...", false);

            byte[] streamBytes = Mp4Ec3Extractor.ExtractIfMp4(inputBytes, fileName, printStderr, OnLog);

            using MemoryStream inputStream = new MemoryStream(streamBytes, writable: false);
            using AudioReader reader = AudioReader.Open(inputStream);
            reader.ReadHeader();

            Renderer renderer = reader.GetRenderer();
            int sampleRate = targetSampleRate > 0 ? targetSampleRate : reader.SampleRate;
            int updateRate = 1536; // 1 E-AC-3 frame = 1536 samples (6 blocks * 256 samples)

            ConfigureCicpLayout(cicpLayout);
            int outputChannels = Listener.Channels.Length;

            if (printStderr)
            {
                OnLog($"[Cavern] Input SR: {reader.SampleRate} Hz, Length: {reader.Length} samples, JOC Objects: {renderer.Objects.Count}", false);
                OnLog($"[Cavern] Target CICP {cicpLayout} -> {outputChannels} channels @ {sampleRate} Hz, {pcmBitDepth}-bit", false);
            }

            Listener listener = new Listener
            {
                SampleRate = sampleRate,
                UpdateRate = updateRate,
                Volume = LoudnessToLinearGain(targetLoudnessDb, drcEffectType)
            };

            foreach (Source source in renderer.Objects)
            {
                listener.AttachSource(source);
            }

            BitDepth bitDepth = pcmBitDepth switch
            {
                16 => BitDepth.Int16,
                24 => BitDepth.Int24,
                32 => BitDepth.Float32,
                _ => BitDepth.Int24
            };

            long totalSamplesPerChannel = reader.Length;
            if (sampleRate != reader.SampleRate && reader.SampleRate > 0)
            {
                totalSamplesPerChannel = (long)((double)reader.Length * sampleRate / reader.SampleRate);
            }

            int totalFrames = (int)Math.Max(1, (totalSamplesPerChannel + updateRate - 1) / updateRate);

            using MemoryStream outputStream = new MemoryStream();
            using AudioWriter writer = new RIFFWaveWriter(
                outputStream,
                outputChannels,
                totalSamplesPerChannel,
                sampleRate,
                bitDepth);

            writer.WriteHeader();

            long samplesRendered = 0;
            int framesProcessed = 0;
            DateTime startTime = DateTime.UtcNow;

            while (samplesRendered < totalSamplesPerChannel)
            {
                if (_cancelRequested)
                {
                    if (printStderr) OnLog("[Cavern] Decoding cancelled by user.", true);
                    return null;
                }

                float[] renderedBlock = listener.Render();
                long remainingSamples = totalSamplesPerChannel - samplesRendered;
                long samplesInThisBlock = Math.Min(updateRate, remainingSamples);
                long floatsToWrite = samplesInThisBlock * outputChannels;

                writer.WriteBlock(renderedBlock, 0, floatsToWrite);

                samplesRendered += samplesInThisBlock;
                framesProcessed++;

                if (framesProcessed % 8 == 0 || samplesRendered >= totalSamplesPerChannel)
                {
                    double elapsedSec = Math.Max(0.001, (DateTime.UtcNow - startTime).TotalSeconds);
                    double fps = framesProcessed / elapsedSec;
                    OnProgress(framesProcessed, totalFrames, fps);
                }
            }

            if (printStderr)
                OnLog($"[Cavern] Finished {framesProcessed} frames ({outputStream.Length} bytes).", false);

            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            OnLog($"[Cavern Error] {ex.Message}\n{ex.StackTrace}", true);
            throw;
        }
    }

    private static float LoudnessToLinearGain(double targetLoudnessDb, int drcEffectType)
    {
        double offsetDb = 0.0;
        if (!double.IsNaN(targetLoudnessDb) && targetLoudnessDb != 0.0)
        {
            offsetDb = Math.Clamp(targetLoudnessDb - (-24.0), -18.0, 12.0);
        }
        if (drcEffectType == 1) offsetDb -= 3.0;
        return (float)Math.Pow(10.0, offsetDb / 20.0);
    }

    private static void ConfigureCicpLayout(int cicp)
    {
        switch (cicp)
        {
            case 2:  // 2.0 Stereo
                Listener.ReplaceChannels(2);
                break;
            case 6:  // 5.1 Surround
                Listener.ReplaceChannels(6);
                break;
            case 12: // 7.1 Surround
                Listener.ReplaceChannels(8);
                break;
            case 14: // 5.1.2 Surround
                Listener.ReplaceChannels(8);
                break;
            case 16: // 5.1.4 Surround
                Listener.ReplaceChannels(10);
                break;
            case 19: // 7.1.4 Surround
                Listener.ReplaceChannels(12);
                break;
            case 13: // 22.2 Surround • 11/11.2 (24 channels)
            default:
                Listener.ReplaceChannels(24);
                break;
        }
    }
}
