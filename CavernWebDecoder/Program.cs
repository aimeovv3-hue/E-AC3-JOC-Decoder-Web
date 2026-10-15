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

    private static int _outputChannels = 16;
    private static int _sampleRate = 48000;
    private static int _pcmBitDepth = 24;
    private static int _updateRate = 256;
    private static long _totalSamplesPerChannel = 0;
    private static long _samplesRendered = 0;
    private static int _totalFrames = 0;
    private static string _lastLog = "";
    private static string _lastError = "";
    private static string _currentChannelNamesCsv = "";
    private static string _currentChannelAbbrCsv = "";
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

    /// <summary>
    /// Devuelve los nombres completos de los canales del layout activo separados por coma (ej. "FrontLeft,FrontRight,...")
    /// </summary>
    [JSExport]
    public static string GetChannelNames() => _currentChannelNamesCsv;

    /// <summary>
    /// Devuelve las etiquetas estándar CICP/Atmos separadas por coma (ej. "L,R,C,LFE,Ls,Rs,Lrs,Rrs,Lw,Rw,TFL,TFR,TSL,TSR,TRL,TRR")
    /// </summary>
    [JSExport]
    public static string GetChannelLabels() => _currentChannelAbbrCsv;

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

            // 1. Configurar primero los canales del Listener usando los prototipos nativos de Cavern
            bool binaural = ConfigureCavernizeLayout(targetLayout);
            Listener.HeadphoneVirtualizer = binaural;
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

            // 2. Instanciar el Listener después de que Listener.Channels ya tiene la matriz espacial correcta
            _listener = new Listener
            {
                SampleRate = _sampleRate,
                UpdateRate = _updateRate,
                Volume = linearVolume,
                AudioQuality = quality,
                LFESeparation = lfeSeparation
            };

            // 3. Vincular todos los objetos del Renderer (Bed + JOC Dynamic Objects)
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

            _lastLog = $"[Cavernize] '{fileName}' | SR: {_reader.SampleRate} Hz | JOC Objects: {_renderer.Objects.Count} | Layout: {targetLayout} ({_outputChannels} ch: {_currentChannelAbbrCsv}) | UpdateRate: {_updateRate} | Frames: {_totalFrames}";
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
    /// Convierte un arreglo de ReferenceChannel en objetos Channel con las coordenadas esféricas exactas
    /// de ChannelPrototype.Mapping de Cavern, y guarda las etiquetas para exportarlas al frontend.
    /// </summary>
    private static void ApplyReferenceLayout(ReferenceChannel[] refChannels, string[] abbrLabels)
    {
        Channel[] channels = ChannelPrototype.ToLayout(refChannels);
        Listener.ReplaceChannels(channels);

        string[] fullNames = new string[refChannels.Length];
        for (int i = 0; i < refChannels.Length; i++)
        {
            fullNames[i] = refChannels[i].ToString();
        }

        _currentChannelNamesCsv = string.Join(",", fullNames);
        _currentChannelAbbrCsv = string.Join(",", abbrLabels);
    }

    /// <summary>
    /// Configura las matrices de canales exactas de Cavern usando ReferenceChannel oficial para cada layout
    /// de Dolby Atmos y CICP. Así el renderizador JOC mapea correctamente tanto la cama (Bed) como los objetos 3D.
    /// </summary>
    private static bool ConfigureCavernizeLayout(string layoutKey)
    {
        string normalized = (layoutKey ?? "9.1.6").Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "virtualizer":
            case "binaural":
                ApplyReferenceLayout(
                    new[] { ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight },
                    new[] { "L", "R" }
                );
                return true;

            case "1.0":
            case "mono":
            case "cicp 1":
                ApplyReferenceLayout(
                    new[] { ReferenceChannel.FrontCenter },
                    new[] { "C" }
                );
                return false;

            case "2.0":
            case "stereo":
            case "cicp 2":
                ApplyReferenceLayout(
                    new[] { ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight },
                    new[] { "L", "R" }
                );
                return false;

            case "4.0":
            case "quad":
                ApplyReferenceLayout(
                    new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight,
                        ReferenceChannel.RearLeft, ReferenceChannel.RearRight
                    },
                    new[] { "L", "R", "Ls", "Rs" }
                );
                return false;

            case "5.1":
            case "cicp 6":
                // En Cavern, el estándar 5.1 usa RearLeft/RearRight (±110°) o SideLeft/SideRight
                // Usamos el matriz estándar de 6 canales de Cavern para recibir el Bed completo
                ApplyReferenceLayout(
                    new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight,
                        ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
                        ReferenceChannel.SideLeft, ReferenceChannel.SideRight
                    },
                    new[] { "L", "R", "C", "LFE", "Ls", "Rs" }
                );
                return false;

            case "7.1":
            case "cicp 12":
                // Orden SMPTE/Atmos: L, R, C, LFE, Ls (Side), Rs (Side), Lrs (Rear), Rrs (Rear)
                ApplyReferenceLayout(
                    new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight,
                        ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
                        ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                        ReferenceChannel.RearLeft, ReferenceChannel.RearRight
                    },
                    new[] { "L", "R", "C", "LFE", "Ls", "Rs", "Lrs", "Rrs" }
                );
                return false;

            case "5.1.2":
            case "cicp 14":
                ApplyReferenceLayout(
                    new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight,
                        ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
                        ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                        ReferenceChannel.TopSideLeft, ReferenceChannel.TopSideRight
                    },
                    new[] { "L", "R", "C", "LFE", "Ls", "Rs", "TSL", "TSR" }
                );
                return false;

            case "5.1.4":
            case "cicp 16":
                ApplyReferenceLayout(
                    new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight,
                        ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
                        ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                        ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
                        ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
                    },
                    new[] { "L", "R", "C", "LFE", "Ls", "Rs", "TFL", "TFR", "TRL", "TRR" }
                );
                return false;

            case "7.1.2":
                ApplyReferenceLayout(
                    new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight,
                        ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
                        ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                        ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
                        ReferenceChannel.TopSideLeft, ReferenceChannel.TopSideRight
                    },
                    new[] { "L", "R", "C", "LFE", "Ls", "Rs", "Lrs", "Rrs", "TSL", "TSR" }
                );
                return false;

            case "7.1.4":
            case "cicp 19":
                ApplyReferenceLayout(
                    new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight,
                        ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
                        ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                        ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
                        ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
                        ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
                    },
                    new[] { "L", "R", "C", "LFE", "Ls", "Rs", "Lrs", "Rrs", "TFL", "TFR", "TRL", "TRR" }
                );
                return false;

            case "7.1.6":
                ApplyReferenceLayout(
                    new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight,
                        ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
                        ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                        ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
                        ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
                        ReferenceChannel.TopSideLeft, ReferenceChannel.TopSideRight,
                        ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
                    },
                    new[] { "L", "R", "C", "LFE", "Ls", "Rs", "Lrs", "Rrs", "TFL", "TFR", "TSL", "TSR", "TRL", "TRR" }
                );
                return false;

            case "9.1.4":
                ApplyReferenceLayout(
                    new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight,
                        ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
                        ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                        ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
                        ReferenceChannel.WideLeft, ReferenceChannel.WideRight,
                        ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
                        ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
                    },
                    new[] { "L", "R", "C", "LFE", "Ls", "Rs", "Lrs", "Rrs", "Lw", "Rw", "TFL", "TFR", "TRL", "TRR" }
                );
                return false;

            case "22.2":
            case "cicp 13":
                // SMPTE 2036-2 / ITU-R BS.2051 System H (24 canales: 11/11.2)
                // NOTA: En pistas Dolby Atmos (E-AC-3 JOC), los canales de piso (Bottom) y LFE2 suelen venir vacíos
                // porque Atmos codifica camas y objetos desde el nivel del oído (0°) hacia el techo (-45° / -90°).
                ApplyReferenceLayout(
                    new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight, ReferenceChannel.FrontCenter,
                        ReferenceChannel.ScreenLFE, ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
                        ReferenceChannel.FrontLeftCenter, ReferenceChannel.FrontRightCenter, ReferenceChannel.RearCenter,
                        ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                        ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight, ReferenceChannel.TopFrontCenter,
                        ReferenceChannel.TopCenter, ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight,
                        ReferenceChannel.TopSideLeft, ReferenceChannel.TopSideRight, ReferenceChannel.TopRearCenter,
                        ReferenceChannel.Subwoofer, ReferenceChannel.BottomFrontLeft,
                        ReferenceChannel.BottomFrontRight, ReferenceChannel.BottomFrontCenter
                    },
                    new[] {
                        "FL", "FR", "FC", "LFE1", "BL", "BR", "FLc", "FRc", "BC", "SiL", "SiR",
                        "TpFL", "TpFR", "TpFC", "TpC", "TpBL", "TpBR", "TpSiL", "TpSiR", "TpBC",
                        "LFE2", "BtFL", "BtFR", "BtFC"
                    }
                );
                return false;

            case "9.1.6":
            default:
                // Layout 9.1.6 Dolby Atmos por defecto (16 canales) usando los prototipos nativos de Cavern:
                // Así evitamos que un nombre no reconocido caiga en 22.2 (donde los canales de piso salen mudos).
                ApplyReferenceLayout(
                    new[] {
                        ReferenceChannel.FrontLeft, ReferenceChannel.FrontRight,
                        ReferenceChannel.FrontCenter, ReferenceChannel.ScreenLFE,
                        ReferenceChannel.SideLeft, ReferenceChannel.SideRight,
                        ReferenceChannel.RearLeft, ReferenceChannel.RearRight,
                        ReferenceChannel.WideLeft, ReferenceChannel.WideRight,
                        ReferenceChannel.TopFrontLeft, ReferenceChannel.TopFrontRight,
                        ReferenceChannel.TopSideLeft, ReferenceChannel.TopSideRight,
                        ReferenceChannel.TopRearLeft, ReferenceChannel.TopRearRight
                    },
                    new[] { "L", "R", "C", "LFE", "Ls", "Rs", "Lrs", "Rrs", "Lw", "Rw", "TFL", "TFR", "TSL", "TSR", "TRL", "TRR" }
                );
                return false;
        }
    }
}
