// SkyMessage media devices (our own code). Shared by Settings › Devices and the call screen:
//   capabilities() · listDevices(requestAudio, requestVideo) · watchDevices(dotnetRef, method)
//   createMicTest(dotnetRef, meterEl) · createCameraPreview(videoEl) · playTestTone(sinkId) · fetchText(url)
// Device labels are only exposed after the user granted access, so listDevices() can open (and immediately
// close) a capture stream first. Every capture opened here is stopped by its handle's stop()/dispose().

const md = () => (typeof navigator !== 'undefined' ? navigator.mediaDevices : undefined);
const ALIASES = new Set(['default', 'communications']);

export function capabilities() {
  const media = md();
  return {
    media: !!(media && media.getUserMedia),
    enumerate: !!(media && media.enumerateDevices),
    speakerSelection: typeof HTMLMediaElement !== 'undefined' && 'setSinkId' in HTMLMediaElement.prototype,
    screenShare: !!(media && media.getDisplayMedia),
    secureContext: window.isSecureContext !== false,
    recorder: typeof MediaRecorder !== 'undefined',
  };
}

// Maps a getUserMedia failure to a short code the UI understands.
export function errorCode(e) {
  const name = (e && e.name) || '';
  if (!window.isSecureContext) return 'insecure';
  switch (name) {
    case 'NotAllowedError':
    case 'SecurityError':
    case 'PermissionDeniedError':
      return 'denied';
    case 'NotFoundError':
    case 'DevicesNotFoundError':
    case 'OverconstrainedError':
      return 'none';
    case 'NotReadableError':
    case 'TrackStartError':
    case 'AbortError':
      return 'busy';
    case 'TypeError':
      return 'unsupported';
    default:
      return 'failed';
  }
}

async function enumerate() {
  try {
    return await md().enumerateDevices();
  } catch (e) {
    return [];
  }
}

async function queryPermission(name) {
  try {
    if (!navigator.permissions || !navigator.permissions.query) return 'unknown';
    const status = await navigator.permissions.query({ name });
    return status.state || 'unknown';
  } catch (e) {
    return 'unknown';
  }
}

async function probe(kind) {
  try {
    const stream = await md().getUserMedia(kind === 'audio' ? { audio: true } : { video: true });
    stream.getTracks().forEach((t) => t.stop());
    return 'granted';
  } catch (e) {
    return errorCode(e);
  }
}

function toItems(devices, kind) {
  const seen = new Set();
  const items = [];
  for (const d of devices) {
    if (d.kind !== kind || !d.deviceId || ALIASES.has(d.deviceId) || seen.has(d.deviceId)) continue;
    seen.add(d.deviceId);
    items.push({ id: d.deviceId, label: d.label || '', groupId: d.groupId || '' });
  }
  return items;
}

/**
 * Lists inputs/outputs. With requestAudio/requestVideo the user is asked for access when labels are hidden.
 * Permissions: 'granted' | 'denied' | 'prompt' | 'none' (no such device) | 'busy' | 'insecure' | 'unsupported' | 'unknown'.
 */
export async function listDevices(requestAudio, requestVideo) {
  const result = {
    audioInputs: [], videoInputs: [], audioOutputs: [],
    audioPermission: 'unknown', videoPermission: 'unknown',
    supported: true,
  };
  const media = md();
  if (!media || !media.enumerateDevices) {
    result.supported = false;
    result.audioPermission = window.isSecureContext === false ? 'insecure' : 'unsupported';
    result.videoPermission = result.audioPermission;
    return result;
  }

  let devices = await enumerate();
  const hasAudioLabels = devices.some((d) => d.kind === 'audioinput' && d.label);
  const hasVideoLabels = devices.some((d) => d.kind === 'videoinput' && d.label);
  if (requestAudio && !hasAudioLabels && media.getUserMedia) result.audioPermission = await probe('audio');
  if (requestVideo && !hasVideoLabels && media.getUserMedia && devices.some((d) => d.kind === 'videoinput'))
    result.videoPermission = await probe('video');
  if (result.audioPermission !== 'unknown' || result.videoPermission !== 'unknown') devices = await enumerate();

  if (result.audioPermission === 'unknown')
    result.audioPermission = devices.some((d) => d.kind === 'audioinput' && d.label) ? 'granted' : await queryPermission('microphone');
  if (result.videoPermission === 'unknown') {
    if (!devices.some((d) => d.kind === 'videoinput')) result.videoPermission = 'none';
    else result.videoPermission = devices.some((d) => d.kind === 'videoinput' && d.label) ? 'granted' : await queryPermission('camera');
  }

  result.audioInputs = toItems(devices, 'audioinput');
  result.videoInputs = toItems(devices, 'videoinput');
  result.audioOutputs = toItems(devices, 'audiooutput');
  return result;
}

/** Calls dotnetRef[method]() (debounced) when devices are plugged in or removed. Returns a handle with dispose(). */
export function watchDevices(dotnetRef, method) {
  const media = md();
  let timer = 0;
  const handler = () => {
    clearTimeout(timer);
    timer = setTimeout(() => dotnetRef.invokeMethodAsync(method).catch(() => { }), 300);
  };
  if (media && media.addEventListener) media.addEventListener('devicechange', handler);
  return {
    dispose() {
      clearTimeout(timer);
      if (media && media.removeEventListener) media.removeEventListener('devicechange', handler);
    },
  };
}

function audioConstraints(options) {
  const o = options || {};
  const c = {
    echoCancellation: o.echoCancellation !== false,
    noiseSuppression: o.noiseSuppression !== false,
    autoGainControl: o.autoGainControl !== false,
  };
  if (o.deviceId) c.deviceId = { exact: o.deviceId };
  return c;
}

function newAudioContext() {
  const AC = window.AudioContext || window.webkitAudioContext;
  if (!AC) return null;
  try {
    return new AC();
  } catch (e) {
    return null;
  }
}

async function playThrough(audio, sinkId) {
  if (sinkId && typeof audio.setSinkId === 'function') {
    try {
      await audio.setSinkId(sinkId);
    } catch (e) {
      // Unknown or revoked output: fall back to the default one.
    }
  }
  await audio.play();
}

// ---- Microphone test: live level meter + record a few seconds and play them back -------------------------------

class MicTest {
  constructor(dotnetRef, meterEl) {
    this.dotnet = dotnetRef;
    this.meter = meterEl;
    this.stream = null;
    this.ctx = null;
    this.analyser = null;
    this.raf = 0;
    this.level = 0;
    this.recorder = null;
    this.chunks = [];
    this.recordTimer = 0;
    this.blobUrl = null;
    this.audio = null;
    this.closed = false;
  }

  // Events: live · stopped (capture) · recording · recorded · recordEmpty · playing · played.
  notify(event, detail) {
    if (this.closed) return;
    try {
      this.dotnet.invokeMethodAsync('OnMicTestState', event, detail || null).catch(() => { });
    } catch (e) {
      // .NET side already gone.
    }
  }

  /** Opens the microphone and starts the level meter. Returns { ok, error }. */
  async start(options) {
    this.stopCapture();
    const media = md();
    if (!media || !media.getUserMedia) return { ok: false, error: window.isSecureContext === false ? 'insecure' : 'unsupported' };
    try {
      this.stream = await media.getUserMedia({ audio: audioConstraints(options) });
    } catch (e) {
      if (options && options.deviceId && errorCode(e) === 'none') {
        // The saved microphone is gone: use the system default.
        try {
          this.stream = await media.getUserMedia({ audio: audioConstraints({ ...options, deviceId: null }) });
        } catch (e2) {
          return { ok: false, error: errorCode(e2) };
        }
      } else {
        return { ok: false, error: errorCode(e) };
      }
    }
    if (this.closed) {
      this.stopCapture();
      return { ok: false, error: 'closed' };
    }
    this.ctx = newAudioContext();
    if (this.ctx) {
      try {
        if (this.ctx.state === 'suspended') await this.ctx.resume();
        const source = this.ctx.createMediaStreamSource(this.stream);
        this.analyser = this.ctx.createAnalyser();
        this.analyser.fftSize = 1024;
        this.analyser.smoothingTimeConstant = 0.6;
        source.connect(this.analyser);
        this.loop();
      } catch (e) {
        this.analyser = null;
      }
    }
    const track = this.stream.getAudioTracks()[0];
    const settings = track && track.getSettings ? track.getSettings() : {};
    this.notify('live');
    return { ok: true, error: null, deviceId: settings.deviceId || null };
  }

  loop() {
    const data = new Float32Array(this.analyser.fftSize);
    const tick = () => {
      if (!this.analyser) return;
      this.analyser.getFloatTimeDomainData(data);
      let sum = 0;
      for (let i = 0; i < data.length; i++) sum += data[i] * data[i];
      const rms = Math.sqrt(sum / data.length);
      // -60 dBFS … 0 dBFS → 0 … 1, with a quick attack and a slower release.
      const db = rms > 0 ? 20 * Math.log10(rms) : -100;
      const target = Math.max(0, Math.min(1, (db + 60) / 60));
      this.level = target > this.level ? target : this.level * 0.85 + target * 0.15;
      if (this.meter) this.meter.style.setProperty('--level', this.level.toFixed(3));
      this.raf = requestAnimationFrame(tick);
    };
    this.raf = requestAnimationFrame(tick);
  }

  /** Records up to maxMs (default 5 s) from the open microphone. */
  record(maxMs) {
    if (!this.stream || typeof MediaRecorder === 'undefined') return { ok: false, error: 'unsupported' };
    this.stopPlayback();
    this.releaseRecording();
    this.chunks = [];
    try {
      this.recorder = new MediaRecorder(this.stream);
    } catch (e) {
      return { ok: false, error: 'unsupported' };
    }
    this.recorder.ondataavailable = (e) => { if (e.data && e.data.size > 0) this.chunks.push(e.data); };
    this.recorder.onstop = () => {
      clearTimeout(this.recordTimer);
      if (this.closed) return;
      if (this.chunks.length === 0) {
        this.notify('recordEmpty');
        return;
      }
      const blob = new Blob(this.chunks, { type: this.recorder && this.recorder.mimeType ? this.recorder.mimeType : 'audio/webm' });
      this.chunks = [];
      this.blobUrl = URL.createObjectURL(blob);
      this.notify('recorded');
    };
    this.recorder.start();
    this.recordTimer = setTimeout(() => this.stopRecording(), Math.max(1000, Math.min(15000, maxMs || 5000)));
    this.notify('recording');
    return { ok: true, error: null };
  }

  stopRecording() {
    clearTimeout(this.recordTimer);
    if (this.recorder && this.recorder.state !== 'inactive') {
      try { this.recorder.stop(); } catch (e) { /* already stopped */ }
    }
  }

  /** Plays the last recording through the chosen output (setSinkId where supported). */
  async play(sinkId) {
    if (!this.blobUrl) return { ok: false, error: 'empty' };
    this.stopPlayback();
    const audio = new Audio(this.blobUrl);
    this.audio = audio;
    audio.onended = () => {
      if (this.audio === audio) this.audio = null;
      this.notify('played');
    };
    try {
      await playThrough(audio, sinkId);
      this.notify('playing');
      return { ok: true, error: null };
    } catch (e) {
      this.audio = null;
      return { ok: false, error: 'playback' };
    }
  }

  stopPlayback() {
    if (this.audio) {
      try { this.audio.pause(); } catch (e) { /* ignore */ }
      this.audio = null;
      this.notify('played');
    }
  }

  releaseRecording() {
    if (this.blobUrl) {
      URL.revokeObjectURL(this.blobUrl);
      this.blobUrl = null;
    }
  }

  stopCapture() {
    cancelAnimationFrame(this.raf);
    this.raf = 0;
    this.stopRecording();
    this.analyser = null;
    if (this.ctx) {
      this.ctx.close().catch(() => { });
      this.ctx = null;
    }
    if (this.stream) {
      this.stream.getTracks().forEach((t) => t.stop());
      this.stream = null;
    }
    this.level = 0;
    if (this.meter) this.meter.style.setProperty('--level', '0');
  }

  /** Releases the microphone but keeps the recording for playback. */
  stop() {
    this.stopCapture();
    this.notify('stopped');
  }

  dispose() {
    this.closed = true;
    this.stopCapture();
    this.stopPlayback();
    this.releaseRecording();
    this.meter = null;
  }
}

export function createMicTest(dotnetRef, meterEl) {
  return new MicTest(dotnetRef, meterEl);
}

// ---- Camera preview -------------------------------------------------------------------------------------------

const QUALITY = {
  low: { width: 640, height: 360 },
  medium: { width: 960, height: 540 },
  high: { width: 1280, height: 720 },
  auto: { width: 1280, height: 720 },
};

class CameraPreview {
  constructor(videoEl) {
    this.video = videoEl;
    this.stream = null;
    this.closed = false;
  }

  /** Starts (or restarts) the preview. Returns { ok, error, deviceId, facing }. */
  async start(deviceId, quality, facing) {
    this.stop();
    const media = md();
    if (!media || !media.getUserMedia) return { ok: false, error: window.isSecureContext === false ? 'insecure' : 'unsupported' };
    const size = QUALITY[quality] || QUALITY.auto;
    const video = { width: { ideal: size.width }, height: { ideal: size.height } };
    if (deviceId) video.deviceId = { exact: deviceId };
    else if (facing) video.facingMode = { ideal: facing };
    try {
      this.stream = await media.getUserMedia({ video, audio: false });
    } catch (e) {
      if (deviceId && errorCode(e) === 'none') return this.start(null, quality, facing);
      return { ok: false, error: errorCode(e) };
    }
    if (this.closed) {
      this.stop();
      return { ok: false, error: 'closed' };
    }
    if (this.video) {
      this.video.muted = true;
      this.video.playsInline = true;
      this.video.srcObject = this.stream;
      try { await this.video.play(); } catch (e) { /* autoplay of a muted preview may still be blocked; frames still show */ }
    }
    const track = this.stream.getVideoTracks()[0];
    const settings = track && track.getSettings ? track.getSettings() : {};
    return { ok: true, error: null, deviceId: settings.deviceId || null, facing: settings.facingMode || null };
  }

  stop() {
    if (this.stream) {
      this.stream.getTracks().forEach((t) => t.stop());
      this.stream = null;
    }
    if (this.video) this.video.srcObject = null;
  }

  dispose() {
    this.closed = true;
    this.stop();
    this.video = null;
  }
}

export function createCameraPreview(videoEl) {
  return new CameraPreview(videoEl);
}

// ---- Speaker test: a short two-note chime of our own, routed to the chosen output -----------------------------

export async function playTestTone(sinkId) {
  const ctx = newAudioContext();
  if (!ctx) return { ok: false, error: 'unsupported' };
  try {
    if (ctx.state === 'suspended') await ctx.resume();
    const useElement = !!sinkId && typeof HTMLMediaElement !== 'undefined' && 'setSinkId' in HTMLMediaElement.prototype && ctx.createMediaStreamDestination;
    const out = useElement ? ctx.createMediaStreamDestination() : ctx.destination;
    const master = ctx.createGain();
    master.gain.value = 0.5;
    master.connect(out);
    const t0 = ctx.currentTime + 0.05;
    const notes = [[0, 659.25], [0.28, 880], [0.56, 1046.5]];
    for (const [start, freq] of notes) {
      for (const [mult, amp] of [[1, 0.5], [2, 0.12], [3, 0.05]]) {
        const osc = ctx.createOscillator();
        const g = ctx.createGain();
        osc.type = 'sine';
        osc.frequency.setValueAtTime(freq * mult, t0 + start);
        g.gain.setValueAtTime(0, t0 + start);
        g.gain.linearRampToValueAtTime(amp, t0 + start + 0.01);
        g.gain.setTargetAtTime(0, t0 + start + 0.01, 0.18);
        osc.connect(g);
        g.connect(master);
        osc.start(t0 + start);
        osc.stop(t0 + start + 1.4);
      }
    }
    let audio = null;
    if (useElement) {
      audio = new Audio();
      audio.srcObject = out.stream;
      await playThrough(audio, sinkId);
    }
    await new Promise((resolve) => setTimeout(resolve, 2100));
    if (audio) {
      audio.pause();
      audio.srcObject = null;
    }
    return { ok: true, error: null };
  } catch (e) {
    return { ok: false, error: 'playback' };
  } finally {
    ctx.close().catch(() => { });
  }
}

/** Plain text of a static file shipped with the app (license texts on the About page). */
export async function fetchText(url) {
  try {
    const response = await fetch(url, { cache: 'no-cache' });
    return response.ok ? await response.text() : null;
  } catch (e) {
    return null;
  }
}
