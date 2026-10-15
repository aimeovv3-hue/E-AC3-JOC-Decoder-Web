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
    private static int _updateRate = 64;
    private static long _totalSamplesPerChannel = 0;
    private static long _samplesRendered = 0;
    private static int _totalFrames = 0;
    private static string _lastLog = "";
    private static string _lastError = "";
    private static string _currentChannelNamesCsv = "";
    private static string _currentChannelAbbrCsv = "";
    private static byte[]? _reusableBatchBuffer;

    private readonly struct CavernSpeakerSpec
    {
        public readonly ReferenceChannel? RefChannel;
        public readonly float X;
        public readonly float Y;
        public readonly bool LFE;
        public readonly string ShortLabel;
        public readonly string FullName;

        public CavernSpeakerSpec(ReferenceChannel refChannel, string shortLabel, string? fullName = null)
        {
            RefChannel = refChannel;
            X = 0;
            Y = 0;
            LFE = false;
            ShortLabel = shortLabel;
            FullName = fullName ?? refChannel.ToString();
        }

        public CavernSpeakerSpec(float x, float y, bool lfe, string shortLabel, string fullName)
        {
            RefChannel = null;
            X = x;
            Y = y;
            LFE = lfe;
            ShortLabel = shortLabel;
            FullName = fullName;
        }
    }

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
    public static string GetChannelNames() => _currentChannelNamesCsv;

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
            // Intentamos abrir el contenedor nativamente primero (MP4Reader usa la tabla stsz/stco exacta sin cortar tramas)
            // y si no detecta objetos JOC o lanza excepción, usamos Mp4Ec3Extractor.
            bool openedWithJoc = false;
            try
            {
                _inputStream = new MemoryStream(inputBytes, writable: false);
                _reader = AudioReader.Open(_inputStream);
                _reader.ReadHeader();
                _renderer = _reader.GetRenderer();
                openedWithJoc = _renderer != null && _renderer.Objects.Count > 6;
            }
            catch
            {
                openedWithJoc = false;
            }

            if (!openedWithJoc)
            {
                byte[]? streamBytes = Mp4Ec3Extractor.ExtractIfMp4(inputBytes, fileName, false, (_, _) => { });
                if (streamBytes != null && streamBytes.Length > 0)
                {
                    try { _reader?.Dispose(); } catch { }
                    try { _inputStream?.Dispose(); } catch { }
                    _inputStream = new MemoryStream(streamBytes, writable: false);
                    _reader = AudioReader.Open(_inputStream);
                    _reader.ReadHeader();
                    _renderer = _reader.GetRenderer();
                }
            }

            if (_reader == null || _renderer == null)
                throw new InvalidOperationException("No se pudo inicializar el lector E-AC-3 JOC.");

            _sampleRate = targetSampleRate > 0 ? targetSampleRate : _reader.SampleRate;
            if (_sampleRate <= 0) _sampleRate = 48000;

            // IMPORTANTE PARA LA CALIDAD EN E-AC-3 JOC:
            // Cada timeslot QMF de JOC en Cavern es de 64 muestras (24 slots = 1536 muestras por trama E-AC-3).
            // Usar 64 muestras evita saltos de ganancia ("zipper noise" a 187.5 Hz) al mover objetos dinámicos.
            _updateRate = (updateRateSamples > 0 && updateRateSamples <= 256) ? updateRateSamples : 64;
            _pcmBitDepth = pcmBitDepth;

            float linearVolume = (float)Math.Pow(10.0, Math.Clamp(gainDb, -40.0, 20.0) / 20.0);

            QualityModes quality = audioQualityIndex switch
            {
                0 => QualityModes.Low,
                1 => QualityModes.Medium,
                2 => QualityModes.High,
                3 => QualityModes.Perfect,
                _ => QualityModes.Perfect
            };

            // ORDEN CRÍTICO EN CAVERN:
            // 1. Instanciar Listener vacío (su constructor reinicia Listener.Channels a 2 canales).
            // 2. Reemplazar Listener.Channels con la matriz elegida (ConfigureCavernizeLayout).
            // 3. Asignar SampleRate, UpdateRate, AudioQuality y LFESeparation DESPUÉS de ReplaceChannels
            //    para que los filtros internos y buffers por canal de Listener se dimensionen para todos los canales.
            _listener = new Listener();

            bool binaural = ConfigureCavernizeLayout(targetLayout);
            Listener.HeadphoneVirtualizer = binaural;
            _outputChannels = Listener.Channels.Length;

            _listener.SampleRate = _sampleRate;
            _listener.UpdateRate = _updateRate;
            _listener.Volume = linearVolume;
            _listener.AudioQuality = quality;
            _listener.LFESeparation = lfeSeparation;

            // 4. Vincular todos los objetos del Renderer (Bed + JOC Dynamic Objects)
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

            _lastLog = $"[Cavernize] '{fileName}' | SR: {_reader.SampleRate} Hz | JOC Objects: {_renderer.Objects.Count} | Quality: {quality} | Layout: {targetLayout} ({_outputChannels} ch: {_currentChannelAbbrCsv}) | UpdateRate: {_updateRate} | Frames: {_totalFrames}";
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
                int floatCount = Math.Min(renderedBlock.Length, samplesInBlock * _outputChannels);

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
                        int val = (int)MathF.Round(s * 8388607f);
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
                        dst16[i] = (short)MathF.Round(s * 32767f);
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
                        dst[i] = (byte)MathF.Round((s + 1f) * 127.5f);
                    }
                    return count;
            }
        }
        return offset - startOffset;
    }

    private static CavernSpeakerSpec Ref(ReferenceChannel rc, string shortLabel, string? fullName = null)
        => new CavernSpeakerSpec(rc, shortLabel, fullName);

    private static CavernSpeakerSpec TopCenterSpec => new CavernSpeakerSpec(-90f, 0f, false, "TC", "TopCenter");
    private static CavernSpeakerSpec BottomFrontLeftSpec => new CavernSpeakerSpec(45f, -45f, false, "BFL", "BottomFrontLeft");
    private static CavernSpeakerSpec BottomFrontRightSpec => new CavernSpeakerSpec(45f, 45f, false, "BFR", "BottomFrontRight");
    private static CavernSpeakerSpec BottomFrontCenterSpec => new CavernSpeakerSpec(45f, 0f, false, "BFC", "BottomFrontCenter");

    private static void ApplyCavernSpeakerLayout(CavernSpeakerSpec[] specs, bool swapSurrounds)
    {
        Channel[] channels = new Channel[specs.Length];
        string[] fullNames = new string[specs.Length];
        string[] shortLabels = new string[specs.Length];

        for (int i = 0; i < specs.Length; i++)
        {
            CavernSpeakerSpec spec = specs[i];
            if (spec.RefChannel.HasValue)
            {
                ReferenceChannel rc = spec.RefChannel.Value;
                string label = spec.ShortLabel;
                string name = spec.FullName;

                if (swapSurrounds)
                {
                    if (rc == ReferenceChannel.RearLeft)
                    {
                        rc = ReferenceChannel.SideLeft;
                        label = "SL";
                        name = "SideLeft";
                    }
                    else if (rc == ReferenceChannel.RearRight)
                    {
                        rc = ReferenceChannel.SideRight;
                        label = "SR";
                        name = "SideRight";
                    }
                    else if (rc == ReferenceChannel.SideLeft)
                    {
                        rc = ReferenceChannel.RearLeft;
                        label = "RL";
                        name = "RearLeft";
                    }
                    else if (rc == ReferenceChannel.SideRight)
                    {
                        rc = ReferenceChannel.RearRight;
                        label = "RR";
                        name = "RearRight";
                    }
                }

                channels[i] = ChannelPrototype.ToLayout(new[] { rc })[0];
                fullNames[i] = name;
                shortLabels[i] = label;
            }
            else
            {
                channels[i] = new Channel(spec.X, spec.Y, spec.LFE);
                fullNames[i] = spec.FullName;
                shortLabels[i] = spec.ShortLabel;
            }
        }

        Listener.ReplaceChannels(channels);
        _currentChannelNamesCsv = string.Join(",", fullNames);
        _currentChannelAbbrCsv = string.Join(", ", shortLabels);
    }

    private static bool ConfigureCavernizeLayout(string layoutKey)
    {
        string raw = (layoutKey ?? "9.1.6").Trim();
        bool swapSurrounds = false;

        if (raw.EndsWith("|ss", StringComparison.OrdinalIgnoreCase))
        {
            swapSurrounds = true;
            raw = raw.Substring(0, raw.Length - 3).Trim();
        }

        string key = raw.ToLowerInvariant();

        switch (key)
        {
            case "virtualizer":
            case "headphone virtualizer":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR")
                }, false);
                return true;

            case "1.0":
            case "mono":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontCenter, "FC")
                }, false);
                return false;

            case "2.0":
            case "stereo":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR")
                }, false);
                return false;

            case "4.0":
            case "quad":
            case "quadraphonic":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR")
                }, swapSurrounds);
                return false;

            case "4.1.1":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR"),
                    TopCenterSpec
                }, swapSurrounds);
                return false;

            case "4.1.3":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR"),
                    Ref(ReferenceChannel.TopFrontLeft, "TFL"),
                    Ref(ReferenceChannel.TopFrontRight, "TFR"),
                    TopCenterSpec
                }, swapSurrounds);
                return false;

            case "5.1":
            case "5.1 side":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR")
                }, swapSurrounds);
                return false;

            case "5.1 rear":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR")
                }, swapSurrounds);
                return false;

            case "6.2":
            case "6.2 surround":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.RearCenter, "RC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE2", "Subwoofer"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR")
                }, false);
                return false;

            case "7.1":
            case "7.1 surround":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR")
                }, swapSurrounds);
                return false;

            case "5.1.2":
            case "5.1.2 side":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR"),
                    Ref(ReferenceChannel.TopSideLeft, "TSL"),
                    Ref(ReferenceChannel.TopSideRight, "TSR")
                }, swapSurrounds);
                return false;

            case "5.1.2 front":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR"),
                    Ref(ReferenceChannel.TopFrontLeft, "TFL"),
                    Ref(ReferenceChannel.TopFrontRight, "TFR")
                }, swapSurrounds);
                return false;

            case "5.1.4":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR"),
                    Ref(ReferenceChannel.TopFrontLeft, "TFL"),
                    Ref(ReferenceChannel.TopFrontRight, "TFR"),
                    Ref(ReferenceChannel.TopRearLeft, "TRL"),
                    Ref(ReferenceChannel.TopRearRight, "TRR")
                }, swapSurrounds);
                return false;

            case "5.1.6":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR"),
                    Ref(ReferenceChannel.TopFrontLeft, "TFL"),
                    Ref(ReferenceChannel.TopFrontRight, "TFR"),
                    Ref(ReferenceChannel.TopSideLeft, "TSL"),
                    Ref(ReferenceChannel.TopSideRight, "TSR"),
                    Ref(ReferenceChannel.TopRearLeft, "TRL"),
                    Ref(ReferenceChannel.TopRearRight, "TRR")
                }, swapSurrounds);
                return false;

            case "7.1.2":
            case "7.1.2 side":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR"),
                    Ref(ReferenceChannel.TopSideLeft, "TSL"),
                    Ref(ReferenceChannel.TopSideRight, "TSR")
                }, swapSurrounds);
                return false;

            case "7.1.2 front":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR"),
                    Ref(ReferenceChannel.TopFrontLeft, "TFL"),
                    Ref(ReferenceChannel.TopFrontRight, "TFR")
                }, swapSurrounds);
                return false;

            case "7.1.4":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR"),
                    Ref(ReferenceChannel.TopFrontLeft, "TFL"),
                    Ref(ReferenceChannel.TopFrontRight, "TFR"),
                    Ref(ReferenceChannel.TopRearLeft, "TRL"),
                    Ref(ReferenceChannel.TopRearRight, "TRR")
                }, swapSurrounds);
                return false;

            case "7.1.6":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR"),
                    Ref(ReferenceChannel.TopFrontLeft, "TFL"),
                    Ref(ReferenceChannel.TopFrontRight, "TFR"),
                    Ref(ReferenceChannel.TopSideLeft, "TSL"),
                    Ref(ReferenceChannel.TopSideRight, "TSR"),
                    Ref(ReferenceChannel.TopRearLeft, "TRL"),
                    Ref(ReferenceChannel.TopRearRight, "TRR")
                }, swapSurrounds);
                return false;

            case "9.1.2":
            case "9.1.2 side":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR"),
                    Ref(ReferenceChannel.WideLeft, "WL"),
                    Ref(ReferenceChannel.WideRight, "WR"),
                    Ref(ReferenceChannel.TopSideLeft, "TSL"),
                    Ref(ReferenceChannel.TopSideRight, "TSR")
                }, swapSurrounds);
                return false;

            case "9.1.2 front":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR"),
                    Ref(ReferenceChannel.WideLeft, "WL"),
                    Ref(ReferenceChannel.WideRight, "WR"),
                    Ref(ReferenceChannel.TopFrontLeft, "TFL"),
                    Ref(ReferenceChannel.TopFrontRight, "TFR")
                }, swapSurrounds);
                return false;

            case "9.1.4":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR"),
                    Ref(ReferenceChannel.WideLeft, "WL"),
                    Ref(ReferenceChannel.WideRight, "WR"),
                    Ref(ReferenceChannel.TopFrontLeft, "TFL"),
                    Ref(ReferenceChannel.TopFrontRight, "TFR"),
                    Ref(ReferenceChannel.TopRearLeft, "TRL"),
                    Ref(ReferenceChannel.TopRearRight, "TRR")
                }, swapSurrounds);
                return false;

            case "small cinema":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR"),
                    Ref(ReferenceChannel.WideLeft, "WL"),
                    Ref(ReferenceChannel.WideRight, "WR"),
                    Ref(ReferenceChannel.FrontLeftCenter, "FLC"),
                    Ref(ReferenceChannel.FrontRightCenter, "FRC")
                }, false);
                return false;

            case "22.2":
            case "22.2 surround":
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR"),
                    Ref(ReferenceChannel.FrontLeftCenter, "FLC"),
                    Ref(ReferenceChannel.FrontRightCenter, "FRC"),
                    Ref(ReferenceChannel.RearCenter, "RC"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR"),
                    Ref(ReferenceChannel.TopFrontLeft, "TFL"),
                    Ref(ReferenceChannel.TopFrontRight, "TFR"),
                    Ref(ReferenceChannel.TopFrontCenter, "TFC"),
                    TopCenterSpec,
                    Ref(ReferenceChannel.TopRearLeft, "TRL"),
                    Ref(ReferenceChannel.TopRearRight, "TRR"),
                    Ref(ReferenceChannel.TopSideLeft, "TSL"),
                    Ref(ReferenceChannel.TopSideRight, "TSR"),
                    Ref(ReferenceChannel.TopRearCenter, "TRC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE2", "Subwoofer"),
                    BottomFrontLeftSpec,
                    BottomFrontRightSpec,
                    BottomFrontCenterSpec
                }, false);
                return false;

            case "9.1.6":
            default:
                ApplyCavernSpeakerLayout(new[] {
                    Ref(ReferenceChannel.FrontLeft, "FL"),
                    Ref(ReferenceChannel.FrontRight, "FR"),
                    Ref(ReferenceChannel.FrontCenter, "FC"),
                    Ref(ReferenceChannel.ScreenLFE, "LFE"),
                    Ref(ReferenceChannel.RearLeft, "RL"),
                    Ref(ReferenceChannel.RearRight, "RR"),
                    Ref(ReferenceChannel.SideLeft, "SL"),
                    Ref(ReferenceChannel.SideRight, "SR"),
                    Ref(ReferenceChannel.WideLeft, "WL"),
                    Ref(ReferenceChannel.WideRight, "WR"),
                    Ref(ReferenceChannel.TopFrontLeft, "TFL"),
                    Ref(ReferenceChannel.TopFrontRight, "TFR"),
                    Ref(ReferenceChannel.TopSideLeft, "TSL"),
                    Ref(ReferenceChannel.TopSideRight, "TSR"),
                    Ref(ReferenceChannel.TopRearLeft, "TRL"),
                    Ref(ReferenceChannel.TopRearRight, "TRR")
                }, swapSurrounds);
                return false;
        }
    }
}
