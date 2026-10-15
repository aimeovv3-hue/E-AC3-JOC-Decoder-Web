let exports = null;

async function ensureDotnetInitialized() {
  if (exports) return exports;

  self.postMessage({ type: 'STATUS', text: 'Loading WASM...' });

  const dotnetUrl = new URL('./_framework/dotnet.js', import.meta.url).href;
  const { dotnet } = await import(dotnetUrl);

  const { getAssemblyExports, getConfig } = await dotnet
    .withDiagnosticTracing(false)
    .create();

  const config = getConfig();
  exports = await getAssemblyExports(config.mainAssemblyName);
  return exports;
}

function createWavHeader(channels, sampleRate, bitDepth, dataByteLength) {
  const bytesPerSample = bitDepth / 8;
  const blockAlign = channels * bytesPerSample;
  const byteRate = sampleRate * blockAlign;
  const isFloat = bitDepth === 32;
  const formatTag = isFloat ? 3 : 1;

  const header = new ArrayBuffer(44);
  const view = new DataView(header);

  view.setUint32(0, 0x52494646, false); // "RIFF"
  const riffSize = dataByteLength + 36 > 0xFFFFFFFF ? 0xFFFFFFFF : dataByteLength + 36;
  view.setUint32(4, riffSize, true);
  view.setUint32(8, 0x57415645, false); // "WAVE"
  view.setUint32(12, 0x666d7420, false); // "fmt "
  view.setUint32(16, 16, true);
  view.setUint16(20, formatTag, true);
  view.setUint16(22, channels, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, byteRate, true);
  view.setUint16(32, blockAlign, true);
  view.setUint16(34, bitDepth, true);
  view.setUint32(36, 0x64617461, false); // "data"
  const dataSize = dataByteLength > 0xFFFFFFFF ? 0xFFFFFFFF : dataByteLength;
  view.setUint32(40, dataSize, true);

  return new Uint8Array(header);
}

self.onmessage = async (e) => {
  const msg = e.data;
  if (msg.type === 'DECODE') {
    let pcmChunks = [];
    let totalDataBytes = 0;

    try {
      const wasmExports = await ensureDotnetInitialized();
      const prog = wasmExports.Program;

      self.postMessage({ type: 'STATUS', text: 'Opening stream...' });

      const inputBytes = new Uint8Array(msg.buffer);
      const opt = msg.options;

      const totalFrames = prog.InitSession(
        inputBytes,
        msg.fileName,
        opt.targetLayout,
        opt.pcmBitDepth,
        opt.targetSampleRate,
        opt.updateRateSamples,
        opt.audioQualityIndex,
        opt.gainDb,
        opt.lfeSeparation
      );

      if (totalFrames <= 0) {
        const errText = prog.GetLastError() || 'Failed to initialize Cavernize session.';
        throw new Error(errText);
      }

      if (opt.printLogs) {
        const initLog = prog.GetLastLog();
        if (initLog) self.postMessage({ type: 'LOG', message: initLog, isError: false });
      }

      const outChannels = prog.GetOutputChannels();
      const outSampleRate = prog.GetSampleRate();
      const channelLabels = typeof prog.GetChannelLabels === 'function' ? prog.GetChannelLabels() : '';
      const channelNames = typeof prog.GetChannelNames === 'function' ? prog.GetChannelNames() : '';
      const startTime = performance.now();

      self.postMessage({
        type: 'PROGRESS',
        framesProcessed: 0,
        totalFrames,
        fps: 0.0
      });

      const batchSizeFrames = 32;

      while (true) {
        const chunk = prog.RenderNextBatch(batchSizeFrames);
        if (!chunk || chunk.byteLength === 0) {
          break;
        }

        const copy = new Uint8Array(chunk);
        pcmChunks.push(copy);
        totalDataBytes += copy.byteLength;

        const framesDone = prog.GetFramesProcessed();
        const elapsedSec = Math.max(0.001, (performance.now() - startTime) / 1000);
        const fps = framesDone / elapsedSec;

        self.postMessage({
          type: 'PROGRESS',
          framesProcessed: framesDone,
          totalFrames,
          fps
        });
      }

      const finalErr = prog.GetLastError();
      const finalFrames = prog.GetFramesProcessed();
      prog.CloseSession();

      if (totalDataBytes === 0 && finalErr) {
        throw new Error(finalErr);
      }

      const wavHeader = createWavHeader(
        outChannels,
        outSampleRate,
        opt.pcmBitDepth,
        totalDataBytes
      );

      const wavBlob = new Blob([wavHeader, ...pcmChunks], { type: 'audio/wav' });
      pcmChunks = [];

      self.postMessage({
        type: 'DONE',
        wavBlob,
        outChannels,
        channelLabels,
        channelNames,
        framesProcessed: finalFrames > 0 ? finalFrames : totalFrames
      });
    } catch (err) {
      pcmChunks = [];
      const errMsg = err && err.message ? err.message : String(err);
      self.postMessage({ type: 'LOG', message: `[Cavernize Error] ${errMsg}`, isError: true });
      self.postMessage({ type: 'ERROR', error: errMsg });
    }
  }
};
