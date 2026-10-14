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
    private static MemoryStream? _inputStream;
    private static AudioReader? _reader;
    private static Renderer? _renderer;
    private static Listener? _listener;

    private static int _outputChannels = 24;
    private static int _sampleRate = 48000;
    private static int _pcmBitDepth = 24;
    private static int _updateRate = 256;
    private static long _totalSamplesPerChannel = 0;
    private static long _samplesRendered = 0;
    private static int _totalFrames = 0;
    private static string _lastLog = "";
    private static string _lastError = "";

    public static void Main()
    {
        Console.WriteLine("[Cavernize WASM] E-AC-3 JOC 22.2 Web Decoder loaded.");
    }

    [JSExport]
    public static string GetLastLog() => _lastLog;

    [JSExport]
    public static string GetLastError() => _lastError;

    [JSExport]
    public static int GetOutputChannels() => _outputChannels;

    [JSExport]
    public static int GetSampleRate() => _sampleRate;

    [JSExport]
    public static int GetTotalFrames() => _totalFrames;

    [JSExport]
    public static int GetFramesProcessed() => (int)(_samplesRendered / 1536);

    /// <summary>
    /// Opens the audio file (first trying Cavern.Format's native AudioReader.Open for MP4/M4A/MKV/EC3/WAV,
    /// and falling back to raw E-AC-3 syncframe extraction if needed) and configures the Cavernize Listener.
    /// Returns total E-AC-3 frames (> 0) on success, or -1 on error.
    /// </summary>
    [JSExport]
    public static int InitSession(
        byte[] inputBytes,
        string fileName,
        string targetLayout,
        int pcmBitDepth,
        int targetSampleRate,
        int updateRateSamples,
        int audioQualityIndex,
        double gainDb,
        bool lfeSeparation)
    {
        CloseSession();
        _lastError = "";
        _lastLog = "";

        try
        {
            // 1. Try Cavern.Format's native container/stream reader first (MP4Reader, MatroskaReader, EnhancedAC3Reader, RIFFWaveReader)
            try
            {
                _inputStream = new MemoryStream(inputBytes, writable: false);
                _reader = AudioReader.Open(_inputStream);
                _reader.ReadHeader();
                _renderer = _reader.GetRenderer();
            }
            catch (Exception nativeEx)
            {
                // 2. Fallback: extract verified 0x0B77 E-AC-3 syncframes from M4A/MP4 container
                byte[]? extractedEc3 = Mp4Ec3Extractor.ExtractIfMp4(inputBytes, fileName, false, (_, _) => { });
                if (extractedEc3 != null && extractedEc3.Length > 0 && !ReferenceEquals(extractedEc3, inputBytes))
                {
                    _inputStream?.Dispose();
                    _inputStream = new MemoryStream(extractedEc3, writable: false);
                    _reader = AudioReader.Open(_inputStream);
                    _reader.ReadHeader();
                    _renderer = _reader.GetRenderer();
                }
                else
                {
                    throw new Exception($"Cavern AudioReader could not open '{fileName}': {nativeEx.Message}", nativeEx);
                }
            }

            _sampleRate = targetSampleRate > 0 ? targetSampleRate : _reader.SampleRate;
            if (_sampleRate <= 0) _sampleRate = 48000;

            _updateRate = updateRateSamples > 0 ? updateRateSamples : 256;
            _pcmBitDepth = pcmBitDepth;

            bool binaural = ConfigureCavernizeLayout(targetLayout);
            _outputChannels = Listener.Channels.Length;

            float linearVolume = (float)Math.Pow(10.0, Math.Clamp(gainDb, -40.0, 20.0) / 20.0);

            QualityModes quality = audioQualityIndex switch
            {
                0 => QualityModes.Low,
                1 => QualityModes.Medium,
                2 => QualityModes.High,
                3 => QualityModes.Perfect,
                _ => QualityModes.High
            };

            _listener = new Listener
            {
                SampleRate = _sampleRate,
                UpdateRate = _updateRate,
                Volume = linearVolume,
                AudioQuality = quality,
                LFESeparation = lfeSeparation
            };
            Listener.HeadphoneVirtualizer = binaural;

            foreach (Source source in _renderer.Objects)
            {
                _listener.AttachSource(source);
            }

            _totalSamplesPerChannel = _reader.Length;
            if (_totalSamplesPerChannel > 0 && _sampleRate != _reader.SampleRate && _reader.SampleRate > 0)
            {
                _totalSamplesPerChannel = (long)((double)_reader.Length * _sampleRate / _reader.SampleRate);
            }

            if (_totalSamplesPerChannel <= 0 && _inputStream != null)
            {
                long estimatedFrames = Math.Max(1, _inputStream.Length / 1536);
                _totalSamplesPerChannel = estimatedFrames * 1536;
            }

            _samplesRendered = 0;
            _totalFrames = (int)Math.Max(1, (_totalSamplesPerChannel + 1535) / 1536);

            _lastLog = $"[Cavernize] Opened '{fileName}' | Input: {_reader.SampleRate} Hz, JOC Objects: {_renderer.Objects.Count} | Target: {targetLayout} ({_outputChannels} ch) @ {_sampleRate} Hz, {_pcmBitDepth}-bit, Gain: {gainDb:0.0} dB, Frames: {_totalFrames}";
            return _totalFrames;
        }
        catch (Exception ex)
        {
            _lastError = $"{ex.Message}";
            Console.Error.WriteLine($"[Cavernize Init Error] {ex}");
            CloseSession();
            return -1;
        }
    }

    /// <summary>
    /// Renders a batch of E-AC-3 frames (1 frame = 1536 samples per channel) and returns the interleaved PCM bytes.
    /// Returns an empty array when decoding is complete.
    /// </summary>
    [JSExport]
    public static byte[] RenderNextBatch(int framesToRender)
    {
        if (_listener == null || _reader == null || _inputStream == null)
            return Array.Empty<byte>();

        if (_samplesRendered >= _totalSamplesPerChannel)
            return Array.Empty<byte>();

        try
        {
            int bytesPerSample = _pcmBitDepth switch
            {
                8 => 1,
                16 => 2,
                24 => 3,
                32 => 4,
                _ => 3
            };

            long targetSamples = Math.Min(_totalSamplesPerChannel - _samplesRendered, (long)framesToRender * 1536);
            if (targetSamples <= 0)
                return Array.Empty<byte>();

            int blocks = (int)((targetSamples + _updateRate - 1) / _updateRate);
            byte[] batchBuffer = new byte[blocks * _updateRate * _outputChannels * bytesPerSample];
            int byteOffset = 0;

            for (int b = 0; b < blocks; b++)
            {
                if (_samplesRendered >= _totalSamplesPerChannel)
                    break;

                float[] renderedBlock = _listener.Render();
                long remaining = _totalSamplesPerChannel - _samplesRendered;
                int samplesInBlock = (int)Math.Min(_updateRate, remaining);
                int floatCount = samplesInBlock * _outputChannels;

                byteOffset += ConvertFloatToPcm(renderedBlock, floatCount, _pcmBitDepth, batchBuffer, byteOffset);
                _samplesRendered += samplesInBlock;
            }

            if (byteOffset == batchBuffer.Length)
                return batchBuffer;

            byte[] trimmed = new byte[byteOffset];
            Buffer.BlockCopy(batchBuffer, 0, trimmed, 0, byteOffset);
            return trimmed;
        }
        catch (Exception ex)
        {
            _lastError = $"Render error at frame {GetFramesProcessed()}: {ex.Message}";
            Console.Error.WriteLine(_lastError);
            return Array.Empty<byte>();
        }
    }

    [JSExport]
    public static void CloseSession()
    {
        try { _reader?.Dispose(); } catch { }
        try { _inputStream?.Dispose(); } catch { }
        _reader = null;
        _renderer = null;
        _listener = null;
        _inputStream = null;
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
                Listener.ReplaceChannels(new Channel[]
                {
                    new Channel(0, -30),
                    new Channel(0, 30),
                    new Channel(0, 0),
                    new Channel(0, 0, true),
                    new Channel(0, -110),
                    new Channel(0, 110),
                    new Channel(-45, -45),
                    new Channel(-45, 45)
                });
                return false;
            case "5.1.4":
                Listener.ReplaceChannels(10);
                return false;
            case "7.1.2":
                Listener.ReplaceChannels(new Channel[]
                {
                    new Channel(0, -30),
                    new Channel(0, 30),
                    new Channel(0, 0),
                    new Channel(0, 0, true),
                    new Channel(0, -150),
                    new Channel(0, 150),
                    new Channel(0, -90),
                    new Channel(0, 90),
                    new Channel(-45, -45),
                    new Channel(-45, 45)
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
                Listener.ReplaceChannels(24);
                return false;
        }
    }
}
