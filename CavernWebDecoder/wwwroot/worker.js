let exports = null;
let pcmChunks = [];
let totalDataBytes = 0;
let streamMeta = { channels: 24, sampleRate: 48000, bitDepth: 24, totalFrames: 0 };

async function ensureDotnetInitialized() {
  if (exports) return exports;

  const { dotnet } = await import('./_framework/dotnet.js');

  const { setModuleImports, getAssemblyExports, getConfig } = await dotnet
    .withDiagnosticTracing(false)
    .create();

  setModuleImports('worker.js', {
    workerCallbacks: {
      onInit: (outputChannels, sampleRate, bitDepth, totalFrames) => {
        streamMeta = { channels: outputChannels, sampleRate, bitDepth, totalFrames };
        pcmChunks = [];
        totalDataBytes = 0;
      },
      onPcmChunk: (chunkBytes) => {
        const copy = new Uint8Array(chunkBytes);
        pcmChunks.push(copy);
        totalDataBytes += copy.byteLength;
      },
      onProgress: (framesProcessed, totalFrames, fps) => {
        self.postMessage({
          type: 'PROGRESS',
          framesProcessed,
          totalFrames,
          fps
        });
      },
      onLog: (message, isError) => {
        self.postMessage({
          type: 'LOG',
          message,
          isError
        });
      }
    }
  });

  const config = getConfig();
  exports = await getAssemblyExports(config.mainAssemblyName);
  return exports;
}

/**
 * Creates a standard WAVE_FORMAT_EXTENSIBLE header (or RF64 header if > 4GB) for multichannel PCM/Float audio.
 */
function createWavHeader(channels, sampleRate, bitDepth, dataByteLength) {
  const bytesPerSample = bitDepth / 8;
  const blockAlign = channels * bytesPerSample;
  const byteRate = sampleRate * blockAlign;
  const isFloat = bitDepth === 32;
  const formatTag = isFloat ? 3 : 1; // 1 = PCM, 3 = IEEE Float

  const header = new ArrayBuffer(44);
  const view = new DataView(header);

  // "RIFF"
  view.setUint32(0, 0x52494646, false);
  // File size - 8 (clamped to 0xFFFFFFFF if > 4GB)
  const riffSize = dataByteLength + 36 > 0xFFFFFFFF ? 0xFFFFFFFF : dataByteLength + 36;
  view.setUint32(4, riffSize, true);
  // "WAVE"
  view.setUint32(8, 0x57415645, false);
  // "fmt "
  view.setUint32(12, 0x666d7420, false);
  view.setUint32(16, 16, true); // fmt chunk size
  view.setUint16(20, formatTag, true);
  view.setUint16(22, channels, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, byteRate, true);
  view.setUint16(32, blockAlign, true);
  view.setUint16(34, bitDepth, true);
  // "data"
  view.setUint32(36, 0x64617461, false);
  const dataSize = dataByteLength > 0xFFFFFFFF ? 0xFFFFFFFF : dataByteLength;
  view.setUint32(40, dataSize, true);

  return new Uint8Array(header);
}

self.onmessage = async (e) => {
  const msg = e.data;
  if (msg.type === 'DECODE') {
    try {
      const wasmExports = await ensureDotnetInitialized();
      const inputBytes = new Uint8Array(msg.buffer);
      const opt = msg.options;

      const finalFrames = wasmExports.Program.DecodeWithCavernize(
        inputBytes,
        msg.fileName,
        opt.targetLayout,
        opt.pcmBitDepth,
        opt.targetSampleRate,
        opt.updateRateSamples,
        opt.audioQualityIndex,
        opt.gainDb,
        opt.lfeSeparation,
        opt.printLogs
      );

      if (finalFrames < 0) {
        self.postMessage({ type: 'ERROR', error: 'Decoding cancelled.' });
        return;
      }

      const wavHeader = createWavHeader(
        streamMeta.channels,
        streamMeta.sampleRate,
        streamMeta.bitDepth,
        totalDataBytes
      );

      const wavBlob = new Blob([wavHeader, ...pcmChunks], { type: 'audio/wav' });
      pcmChunks = [];
      totalDataBytes = 0;

      self.postMessage({
        type: 'DONE',
        wavBlob,
        framesProcessed: finalFrames
      });
    } catch (err) {
      pcmChunks = [];
      totalDataBytes = 0;
      self.postMessage({
        type: 'ERROR',
        error: err && err.message ? err.message : String(err)
      });
    }
  }
};
