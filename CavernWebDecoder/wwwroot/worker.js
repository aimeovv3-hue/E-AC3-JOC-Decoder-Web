import { dotnet } from './_framework/dotnet.js';

let exports = null;
let lastReportedFrames = 0;

async function ensureDotnetInitialized() {
  if (exports) return exports;

  const { setModuleImports, getAssemblyExports, getConfig } = await dotnet
    .withDiagnosticTracing(false)
    .create();

  setModuleImports('worker.js', {
    workerCallbacks: {
      onProgress: (framesProcessed, totalFrames, fps) => {
        lastReportedFrames = framesProcessed;
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

self.onmessage = async (e) => {
  const msg = e.data;
  if (msg.type === 'DECODE') {
    try {
      const wasmExports = await ensureDotnetInitialized();
      const inputBytes = new Uint8Array(msg.buffer);
      const opt = msg.options;
      lastReportedFrames = 0;

      const wavBytes = wasmExports.Program.DecodeEac3Joc(
        inputBytes,
        msg.fileName,
        opt.cicpLayout,
        opt.pcmBitDepth,
        opt.targetSampleRate,
        opt.targetLoudnessDb,
        opt.drcEffectType,
        opt.printStderr
      );

      if (!wavBytes) {
        self.postMessage({ type: 'ERROR', error: 'Decoding cancelled.' });
        return;
      }

      self.postMessage(
        {
          type: 'DONE',
          wavBuffer: wavBytes.buffer,
          framesProcessed: lastReportedFrames
        },
        [wavBytes.buffer]
      );
    } catch (err) {
      self.postMessage({
        type: 'ERROR',
        error: err && err.message ? err.message : String(err)
      });
    }
  }
};
