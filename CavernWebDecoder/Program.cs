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
        Console.WriteLine("[Cavernize WASM] E-AC-3 JOC 22.2 Web Decoder ready.");
    }

    [JSImport("workerCallbacks.onInit", "worker.js")]
    internal static partial void OnInit(int outputChannels, int sampleRate, int bitDepth, int totalFrames);

    [JSImport("workerCallbacks.onPcmChunk", "worker.js")]
    internal static partial void OnPcmChunk(byte[] pcmChunk);

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
    /// Decodes an E-AC-3 JOC (or M4A/MP4/MKV/WAV) stream using Cavernize's object renderer
    /// and streams PCM chunks back to the Web Worker to build the multichannel WAV file.
    /// </summary>
    [JSExport]
    public static int DecodeWithCavernize(
        byte[] inputBytes,
        string fileName,
        string targetLayout,
        int pcmBitDepth,
        int targetSampleRate,
        int updateRateSamples,
        int audioQualityIndex,
        double gainDb,
        bool lfeSeparation,
        bool printLogs)
    {
        _cancelRequested = false;

        try
        {
            if (printLogs)
                OnLog($"[Cavernize] Opening '{fileName}' ({inputBytes.Length:N0} bytes)...", false);

            // Extract raw E-AC-3 JOC bitstream if wrapped in an MP4/M4A container
            byte[] streamBytes = Mp4Ec3Extractor.ExtractIfMp4(inputBytes, fileName, printLogs, OnLog);

            using MemoryStream inputStream = new MemoryStream(streamBytes, writable: false);
            using AudioReader reader = AudioReader.Open(inputStream);
            reader.ReadHeader();

            Renderer renderer = reader.GetRenderer();
            int sampleRate = targetSampleRate > 0 ? targetSampleRate : reader.SampleRate;
            int updateRate = updateRateSamples > 0 ? updateRateSamples : 256; // Cavernize default: 256 samples (1 E-AC-3 block)

            // Configure Cavernize Render Target
            bool binaural = ConfigureCavernizeLayout(targetLayout);
            int outputChannels = Listener.Channels.Length;

            // Convert dB gain (Cavernize default = 0.0 dB => Volume = 1.0f)
            float linearVolume = (float)Math.Pow(10.0, Math.Clamp(gainDb, -40.0, 20.0) / 20.0);

            QualityModes quality = audioQualityIndex switch
            {
                0 => QualityModes.Low,
                1 => QualityModes.Medium,
                2 => QualityModes.High,
                3 => QualityModes.Perfect,
                _ => QualityModes.High
            };

            Listener listener = new Listener
            {
                SampleRate = sampleRate,
                UpdateRate = updateRate,
                Volume = linearVolume,
                AudioQuality = quality,
                LFESeparation = lfeSeparation
            };
            Listener.HeadphoneVirtualizer = binaural;

            foreach (Source source in renderer.Objects)
            {
                listener.AttachSource(source);
            }

            long totalSamplesPerChannel = reader.Length;
            if (sampleRate != reader.SampleRate && reader.SampleRate > 0 && totalSamplesPerChannel > 0)
            {
                totalSamplesPerChannel = (long)((double)reader.Length * sampleRate / reader.SampleRate);
            }

            // 1 E-AC-3 JOC syncframe = 1536 samples per channel
            const int samplesPerEc3Frame = 1536;
            int totalFrames = (int)Math.Max(1, (totalSamplesPerChannel + samplesPerEc3Frame - 1) / samplesPerEc3Frame);

            if (printLogs)
            {
                OnLog($"[Cavernize] Input: {reader.SampleRate} Hz, JOC Objects: {renderer.Objects.Count}, Total frames: {totalFrames}", false);
                OnLog($"[Cavernize] Render target: {targetLayout} ({outputChannels} ch) @ {sampleRate} Hz, {pcmBitDepth}-bit, Gain: {gainDb:0.0} dB, UpdateRate: {updateRate}", false);
            }

            OnInit(outputChannels, sampleRate, pcmBitDepth, totalFrames);

            int bytesPerSample = pcmBitDepth switch
            {
                8 => 1,
                16 => 2,
                24 => 3,
                32 => 4,
                _ => 3
            };

            // Buffer ~16 E-AC-3 frames per chunk (~1-2 MB) to minimize JS interop overhead and keep RAM flat
            int blocksPerChunk = Math.Max(1, (samplesPerEc3Frame * 16) / updateRate);
            byte[] chunkBuffer = new byte[blocksPerChunk * updateRate * outputChannels * bytesPerSample];
            int chunkByteOffset = 0;

            long samplesRendered = 0;
            int lastReportedFrame = -1;
            DateTime startTime = DateTime.UtcNow;

            while (samplesRendered < totalSamplesPerChannel)
            {
                if (_cancelRequested)
                {
                    if (printLogs) OnLog("[Cavernize] Decoding cancelled by user.", true);
                    return -1;
                }

                float[] renderedBlock = listener.Render();
                long remainingSamples = totalSamplesPerChannel - samplesRendered;
                int samplesInBlock = (int)Math.Min(updateRate, remainingSamples);
                int floatCount = samplesInBlock * outputChannels;

                int bytesNeeded = floatCount * bytesPerSample;
                if (chunkByteOffset + bytesNeeded > chunkBuffer.Length)
                {
                    FlushChunk(chunkBuffer, chunkByteOffset);
                    chunkByteOffset = 0;
                }

                chunkByteOffset += ConvertFloatToPcm(renderedBlock, floatCount, pcmBitDepth, chunkBuffer, chunkByteOffset);
                samplesRendered += samplesInBlock;

                int framesProcessed = (int)(samplesRendered / samplesPerEc3Frame);
                if (framesProcessed - lastReportedFrame >= 8 || samplesRendered >= totalSamplesPerChannel)
                {
                    lastReportedFrame = framesProcessed;
                    double elapsedSec = Math.Max(0.001, (DateTime.UtcNow - startTime).TotalSeconds);
                    double fps = framesProcessed / elapsedSec;
                    OnProgress(framesProcessed, totalFrames, fps);
                }

                // Stop cleanly if underlying stream reached EOF
                if (inputStream.Position >= inputStream.Length && samplesRendered > 0)
                {
                    break;
                }
            }

            if (chunkByteOffset > 0)
            {
                FlushChunk(chunkBuffer, chunkByteOffset);
            }

            int finalFrames = (int)Math.Max(1, (samplesRendered + samplesPerEc3Frame - 1) / samplesPerEc3Frame);
            double totalSec = Math.Max(0.001, (DateTime.UtcNow - startTime).TotalSeconds);
            OnProgress(finalFrames, finalFrames, finalFrames / totalSec);

            if (printLogs)
                OnLog($"[Cavernize] Completed {finalFrames} E-AC-3 JOC frames in {totalSec:0.2f}s.", false);

            return finalFrames;
        }
        catch (Exception ex)
        {
            OnLog($"[Cavernize Error] {ex.Message}\n{ex.StackTrace}", true);
            throw;
        }
    }

    private static void FlushChunk(byte[] buffer, int length)
    {
        if (length == buffer.Length)
        {
            OnPcmChunk(buffer);
        }
        else
        {
            byte[] slice = new byte[length];
            Buffer.BlockCopy(buffer, 0, slice, 0, length);
            OnPcmChunk(slice);
        }
    }

    private static int ConvertFloatToPcm(float[] samples, int count, int bitDepth, byte[] dest, int offset)
    {
        int startOffset = offset;
        switch (bitDepth)
        {
            case 8:
                for (int i = 0; i < count; i++)
                {
                    float s = Math.Clamp(samples[i], -1f, 1f);
                    dest[offset++] = (byte)((s + 1f) * 127.5f);
                }
                break;

            case 16:
                for (int i = 0; i < count; i++)
                {
                    float s = Math.Clamp(samples[i], -1f, 1f);
                    short val = (short)(s * 32767f);
                    dest[offset++] = (byte)(val & 0xFF);
                    dest[offset++] = (byte)((val >> 8) & 0xFF);
                }
                break;

            case 24:
                for (int i = 0; i < count; i++)
                {
                    float s = Math.Clamp(samples[i], -1f, 1f);
                    int val = (int)(s * 8388607f);
                    dest[offset++] = (byte)(val & 0xFF);
                    dest[offset++] = (byte)((val >> 8) & 0xFF);
                    dest[offset++] = (byte)((val >> 16) & 0xFF);
                }
                break;

            case 32:
                Buffer.BlockCopy(samples, 0, dest, offset, count * 4);
                offset += count * 4;
                break;
        }
        return offset - startOffset;
    }

    /// <summary>
    /// Configures Cavern's Listener channels according to Cavernize's built-in RenderTargets.
    /// Returns true if Headphone Virtualizer mode is selected.
    /// </summary>
    private static bool ConfigureCavernizeLayout(string layoutKey)
    {
        switch (layoutKey)
        {
            case "virtualizer":
                Listener.ReplaceChannels(2);
                return true;
            case "2.0":
                Listener.ReplaceChannels(2);
                return false;
            case "4.0":
                Listener.ReplaceChannels(4);
                return false;
            case "5.1":
                Listener.ReplaceChannels(6);
                return false;
            case "7.1":
                Listener.ReplaceChannels(8);
                return false;
            case "5.1.2":
                // 5.1 bed + 2 top middle/front channels
                Listener.ReplaceChannels(new Channel[]
                {
                    new Channel(0, -30),        // FL
                    new Channel(0, 30),         // FR
                    new Channel(0, 0),          // FC
                    new Channel(0, 0, true),    // LFE
                    new Channel(0, -110),       // SL
                    new Channel(0, 110),        // SR
                    new Channel(-45, -45),      // TFL
                    new Channel(-45, 45)        // TFR
                });
                return false;
            case "5.1.4":
                Listener.ReplaceChannels(10);
                return false;
            case "7.1.2":
                // 7.1 bed + 2 top channels
                Listener.ReplaceChannels(new Channel[]
                {
                    new Channel(0, -30),        // FL
                    new Channel(0, 30),         // FR
                    new Channel(0, 0),          // FC
                    new Channel(0, 0, true),    // LFE
                    new Channel(0, -150),       // BL
                    new Channel(0, 150),        // BR
                    new Channel(0, -90),        // SL
                    new Channel(0, 90),         // SR
                    new Channel(-45, -45),      // TFL
                    new Channel(-45, 45)        // TFR
                });
                return false;
            case "7.1.4":
                Listener.ReplaceChannels(12);
                return false;
            case "7.1.6":
                Listener.ReplaceChannels(14);
                return false;
            case "9.1.6":
                Listener.ReplaceChannels(16);
                return false;
            case "22.2":
            default:
                // Cavern's 24-channel matrix = 22.2 SMPTE 2036-2 (11/11.2)
                Listener.ReplaceChannels(24);
                return false;
        }
    }
}
