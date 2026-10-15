const CAVERN_LAYOUTS = [
  {
    id: '9.1.6',
    title: '9.1.6 • 9/6.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'RL', 'RR', 'SL', 'SR', 'WL', 'WR', 'TFL', 'TFR', 'TSL', 'TSR', 'TRL', 'TRR']
  },
  {
    id: '9.1.4',
    title: '9.1.4 • 9/4.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'RL', 'RR', 'SL', 'SR', 'WL', 'WR', 'TFL', 'TFR', 'TRL', 'TRR']
  },
  {
    id: '9.1.2 side',
    title: '9.1.2 side • 9/2.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'RL', 'RR', 'SL', 'SR', 'WL', 'WR', 'TSL', 'TSR']
  },
  {
    id: '9.1.2 front',
    title: '9.1.2 front • 9/2.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'RL', 'RR', 'SL', 'SR', 'WL', 'WR', 'TFL', 'TFR']
  },
  {
    id: '7.1.6',
    title: '7.1.6 • 7/6.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'RL', 'RR', 'SL', 'SR', 'TFL', 'TFR', 'TSL', 'TSR', 'TRL', 'TRR']
  },
  {
    id: '7.1.4',
    title: '7.1.4 • 7/4.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'RL', 'RR', 'SL', 'SR', 'TFL', 'TFR', 'TRL', 'TRR']
  },
  {
    id: '7.1.2 side',
    title: '7.1.2 side • 7/2.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'RL', 'RR', 'SL', 'SR', 'TSL', 'TSR']
  },
  {
    id: '7.1.2 front',
    title: '7.1.2 front • 7/2.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'RL', 'RR', 'SL', 'SR', 'TFL', 'TFR']
  },
  {
    id: '5.1.6',
    title: '5.1.6 • 5/6.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'SL', 'SR', 'TFL', 'TFR', 'TSL', 'TSR', 'TRL', 'TRR']
  },
  {
    id: '5.1.4',
    title: '5.1.4 • 5/4.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'SL', 'SR', 'TFL', 'TFR', 'TRL', 'TRR']
  },
  {
    id: '5.1.2 side',
    title: '5.1.2 side • 5/2.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'SL', 'SR', 'TSL', 'TSR']
  },
  {
    id: '5.1.2 front',
    title: '5.1.2 front • 5/2.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'SL', 'SR', 'TFL', 'TFR']
  },
  {
    id: '7.1',
    title: '7.1 Surround • 7/0.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'RL', 'RR', 'SL', 'SR']
  },
  {
    id: '6.2 Surround',
    title: '6.2 Surround • 6/0.2',
    channels: ['FL', 'FR', 'FC', 'LFE', 'RC', 'LFE2', 'SL', 'SR']
  },
  {
    id: '5.1 side',
    title: '5.1 side • 5/0.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'SL', 'SR']
  },
  {
    id: '5.1 rear',
    title: '5.1 rear • 5/0.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'RL', 'RR']
  },
  {
    id: '4.1.3',
    title: '4.1.3 • 4/3.1',
    channels: ['FL', 'FR', 'LFE', 'RL', 'RR', 'TFL', 'TFR', 'TC']
  },
  {
    id: '4.1.1',
    title: '4.1.1 • 4/1.1',
    channels: ['FL', 'FR', 'LFE', 'RL', 'RR', 'TC']
  },
  {
    id: '4.0',
    title: '4.0 Quadraphonic • 4/0.0',
    channels: ['FL', 'FR', 'RL', 'RR']
  },
  {
    id: '2.0',
    title: '2.0 Stereo • 2/0.0',
    channels: ['FL', 'FR']
  },
  {
    id: '1.0',
    title: '1.0 Mono • 1/0.0',
    channels: ['FC']
  },
  {
    id: 'virtualizer',
    title: 'Headphone Virtualizer • Binaural',
    channels: ['FL', 'FR']
  },
  {
    id: 'Small Cinema',
    title: 'Small Cinema • 11/0.1',
    channels: ['FL', 'FR', 'FC', 'LFE', 'SL', 'SR', 'RL', 'RR', 'WL', 'WR', 'FLC', 'FRC']
  },
  {
    id: '22.2 Surround',
    title: '22.2 Surround • 11/11.2',
    channels: ['FL', 'FR', 'FC', 'LFE', 'RL', 'RR', 'FLC', 'FRC', 'RC', 'SL', 'SR', 'TFL', 'TFR', 'TFC', 'TC', 'TRL', 'TRR', 'TSL', 'TSR', 'TRC', 'LFE2', 'BFL', 'BFR', 'BFC']
  }
];

const fileInput = document.getElementById('fileInput');
const dropzone = document.getElementById('dropzone');
const dropzoneEmpty = document.getElementById('dropzoneEmpty');
const dropzoneSelected = document.getElementById('dropzoneSelected');
const selectedCountText = document.getElementById('selectedCountText');
const queueContainer = document.getElementById('queueContainer');
const queueList = document.getElementById('queueList');
const startBtn = document.getElementById('startBtn');
const cancelBtn = document.getElementById('cancelBtn');
const fpsBadge = document.getElementById('fpsBadge');
const fpsNumber = document.getElementById('fpsNumber');
const errorBanner = document.getElementById('errorBanner');
const decodedSection = document.getElementById('decodedSection');
const decodedList = document.getElementById('decodedList');
const clearDecodedBtn = document.getElementById('clearDecodedBtn');

const renderTargetSelect = document.getElementById('renderTargetSelect');
const layoutDropdownTrigger = document.getElementById('layoutDropdownTrigger');
const layoutDropdownMenu = document.getElementById('layoutDropdownMenu');
const selectedLayoutTitle = document.getElementById('selectedLayoutTitle');
const selectedLayoutChannels = document.getElementById('selectedLayoutChannels');

const advancedToggle = document.getElementById('advancedToggle');
const advancedPanel = document.getElementById('advancedPanel');
const advancedChevron = document.getElementById('advancedChevron');
const pcmBitDepth = document.getElementById('pcmBitDepth');
const outputSampleRate = document.getElementById('outputSampleRate');
const updateRateSelect = document.getElementById('updateRateSelect');
const audioQualitySelect = document.getElementById('audioQualitySelect');
const cavernGainDb = document.getElementById('cavernGainDb');
const surroundSwapSwitch = document.getElementById('surroundSwapSwitch');
const lfeSeparationSwitch = document.getElementById('lfeSeparationSwitch');
const stderrSwitch = document.getElementById('stderrSwitch');
const loadErudaBtn = document.getElementById('loadErudaBtn');

let selectedFiles = [];
let isProcessing = false;
let decoderWorker = null;
let currentResolve = null;
let currentReject = null;

function formatChannelSubtitle(layout) {
  const swap = surroundSwapSwitch && surroundSwapSwitch.checked;
  const chList = layout.channels.map((ch) => {
    if (!swap) return ch;
    if (ch === 'RL') return 'SL';
    if (ch === 'RR') return 'SR';
    if (ch === 'SL') return 'RL';
    if (ch === 'SR') return 'RR';
    return ch;
  });
  return `Cavern ${chList.length}ch – ${chList.join(', ')}`;
}

function renderLayoutDropdown() {
  const currentId = renderTargetSelect.value || '9.1.6';
  layoutDropdownMenu.innerHTML = '';

  const activeLayout = CAVERN_LAYOUTS.find((l) => l.id === currentId) || CAVERN_LAYOUTS[0];
  selectedLayoutTitle.textContent = activeLayout.title;
  selectedLayoutChannels.textContent = formatChannelSubtitle(activeLayout);

  CAVERN_LAYOUTS.forEach((layout) => {
    const isSelected = layout.id === activeLayout.id;
    const item = document.createElement('div');
    item.className = `layout-option${isSelected ? ' selected' : ''}`;
    item.setAttribute('role', 'option');
    item.setAttribute('aria-selected', String(isSelected));

    item.innerHTML = `
      <svg class="layout-option-check" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4">
        <polyline points="20 6 9 17 4 12"/>
      </svg>
      <div class="layout-option-texts">
        <div class="layout-option-title">${escapeHtml(layout.title)}</div>
        <div class="layout-option-sub">${escapeHtml(formatChannelSubtitle(layout))}</div>
      </div>
    `;

    item.addEventListener('click', () => {
      renderTargetSelect.value = layout.id;
      renderLayoutDropdown();
      closeLayoutDropdown();
    });

    layoutDropdownMenu.appendChild(item);
  });
}

function closeLayoutDropdown() {
  layoutDropdownMenu.classList.add('hidden');
  layoutDropdownTrigger.classList.remove('open');
  layoutDropdownTrigger.setAttribute('aria-expanded', 'false');
}

layoutDropdownTrigger.addEventListener('click', (e) => {
  e.stopPropagation();
  const isHidden = layoutDropdownMenu.classList.contains('hidden');
  if (isHidden) {
    renderLayoutDropdown();
    layoutDropdownMenu.classList.remove('hidden');
    layoutDropdownTrigger.classList.add('open');
    layoutDropdownTrigger.setAttribute('aria-expanded', 'true');
  } else {
    closeLayoutDropdown();
  }
});

document.addEventListener('click', (e) => {
  if (!e.target.closest('#layoutDropdownContainer')) {
    closeLayoutDropdown();
  }
});

if (surroundSwapSwitch) {
  surroundSwapSwitch.addEventListener('change', () => {
    renderLayoutDropdown();
  });
}

renderLayoutDropdown();

function showError(msg) {
  errorBanner.textContent = msg;
  errorBanner.classList.remove('hidden');
  console.error(msg);
}

function clearError() {
  errorBanner.textContent = '';
  errorBanner.classList.add('hidden');
}

function initWorker() {
  if (decoderWorker) decoderWorker.terminate();
  decoderWorker = new Worker('worker.js', { type: 'module' });
  decoderWorker.onmessage = handleWorkerMessage;
  decoderWorker.onerror = (err) => {
    const errMsg = err.message || 'Error loading Web Worker or _framework/dotnet.js';
    showError(`[Worker Error] ${errMsg}`);
    if (currentReject) {
      currentReject(new Error(errMsg));
      currentResolve = null;
      currentReject = null;
    }
  };
}

initWorker();

dropzone.addEventListener('click', () => {
  if (!isProcessing) fileInput.click();
});

dropzone.addEventListener('dragover', (e) => {
  e.preventDefault();
  if (!isProcessing) dropzone.classList.add('dragover');
});

dropzone.addEventListener('dragleave', () => {
  dropzone.classList.remove('dragover');
});

dropzone.addEventListener('drop', (e) => {
  e.preventDefault();
  dropzone.classList.remove('dragover');
  if (isProcessing) return;
  if (e.dataTransfer.files && e.dataTransfer.files.length > 0) {
    clearError();
    setFiles(Array.from(e.dataTransfer.files));
  }
});

fileInput.addEventListener('change', (e) => {
  if (e.target.files && e.target.files.length > 0) {
    clearError();
    setFiles(Array.from(e.target.files));
  }
});

function setFiles(files) {
  selectedFiles = files;
  if (selectedFiles.length === 0) {
    dropzoneEmpty.classList.remove('hidden');
    dropzoneSelected.classList.add('hidden');
    queueContainer.classList.add('hidden');
    return;
  }

  dropzoneEmpty.classList.add('hidden');
  dropzoneSelected.classList.remove('hidden');
  selectedCountText.textContent = `${selectedFiles.length} file${selectedFiles.length > 1 ? 's' : ''} selected`;
  queueContainer.classList.remove('hidden');
  renderQueueList();
}

function renderQueueList(activeIndex = -1, statusSubText = '0 frames') {
  queueList.innerHTML = '';
  selectedFiles.forEach((file, idx) => {
    const item = document.createElement('div');
    item.className = 'queue-item';
    const isCurrent = idx === activeIndex;

    const iconHtml = isCurrent
      ? `<svg class="m3-spinner" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2">
           <path d="M12 2c1.5 0 2.6 1.2 4 1.8 1.4.6 3.1.2 4.1 1.2 1 1 .6 2.7 1.2 4.1.6 1.4 1.8 2.5 1.8 4s-1.2 2.6-1.8 4c-.6 1.4-.2 3.1-1.2 4.1-1 1-2.7.6-4.1 1.2-1.4.6-2.5 1.8-4 1.8s-2.6-1.2-4-1.8c-1.4-.6-3.1-.2-4.1-1.2-1-1-.6-2.7-1.2-4.1C2.2 14.6 1 13.5 1 12s1.2-2.6 1.8-4c.6-1.4.2-3.1 1.2-4.1 1-1 2.7-.6 4.1-1.2C9.4 2.2 10.5 2 12 2z"/>
         </svg>`
      : `<svg class="file-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8">
           <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
           <polyline points="14 2 14 8 20 8"/>
           <circle cx="10" cy="16" r="2"/>
           <path d="M12 16v-5l4 1"/>
         </svg>`;

    const statusHtml = isCurrent
      ? `<div class="status-main">Processing</div>
         <div class="status-sub" id="activeFrameCount">${escapeHtml(statusSubText)}</div>`
      : `<div class="status-main">${idx < activeIndex ? 'Done' : 'Waiting to<br>process'}</div>`;

    item.innerHTML = `
      <div class="queue-item-left">
        ${iconHtml}
        <div class="file-name">${escapeHtml(file.name)}</div>
      </div>
      <div class="queue-item-right">
        ${statusHtml}
      </div>
    `;
    queueList.appendChild(item);
  });
}

startBtn.addEventListener('click', async () => {
  if (isProcessing || selectedFiles.length === 0) return;
  clearError();
  isProcessing = true;

  dropzone.classList.add('hidden');
  startBtn.classList.add('hidden');
  cancelBtn.classList.remove('hidden');
  fpsBadge.classList.remove('hidden');
  fpsNumber.textContent = '0.0';

  for (let i = 0; i < selectedFiles.length; i++) {
    if (!isProcessing) break;
    const file = selectedFiles[i];
    renderQueueList(i, 'Loading WASM...');

    const arrayBuffer = await file.arrayBuffer();
    const parsedGain = parseFloat(cavernGainDb.value);
    const baseLayout = renderTargetSelect.value || '9.1.6';
    const targetLayout = (surroundSwapSwitch && surroundSwapSwitch.checked)
      ? `${baseLayout}|ss`
      : baseLayout;

    const options = {
      targetLayout,
      displayLayout: baseLayout,
      pcmBitDepth: parseInt(pcmBitDepth.value, 10) || 24,
      targetSampleRate: parseInt(outputSampleRate.value, 10) || 0,
      updateRateSamples: parseInt(updateRateSelect.value, 10) || 256,
      audioQualityIndex: parseInt(audioQualitySelect.value, 10),
      gainDb: Number.isNaN(parsedGain) ? 0.0 : parsedGain,
      lfeSeparation: lfeSeparationSwitch.checked,
      printLogs: stderrSwitch.checked
    };

    try {
      const result = await decodeFileInWorker(file.name, arrayBuffer, options);
      if (result && result.wavBlob) {
        addDecodedFileCard(
          file.name,
          options.displayLayout,
          result.wavBlob,
          result.framesProcessed,
          result.channelLabels,
          result.outChannels
        );
      }
    } catch (err) {
      showError(err && err.message ? err.message : String(err));
    }
  }

  finishProcessingState();
});

cancelBtn.addEventListener('click', () => {
  if (!isProcessing) return;
  isProcessing = false;
  initWorker();
  finishProcessingState();
});

function finishProcessingState() {
  isProcessing = false;
  fpsBadge.classList.add('hidden');
  cancelBtn.classList.add('hidden');
  startBtn.classList.remove('hidden');
  dropzone.classList.remove('hidden');
  setFiles([]);
}

function decodeFileInWorker(fileName, arrayBuffer, options) {
  return new Promise((resolve, reject) => {
    currentResolve = resolve;
    currentReject = reject;
    decoderWorker.postMessage(
      { type: 'DECODE', fileName, buffer: arrayBuffer, options },
      [arrayBuffer]
    );
  });
}

function handleWorkerMessage(e) {
  const msg = e.data;
  if (msg.type === 'STATUS') {
    const frameEl = document.getElementById('activeFrameCount');
    if (frameEl) frameEl.textContent = msg.text;
  } else if (msg.type === 'PROGRESS') {
    fpsNumber.textContent = msg.fps.toFixed(1);
    const frameEl = document.getElementById('activeFrameCount');
    if (frameEl) frameEl.textContent = `${msg.framesProcessed} frames`;
  } else if (msg.type === 'LOG') {
    if (msg.isError) console.error(msg.message);
    else console.log(msg.message);
  } else if (msg.type === 'DONE') {
    if (currentResolve) {
      currentResolve(msg);
      currentResolve = null;
      currentReject = null;
    }
  } else if (msg.type === 'ERROR') {
    if (currentReject) {
      currentReject(new Error(msg.error));
      currentResolve = null;
      currentReject = null;
    }
  }
}

function addDecodedFileCard(originalName, layoutName, wavBlob, totalFrames, channelLabels, outChannels) {
  decodedSection.classList.remove('hidden');

  const baseName = originalName.replace(/\.[^/.]+$/, '');
  const cleanLayout = layoutName.replace(/[^a-zA-Z0-9._-]/g, '_');
  const outName = `${baseName}_${cleanLayout}.wav`;
  const url = URL.createObjectURL(wavBlob);
  const sizeMb = (wavBlob.size / (1024 * 1024)).toFixed(2);

  const card = document.createElement('div');
  card.className = 'decoded-card';
  card.innerHTML = `
    <div class="decoded-title" title="${escapeHtml(outName)}">${escapeHtml(outName)}</div>
    <div class="decoded-frames">${totalFrames} frames • ${outChannels || ''} channels</div>
    ${channelLabels ? `<div class="decoded-channels-badge">Order: ${escapeHtml(channelLabels)}</div>` : ''}
    <a class="btn-download-pill" href="${url}" download="${escapeHtml(outName)}">
      <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2">
        <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/>
        <polyline points="7 10 12 15 17 10"/>
        <line x1="12" y1="15" x2="12" y2="3"/>
      </svg>
      <span>Download • ${sizeMb} MB</span>
    </a>
    <audio class="decoded-audio" controls src="${url}"></audio>
  `;
  decodedList.prepend(card);
}

clearDecodedBtn.addEventListener('click', () => {
  decodedList.innerHTML = '';
  decodedSection.classList.add('hidden');
});

advancedToggle.addEventListener('click', () => {
  const expanded = advancedToggle.getAttribute('aria-expanded') === 'true';
  advancedToggle.setAttribute('aria-expanded', String(!expanded));
  advancedPanel.classList.toggle('hidden', expanded);
  advancedChevron.style.transform = expanded ? 'rotate(0deg)' : 'rotate(180deg)';
});

loadErudaBtn.addEventListener('click', () => {
  if (window.eruda) {
    window.eruda.show();
    return;
  }
  const script = document.createElement('script');
  script.src = 'https://cdn.jsdelivr.net/npm/eruda';
  script.onload = () => {
    window.eruda.init();
    window.eruda.show();
    loadErudaBtn.querySelector('span').textContent = 'Eruda Loaded';
  };
  document.body.appendChild(script);
});

function escapeHtml(str) {
  return String(str).replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  }[c]));
}
