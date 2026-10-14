using System;
using System.IO;
using System.Runtime.CompilerServices;
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
    private static byte[]? _reusableBatchBuffer;

    public static void Main()
    {
        Console.WriteLine("[Cavernize WASM AOT+SIMD] E-AC-3 JOC Decoder ready.");
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
            // Para contenedores .m4a / .mp4 extraemos primero el bitstream E-AC-3 JOC puro (0x0B77)
            // para que EnhancedAC3Reader decodifique todas las subtramas JOC sin cortes
            byte[]? streamBytes = Mp4Ec3Extractor.ExtractIfMp4(inputBytes, fileName, false, (_, _) => { });
            if (streamBytes != null && streamBytes.Length > 0)
            {
                _inputStream = new MemoryStream(streamBytes, writable: false);
            }
            else
            {
                _inputStream = new MemoryStream(inputBytes, writable: false);
            }

            try
            {
                _reader = AudioReader.Open(_inputStream);
                _reader.ReadHeader();
                _renderer = _reader.GetRenderer();
            }
            catch
            {
                _inputStream?.Dispose();
                _inputStream = new MemoryStream(inputBytes, writable: false);
                _reader = AudioReader.Open(_inputStream);
                _reader.ReadHeader();
                _renderer = _reader.GetRenderer();
            }

            _sampleRate = targetSampleRate > 0 ? targetSampleRate : _reader.SampleRate;
            if (_sampleRate <= 0) _sampleRate = 48000;

            // IMPORTANTE: En E-AC-3 JOC cada bloque de audio es de 256 muestras (6 bloques = 1536 muestras).
            // Nunca superar 256 muestras en UpdateRate para que JOC actualice todos los objetos dinámicos.
            _updateRate = (updateRateSamples > 0 && updateRateSamples <= 256) ? updateRateSamples : 256;
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
                _ => QualityModes.Perfect
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

            _lastLog = $"[Cavernize] '{fileName}' | SR: {_reader.SampleRate} Hz | JOC Objects: {_renderer.Objects.Count} | Layout: {targetLayout} ({_outputChannels} ch) | UpdateRate: {_updateRate} | Frames: {_totalFrames}";
            return _totalFrames;
        }
        catch (Exception ex)
        {
            _lastError = $"{ex}";
            Console.Error.WriteLine($"[Cavernize Init Error] {ex}");
            CloseSession();
            return -1;
        }
    }

    [JSExport]
    public static byte[] RenderNextBatch(int framesToRender)
    {
        if (_listener == null || _reader == null || _inputStream == null)
            return Array.Empty<byte>();

        if (_samplesRendered >= _totalSamplesPerChannel)
            return Array.Empty<byte>();

        try
        {
            int bytesPerSample = _pcmBitDepth >> 3;
            long targetSamples = Math.Min(_totalSamplesPerChannel - _samplesRendered, (long)framesToRender * 1536);
            if (targetSamples <= 0)
                return Array.Empty<byte>();

            int blocks = (int)((targetSamples + _updateRate - 1) / _updateRate);
            int requiredBytes = blocks * _updateRate * _outputChannels * bytesPerSample;

            if (_reusableBatchBuffer == null || _reusableBatchBuffer.Length != requiredBytes)
            {
                _reusableBatchBuffer = new byte[requiredBytes];
            }

            int byteOffset = 0;

            for (int b = 0; b < blocks; b++)
            {
                if (_samplesRendered >= _totalSamplesPerChannel)
                    break;

                float[] renderedBlock = _listener.Render();
                long remaining = _totalSamplesPerChannel - _samplesRendered;
                int samplesInBlock = (int)Math.Min(_updateRate, remaining);
                int floatCount = samplesInBlock * _outputChannels;

                byteOffset += ConvertFloatToPcmFast(renderedBlock, floatCount, _pcmBitDepth, _reusableBatchBuffer, byteOffset);
                _samplesRendered += samplesInBlock;
            }

            if (byteOffset == _reusableBatchBuffer.Length)
                return _reusableBatchBuffer;

            byte[] trimmed = new byte[byteOffset];
            Buffer.BlockCopy(_reusableBatchBuffer, 0, trimmed, 0, byteOffset);
            return trimmed;
        }
        catch (Exception ex)
        {
            _lastError = $"Render error at frame {GetFramesProcessed()}: {ex}";
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
        _reusableBatchBuffer = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int ConvertFloatToPcmFast(float[] samples, int count, int bitDepth, byte[] dest, int offset)
    {
        int startOffset = offset;
        fixed (float* srcPtr = samples)
        fixed (byte* dstBase = dest)
        {
            byte* dst = dstBase + offset;
            switch (bitDepth)
            {
                case 24:
                    for (int i = 0; i < count; i++)
                    {
                        float s = srcPtr[i];
                        if (s > 1f) s = 1f;
                        else if (s < -1f) s = -1f;
                        int val = (int)(s * 8388607f);
                        dst[0] = (byte)val;
                        dst[1] = (byte)(val >> 8);
                        dst[2] = (byte)(val >> 16);
                        dst += 3;
                    }
                    return count * 3;

                case 16:
                    short* dst16 = (short*)dst;
                    for (int i = 0; i < count; i++)
                    {
                        float s = srcPtr[i];
                        if (s > 1f) s = 1f;
                        else if (s < -1f) s = -1f;
                        dst16[i] = (short)(s * 32767f);
                    }
                    return count * 2;

                case 32:
                    Buffer.MemoryCopy(srcPtr, dst, count * 4, count * 4);
                    return count * 4;

                case 8:
                    for (int i = 0; i < count; i++)
                    {
                        float s = srcPtr[i];
                        if (s > 1f) s = 1f;
                        else if (s < -1f) s = -1f;
                        dst[i] = (byte)((s + 1f) * 127.5f);
                    }
                    return count;
            }
        }
        return offset - startOffset;
    }

    /// <summary>
    /// Configura las coordenadas esféricas exactas de Cavern para cada layout de Dolby Atmos y SMPTE 22.2.
    /// En Cavern: X = Elevación (-45 = altura/techo, 0 = nivel de oído, +30 = piso), Y = Azimut (- = izquierda, + = derecha).
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
                // Orden SMPTE/Atmos: L, R, C, LFE, Ls (Side Ch 5), Rs (Side Ch 6), Lrs (Rear Ch 7), Rrs (Rear Ch 8)
                Listener.ReplaceChannels(new Channel[]
                {
                    new Channel(0, -30),       // Ch 1: L
                    new Channel(0, 30),        // Ch 2: R
                    new Channel(0, 0),         // Ch 3: C
                    new Channel(0, 0, true),   // Ch 4: LFE
                    new Channel(0, -90),       // Ch 5: Ls (Side Surround Left - Hidden Vocals / Bed)
                    new Channel(0, 90),        // Ch 6: Rs (Side Surround Right - Hidden Vocals / Bed)
                    new Channel(0, -150),      // Ch 7: Lrs (Rear Surround Left)
                    new Channel(0, 150)        // Ch 8: Rrs (Rear Surround Right)
                });
                return false;

            case "5.1.2":
                Listener.ReplaceChannels(new Channel[]
                {
                    new Channel(0, -30),       // Ch 1: L
                    new Channel(0, 30),        // Ch 2: R
                    new Channel(0, 0),         // Ch 3: C
                    new Channel(0, 0, true),   // Ch 4: LFE
                    new Channel(0, -90),       // Ch 5: Ls
                    new Channel(0, 90),        // Ch 6: Rs
                    new Channel(-45, -90),     // Ch 7: TSL / TML
                    new Channel(-45, 90)       // Ch 8: TSR / TMR
                });
                return false;

            case "5.1.4":
                Listener.ReplaceChannels(new Channel[]
                {
                    new Channel(0, -30),       // Ch 1: L
                    new Channel(0, 30),        // Ch 2: R
                    new Channel(0, 0),         // Ch 3: C
                    new Channel(0, 0, true),   // Ch 4: LFE
                    new Channel(0, -90),       // Ch 5: Ls
                    new Channel(0, 90),        // Ch 6: Rs
                    new Channel(-45, -30),     // Ch 7: TFL
                    new Channel(-45, 30),      // Ch 8: TFR
                    new Channel(-45, -150),    // Ch 9: TRL
                    new Channel(-45, 150)      // Ch 10: TRR
                });
                return false;

            case "7.1.2":
                Listener.ReplaceChannels(new Channel[]
                {
                    new Channel(0, -30),       // Ch 1: L
                    new Channel(0, 30),        // Ch 2: R
                    new Channel(0, 0),         // Ch 3: C
                    new Channel(0, 0, true),   // Ch 4: LFE
                    new Channel(0, -90),       // Ch 5: Ls
                    new Channel(0, 90),        // Ch 6: Rs
                    new Channel(0, -150),      // Ch 7: Lrs
                    new Channel(0, 150),       // Ch 8: Rrs
                    new Channel(-45, -90),     // Ch 9: TSL / TML
                    new Channel(-45, 90)       // Ch 10: TSR / TMR
                });
                return false;

            case "7.1.4":
                Listener.ReplaceChannels(new Channel[]
                {
                    new Channel(0, -30),       // Ch 1: L
                    new Channel(0, 30),        // Ch 2: R
                    new Channel(0, 0),         // Ch 3: C
                    new Channel(0, 0, true),   // Ch 4: LFE
                    new Channel(0, -90),       // Ch 5: Ls (Side Surround Left)
                    new Channel(0, 90),        // Ch 6: Rs (Side Surround Right)
                    new Channel(0, -150),      // Ch 7: Lrs (Rear Surround Left)
                    new Channel(0, 150),       // Ch 8: Rrs (Rear Surround Right)
                    new Channel(-45, -30),     // Ch 9: TFL
                    new Channel(-45, 30),      // Ch 10: TFR
                    new Channel(-45, -150),    // Ch 11: TRL
                    new Channel(-45, 150)      // Ch 12: TRR
                });
                return false;

            case "7.1.6":
                Listener.ReplaceChannels(new Channel[]
                {
                    new Channel(0, -30),       // Ch 1: L
                    new Channel(0, 30),        // Ch 2: R
                    new Channel(0, 0),         // Ch 3: C
                    new Channel(0, 0, true),   // Ch 4: LFE
                    new Channel(0, -90),       // Ch 5: Ls
                    new Channel(0, 90),        // Ch 6: Rs
                    new Channel(0, -150),      // Ch 7: Lrs
                    new Channel(0, 150),       // Ch 8: Rrs
                    new Channel(-45, -30),     // Ch 9: TFL
                    new Channel(-45, 30),      // Ch 10: TFR
                    new Channel(-45, -90),     // Ch 11: TML / TSL
                    new Channel(-45, 90),      // Ch 12: TMR / TSR
                    new Channel(-45, -150),    // Ch 13: TRL
                    new Channel(-45, 150)      // Ch 14: TRR
                });
                return false;

            case "9.1.6":
                // Layout 9.1.6 Dolby Atmos exacto (16 canales):
                // Canales 5 y 6 = Ls / Rs (Side Surrounds), Canales 7 y 8 = Lrs / Rrs (Rear Surrounds), Canales 9 y 10 = Lw / Rw (Wides)
                Listener.ReplaceChannels(new Channel[]
                {
                    new Channel(0, -30),       // Ch 1: L (Front Left)
                    new Channel(0, 30),        // Ch 2: R (Front Right)
                    new Channel(0, 0),         // Ch 3: C (Center)
                    new Channel(0, 0, true),   // Ch 4: LFE
                    new Channel(0, -90),       // Ch 5: Ls / SL (Side Surround Left)
                    new Channel(0, 90),        // Ch 6: Rs / SR (Side Surround Right)
                    new Channel(0, -150),      // Ch 7: Lrs / BL (Rear Surround Left)
                    new Channel(0, 150),       // Ch 8: Rrs / BR (Rear Surround Right)
                    new Channel(0, -60),       // Ch 9: Lw / WL (Wide Left)
                    new Channel(0, 60),        // Ch 10: Rw / WR (Wide Right)
                    new Channel(-45, -30),     // Ch 11: TFL (Top Front Left)
                    new Channel(-45, 30),      // Ch 12: TFR (Top Front Right)
                    new Channel(-45, -90),     // Ch 13: TML / TSL (Top Side Left)
                    new Channel(-45, 90),      // Ch 14: TMR / TSR (Top Side Right)
                    new Channel(-45, -150),    // Ch 15: TRL (Top Rear Left)
                    new Channel(-45, 150)      // Ch 16: TRR (Top Rear Right)
                });
                return false;

            case "22.2":
            default:
                // 22.2 Surround SMPTE 2036-2 / ITU-R BS.2051 System H (24 canales: 11/11.2)
                Listener.ReplaceChannels(24);
                return false;
        }
    }
}
