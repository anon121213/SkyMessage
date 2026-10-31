// SkyMessage call media engine (our own code) on top of livekit-client, vendored unmodified as the UMD build in
// ../lib/livekit/ (Apache-2.0, see LICENSE and THIRD-PARTY-NOTICES.txt there).
//
// One engine per call screen. .NET drives it — connect(options), setMicEnabled, setCameraEnabled, switchCamera,
// setScreenShare, setSpeaker, setMicrophoneDevice, restartCamera, startAudio, disconnect — and receives reports
// through its DotNetObjectReference:
//   OnEngineState(state, detail)    connecting | connected | reconnecting | disconnected | failed | replaced
//   OnLocalMedia(info)              { micEnabled, cameraEnabled, screenSharing, speaking, quality, facing, micPublished }
//   OnParticipants(list)            remote people (LiveKit identities are per device; grouped by metadata.userId)
//   OnEngineNotice(kind, detail)    audioBlocked | deviceError | cameraInterrupted | devicesChanged | micPublished
//
// Video is rendered into slots declared by the Razor UI inside the bound stage element:
//   <div data-slot="cam:{userId}|screen:{userId}|local-camera|local-screen"></div>
// The engine keeps a <video> element attached in each slot (a MutationObserver follows re-renders). Elements with
// [data-pip] become draggable and snap to the nearest corner with a spring. Remote audio plays through hidden <audio>
// elements appended to <body>, outside the Blazor tree.
//
// attachMinimize(screen, backdrop, dotnetRef) is the expanded call screen's drag-down-to-minimize gesture (see
// docs/MOTION.md); .NET hears OnMinimizeArmed() at the commit point and OnMinimizeGesture() once it collapsed.

import { reduceMotion } from './app.js';

const LK_SRC = new URL('../lib/livekit/livekit-client.umd.js', import.meta.url).href;
const ALIASES = new Set(['default', 'communications']);
const QUALITY = { excellent: 3, good: 2, poor: 1, lost: 0 };

let lkPromise = null;

function loadLiveKit() {
  if (window.LivekitClient && window.LivekitClient.Room) return Promise.resolve(window.LivekitClient);
  if (!lkPromise) {
    lkPromise = new Promise((resolve, reject) => {
      const script = document.createElement('script');
      script.src = LK_SRC;
      script.async = true;
      script.onload = () => {
        const lk = window.LivekitClient;
        if (lk && lk.Room) {
          try { lk.setLogLevel('warn'); } catch (e) { /* older builds */ }
          resolve(lk);
        } else {
          lkPromise = null;
          reject(new Error('livekit_unavailable'));
        }
      };
      script.onerror = () => {
        lkPromise = null;
        script.remove();
        reject(new Error('livekit_unavailable'));
      };
      document.head.appendChild(script);
    });
  }
  return lkPromise;
}

/** Loads livekit-client ahead of time (e.g. while an incoming call rings). */
export async function preload() {
  try {
    await loadLiveKit();
    return true;
  } catch (e) {
    return false;
  }
}

export function createEngine(dotnetRef) {
  return new CallEngine(dotnetRef);
}

function normId(id) {
  return String(id || '').replace(/-/g, '').toLowerCase();
}

function userIdOf(participant) {
  if (participant.metadata) {
    try {
      const meta = JSON.parse(participant.metadata);
      if (meta && meta.userId) return normId(meta.userId);
    } catch (e) {
      // Not our metadata; fall back to the identity "{userId:N}.{sessionId:N}".
    }
  }
  const identity = participant.identity || '';
  const dot = identity.indexOf('.');
  return normId(dot > 0 ? identity.slice(0, dot) : identity);
}

function qualityOf(q) {
  return Object.prototype.hasOwnProperty.call(QUALITY, q) ? QUALITY[q] : -1;
}

function errorCode(e) {
  const name = (e && e.name) || '';
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
    default:
      return 'failed';
  }
}

const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// ---- motion helpers (docs/MOTION.md) -------------------------------------------------------------------------------

/**
 * Samples a damped spring (mass 1) from `from` to `to` (arrays of numbers) starting with `velocity` (units per ms per
 * component) at 60 fps. Settles in ~250 ms with a small overshoot; never longer than `maxMs`, the last frame is `to`.
 */
function springFrames(from, to, velocity, maxMs = 320) {
  const stiffness = 600;
  const damping = 32;
  const dt = 1 / 60;
  const x = from.map((value, i) => value - to[i]);
  const v = from.map((_, i) => ((velocity && velocity[i]) || 0) * 1000);
  const frames = [from.slice()];
  for (let t = dt; t * 1000 < maxMs; t += dt) {
    for (let i = 0; i < x.length; i++) {
      v[i] += (-stiffness * x[i] - damping * v[i]) * dt;
      x[i] += v[i] * dt;
    }
    frames.push(x.map((d, i) => to[i] + d));
    if (x.every((d) => Math.abs(d) < 0.25) && v.every((s) => Math.abs(s) < 15)) break;
  }
  frames.push(to.slice());
  return { frames, duration: Math.round(((frames.length - 1) * 1000) / 60) };
}

/** Pointer velocity (px/ms) over the last ~100 ms of { t, x, y } samples. */
function velocityOf(samples) {
  if (samples.length < 2) return { x: 0, y: 0 };
  const last = samples[samples.length - 1];
  let first = samples[0];
  for (const s of samples) {
    if (last.t - s.t <= 100) {
      first = s;
      break;
    }
  }
  const dt = last.t - first.t;
  return dt > 0 ? { x: (last.x - first.x) / dt, y: (last.y - first.y) / dt } : { x: 0, y: 0 };
}

function pushSample(samples, e) {
  samples.push({ t: e.timeStamp || performance.now(), x: e.clientX, y: e.clientY });
  if (samples.length > 8) samples.shift();
}

/** The translation an element shows right now (inline style or a running animation). */
function shownOffset(element) {
  const value = getComputedStyle(element).transform;
  if (!value || value === 'none') return { x: 0, y: 0 };
  try {
    const m = new DOMMatrixReadOnly(value);
    return { x: m.m41, y: m.m42 };
  } catch (e) {
    return { x: 0, y: 0 };
  }
}

// The click that ends a drag must not count as a tap on what is under the finger.
function swallowNextClick(element) {
  const swallow = (ev) => {
    ev.stopPropagation();
    ev.preventDefault();
  };
  element.addEventListener('click', swallow, { capture: true, once: true });
  setTimeout(() => element.removeEventListener('click', swallow, { capture: true }), 60);
}

class CallEngine {
  constructor(dotnetRef) {
    this.dotnet = dotnetRef;
    this.LK = null;
    this.room = null;
    this.session = 0;
    this.connected = false;
    this.me = '';
    this.mobile = false;
    this.wantMic = true;
    this.wantCamera = false;
    this.facing = 'user';
    this.audioOptions = {};
    this.videoDeviceId = null;
    this.speakerId = null;
    this.preset = null;
    this.prepared = null;          // camera track created before the room connected (instant preview)
    this.cameraChain = Promise.resolve(); // camera work runs one operation at a time (see cameraWork)
    this.idle = true;              // no connect() since construction or the last disconnect()
    this.stage = null;
    this.observer = null;
    this.slots = new Map();        // slot element -> { key, track, video }
    this.pips = new WeakSet();
    this.slotRaf = 0;
    this.audioHost = null;
    this.lastActive = '';
    this.lastLocal = '';
    this.lastParticipants = '';
    this.partTimer = 0;
    this.partDirty = false;
    this.camTimer = 0;
    this.wakeLock = null;
    this.disposed = false;
    this.onVisibility = () => {
      if (document.visibilityState === 'visible') {
        this.scheduleCameraCheck();
        if (this.connected) this.acquireWakeLock();
      }
    };
    document.addEventListener('visibilitychange', this.onVisibility);
  }

  // ---- reporting ----------------------------------------------------------------------------------------------

  invoke(method, ...args) {
    if (this.disposed) return;
    try {
      this.dotnet.invokeMethodAsync(method, ...args).catch(() => { });
    } catch (e) {
      // The .NET side is gone (page closing).
    }
  }

  state(value, detail) {
    this.invoke('OnEngineState', value, detail == null ? null : String(detail));
  }

  notice(kind, detail) {
    this.invoke('OnEngineNotice', kind, detail == null ? null : String(detail));
  }

  localInfo() {
    const S = this.LK && this.LK.Track.Source;
    const lp = this.connected && this.room ? this.room.localParticipant : null;
    if (!lp || !S) {
      return {
        micEnabled: this.wantMic, cameraEnabled: this.wantCamera && (!!this.prepared || !this.LK), screenSharing: false,
        speaking: false, quality: -1, facing: this.facing, micPublished: false,
      };
    }
    const mic = lp.getTrackPublication(S.Microphone);
    const cam = lp.getTrackPublication(S.Camera);
    const screen = lp.getTrackPublication(S.ScreenShare);
    return {
      micEnabled: !!(mic && mic.track && !mic.isMuted),
      cameraEnabled: !!(cam && cam.track && !cam.isMuted),
      screenSharing: !!(screen && screen.track),
      speaking: !!lp.isSpeaking,
      quality: qualityOf(lp.connectionQuality),
      facing: this.facing,
      micPublished: !!(mic && mic.track),
    };
  }

  localChanged() {
    const info = this.localInfo();
    const json = JSON.stringify(info);
    if (json === this.lastLocal) return;
    this.lastLocal = json;
    this.invoke('OnLocalMedia', info);
  }

  /** A remote participant that is another device of the signed-in user (same account, same room). */
  isMine(participant) {
    return !!(participant && this.me && userIdOf(participant) === this.me);
  }

  collectParticipants() {
    const S = this.LK.Track.Source;
    const people = new Map();
    for (const p of this.room.remoteParticipants.values()) {
      const uid = userIdOf(p);
      if (!uid || uid === this.me) continue; // my other device in the same room
      const cam = p.getTrackPublication(S.Camera);
      const screen = p.getTrackPublication(S.ScreenShare);
      const mic = p.getTrackPublication(S.Microphone);
      const info = {
        userId: uid,
        isSpeaking: !!p.isSpeaking,
        hasVideo: !!(cam && cam.isSubscribed && cam.track && !cam.isMuted),
        hasScreen: !!(screen && screen.isSubscribed && screen.track && !screen.isMuted),
        isMuted: !mic || mic.isMuted,
        quality: qualityOf(p.connectionQuality),
        isActiveSpeaker: false,
      };
      const prev = people.get(uid);
      people.set(uid, prev ? {
        userId: uid,
        isSpeaking: prev.isSpeaking || info.isSpeaking,
        hasVideo: prev.hasVideo || info.hasVideo,
        hasScreen: prev.hasScreen || info.hasScreen,
        isMuted: prev.isMuted && info.isMuted,
        quality: Math.max(prev.quality, info.quality),
        isActiveSpeaker: false,
      } : info);
    }
    const active = people.get(this.lastActive);
    if (active) active.isActiveSpeaker = true;
    return [...people.values()].sort((a, b) => (a.userId < b.userId ? -1 : a.userId > b.userId ? 1 : 0));
  }

  flushParticipants() {
    if (!this.room || !this.LK) return;
    const list = this.collectParticipants();
    const json = JSON.stringify(list);
    if (json === this.lastParticipants) return;
    this.lastParticipants = json;
    this.invoke('OnParticipants', list);
  }

  reportParticipants() {
    if (this.partTimer) {
      this.partDirty = true;
      return;
    }
    this.flushParticipants();
    this.partTimer = setTimeout(() => {
      this.partTimer = 0;
      if (this.partDirty) {
        this.partDirty = false;
        this.reportParticipants();
      }
    }, 200);
  }

  changed() {
    this.scheduleSlots();
    this.localChanged();
    this.reportParticipants();
  }

  // ---- connection ---------------------------------------------------------------------------------------------

  /**
   * options: { url, token, myUserId, micEnabled, cameraEnabled, mobile, lowData, quality ('auto'|'low'|'medium'|'high'),
   *            audio: { deviceId, echoCancellation, noiseSuppression, autoGainControl }, videoDeviceId, speakerId }
   */
  async connect(options) {
    const session = ++this.session;
    this.idle = false;
    await this.teardown();
    if (session !== this.session) return;
    this.connected = false;
    this.lastLocal = '';
    this.lastParticipants = '';
    this.lastActive = '';
    this.me = normId(options.myUserId);
    this.mobile = !!options.mobile;
    this.wantMic = options.micEnabled !== false;
    this.wantCamera = !!options.cameraEnabled;
    this.facing = 'user';
    this.speakerId = options.speakerId || null;
    this.videoDeviceId = options.videoDeviceId || null;
    const audio = options.audio || {};
    this.audioOptions = {
      echoCancellation: audio.echoCancellation !== false,
      noiseSuppression: audio.noiseSuppression !== false,
      autoGainControl: audio.autoGainControl !== false,
    };
    if (audio.deviceId) this.audioOptions.deviceId = audio.deviceId;
    this.state('connecting');

    let LK;
    try {
      LK = await loadLiveKit();
    } catch (e) {
      if (session === this.session) this.state('failed', 'library');
      return;
    }
    if (session !== this.session) return;
    this.LK = LK;
    this.preset = this.presetFor(options.quality, !!options.lowData);

    if (this.wantCamera) {
      await this.cameraWork(async () => {
        if (session !== this.session || !this.wantCamera || this.prepared) return;
        try {
          const track = await LK.createLocalVideoTrack(this.cameraOptions());
          if (session !== this.session || !this.wantCamera) {
            track.stop();
            return;
          }
          this.prepared = track;
          this.watchCamera(track);
        } catch (e) {
          if (session !== this.session) return;
          this.wantCamera = false;
          this.notice('deviceError', 'camera:' + errorCode(e));
        }
      });
      if (session !== this.session) return;
      this.localChanged();
      this.scheduleSlots();
    }

    const room = new LK.Room(this.roomOptions(options));
    this.room = room;
    this.wire(room, session);

    let lastError = null;
    for (let attempt = 0; attempt < 3; attempt++) {
      if (session !== this.session) return;
      try {
        await room.connect(options.url, options.token, { autoSubscribe: true });
        lastError = null;
        break;
      } catch (e) {
        lastError = e;
        if (session !== this.session) return;
        await delay(1500 * (attempt + 1));
      }
    }
    if (session !== this.session) return;
    if (lastError) {
      this.room = null;
      this.idle = true; // a camera toggle still in flight must not leave a preview open
      try { await room.disconnect(true); } catch (e) { /* never connected */ }
      this.stopPrepared();
      this.state('failed', (lastError && lastError.message) || 'connect');
      return;
    }

    this.connected = true;
    this.state('connected');
    this.acquireWakeLock();
    if (!room.canPlaybackAudio) this.notice('audioBlocked', 'true');

    const lp = room.localParticipant;
    // Queued behind a camera toggle that may still be opening the device: the preview is published only if the
    // camera is still wanted, and a toggle that runs after this sees the room connected and uses LiveKit directly.
    await this.cameraWork(async () => {
      const track = this.prepared;
      if (session !== this.session || !track) return;
      this.prepared = null;
      if (!this.wantCamera) {
        track.stop();
        return;
      }
      try {
        await lp.publishTrack(track, { source: LK.Track.Source.Camera });
      } catch (e) {
        track.stop();
        this.wantCamera = false;
      }
    });
    if (session !== this.session) return;
    if (this.wantMic) {
      try {
        await lp.setMicrophoneEnabled(true, this.audioOptions);
      } catch (e) {
        this.notice('deviceError', 'microphone:' + errorCode(e));
      }
    }
    if (session !== this.session) return;
    this.changed();
  }

  presetFor(quality, lowData) {
    const P = this.LK.VideoPresets;
    if (lowData) return { capture: P.h360, encoding: { maxBitrate: 300000, maxFramerate: 15 }, layers: [P.h180] };
    switch (quality) {
      case 'low': return { capture: P.h360, encoding: P.h360.encoding, layers: [P.h180] };
      case 'medium': return { capture: P.h540, encoding: P.h540.encoding, layers: [P.h180, P.h360] };
      case 'high': return this.mobile
        ? { capture: P.h720, encoding: P.h720.encoding, layers: [P.h180, P.h360] }
        : { capture: P.h1080, encoding: P.h1080.encoding, layers: [P.h360, P.h720] };
      default: return { capture: P.h720, encoding: P.h720.encoding, layers: [P.h180, P.h360] };
    }
  }

  roomOptions(options) {
    const LK = this.LK;
    const publish = {
      simulcast: true,
      videoSimulcastLayers: this.preset.layers,
      videoEncoding: this.preset.encoding,
      screenShareEncoding: LK.ScreenSharePresets.h1080fps15.encoding,
      dtx: true,
      red: !options.lowData,
    };
    if (options.lowData) publish.audioPreset = LK.AudioPresets.speech;
    const roomOptions = {
      adaptiveStream: true,
      dynacast: true,
      stopLocalTrackOnUnpublish: true,
      disconnectOnPageLeave: true,
      audioCaptureDefaults: { ...this.audioOptions },
      videoCaptureDefaults: this.cameraOptions(),
      publishDefaults: publish,
    };
    if (this.speakerId) roomOptions.audioOutput = { deviceId: this.speakerId };
    return roomOptions;
  }

  cameraOptions() {
    const options = { resolution: this.preset ? this.preset.capture.resolution : { width: 1280, height: 720 } };
    if (this.videoDeviceId) options.deviceId = this.videoDeviceId;
    else options.facingMode = this.facing || 'user';
    return options;
  }

  wire(room, session) {
    const E = this.LK.RoomEvent;
    const on = (event, handler) => room.on(event, (...args) => {
      if (session !== this.session || this.disposed) return;
      try { handler(...args); } catch (e) { /* never let a handler break LiveKit's emitter */ }
    });
    on(E.Reconnecting, () => this.state('reconnecting'));
    on(E.SignalReconnecting, () => this.state('reconnecting'));
    on(E.Reconnected, () => {
      if (!this.connected) return;
      this.state('connected');
      this.changed();
    });
    // A failed room.connect() attempt emits Disconnected as well: while connect() is still retrying, its loop owns
    // the outcome (another attempt, or 'failed' after the last one), so only a room that got connected ends here.
    on(E.Disconnected, (reason) => {
      if (this.connected) this.onDisconnected(reason);
    });
    on(E.ParticipantConnected, () => this.changed());
    on(E.ParticipantDisconnected, () => this.changed());
    on(E.TrackSubscribed, (track, publication, participant) => {
      if (this.isMine(participant)) {
        // My other device in the same room: playing it would echo my own voice back (and howl when both devices
        // are close), and nothing shows it. Stop receiving it at all.
        try { if (publication) publication.setSubscribed(false); } catch (e) { /* already gone */ }
        return;
      }
      if (track.kind === 'audio') this.attachAudio(track);
      this.changed();
    });
    on(E.TrackUnsubscribed, (track) => {
      if (track.kind === 'audio') this.detachAudio(track);
      this.changed();
    });
    on(E.TrackMuted, () => this.changed());
    on(E.TrackUnmuted, () => this.changed());
    on(E.TrackPublished, () => this.changed());
    on(E.TrackUnpublished, () => this.changed());
    on(E.TrackStreamStateChanged, () => this.scheduleSlots());
    on(E.ParticipantMetadataChanged, () => this.changed());
    on(E.ConnectionQualityChanged, () => this.changed());
    on(E.ActiveSpeakersChanged, (speakers) => {
      const top = (speakers || []).find((p) => !p.isLocal && !this.isMine(p));
      if (top) this.lastActive = userIdOf(top);
      this.changed();
    });
    on(E.LocalTrackPublished, (publication) => {
      const S = this.LK.Track.Source;
      if (publication.source === S.Microphone) this.notice('micPublished');
      if (publication.source === S.Camera && publication.track) this.watchCamera(publication.track);
      if (publication.source === S.ScreenShare && publication.track) this.watchScreen(publication.track);
      this.changed();
    });
    on(E.LocalTrackUnpublished, () => this.changed());
    on(E.AudioPlaybackStatusChanged, () => this.notice('audioBlocked', room.canPlaybackAudio ? 'false' : 'true'));
    // LiveKit emits this for a failed setXxxEnabled and throws the same error to the caller. Camera toggles and
    // restarts return it to .NET, which shows it (a notice would show it twice); a cancelled screen-share picker
    // comes with no kind and is reported by setScreenShare. Only the microphone warning is fed from here.
    on(E.MediaDevicesError, (error, kind) => {
      if (kind === 'audioinput') this.notice('deviceError', 'microphone:' + errorCode(error));
    });
    on(E.MediaDevicesChanged, () => this.notice('devicesChanged'));
  }

  /** The connected room is gone for good (LiveKit gave up reconnecting, was kicked, or the server ended it). */
  onDisconnected(reason) {
    this.connected = false;
    this.idle = true;
    this.room = null;
    this.cleanupMedia();
    this.releaseWakeLock();
    const R = this.LK && this.LK.DisconnectReason;
    if (R && reason === R.DUPLICATE_IDENTITY) {
      this.state('replaced');
      return;
    }
    this.state('disconnected', String(reason == null ? '' : reason));
  }

  async teardown() {
    const room = this.room;
    this.room = null;
    this.connected = false;
    this.stopPrepared();
    this.cleanupMedia();
    this.releaseWakeLock();
    clearTimeout(this.partTimer);
    this.partTimer = 0;
    this.partDirty = false;
    if (room) {
      try {
        await room.disconnect(true);
      } catch (e) {
        // Already gone.
      }
    }
  }

  /** Leaves the room and releases every device. The engine can connect again later. */
  async disconnect() {
    this.session++;
    this.idle = true;
    this.wantCamera = false;
    await this.teardown();
    this.lastLocal = '';
    this.lastParticipants = '';
  }

  async dispose() {
    await this.disconnect();
    this.disposed = true;
    document.removeEventListener('visibilitychange', this.onVisibility);
    clearTimeout(this.camTimer);
    if (this.observer) this.observer.disconnect();
    this.observer = null;
    this.stage = null;
    if (this.audioHost) this.audioHost.remove();
    this.audioHost = null;
    this.dotnet = null;
  }

  stopPrepared() {
    if (this.prepared) {
      try { this.prepared.stop(); } catch (e) { /* ignore */ }
      this.prepared = null;
    }
  }

  /**
   * Runs camera work one operation at a time (preview, publishing it, toggles, flips, restarts), so a toggle never
   * races a camera that is still opening: no second capture, no preview published after the camera was turned off.
   */
  cameraWork(work) {
    const run = this.cameraChain.then(() => work());
    this.cameraChain = run.catch(() => { });
    return run;
  }

  // ---- local media --------------------------------------------------------------------------------------------

  async setMicEnabled(enabled) {
    this.wantMic = !!enabled;
    const lp = this.connected && this.room ? this.room.localParticipant : null;
    if (!lp) {
      this.localChanged();
      return { ok: true, error: null };
    }
    try {
      await lp.setMicrophoneEnabled(this.wantMic, this.audioOptions);
      this.localChanged();
      return { ok: true, error: null };
    } catch (e) {
      this.localChanged();
      return { ok: false, error: errorCode(e) };
    }
  }

  setCameraEnabled(enabled) {
    return this.cameraWork(() => this.applyCamera(!!enabled));
  }

  async applyCamera(on) {
    // Queued behind a disconnect: the next connect() gets the camera state with its options.
    if (this.idle) return { ok: true, error: null };
    const lp = this.connected && this.room ? this.room.localParticipant : null;
    if (!lp) {
      // Not connected yet: prepare (or drop) the preview track that connect() publishes once connected (its
      // publishing step is queued behind this one).
      this.wantCamera = on;
      if (!on) this.stopPrepared();
      else if (!this.prepared && this.LK) {
        const session = this.session;
        try {
          const track = await this.LK.createLocalVideoTrack(this.cameraOptions());
          if (session !== this.session || this.idle || !this.wantCamera) {
            track.stop();
          } else {
            this.prepared = track;
            this.watchCamera(track);
          }
        } catch (e) {
          if (session === this.session) this.wantCamera = false;
          this.localChanged();
          return { ok: false, error: errorCode(e) };
        }
      }
      this.localChanged();
      this.scheduleSlots();
      return { ok: true, error: null };
    }
    try {
      await lp.setCameraEnabled(on, this.cameraOptions());
      this.wantCamera = on;
      this.changed();
      return { ok: true, error: null };
    } catch (e) {
      this.wantCamera = false;
      this.changed();
      return { ok: false, error: errorCode(e) };
    }
  }

  currentCameraTrack() {
    if (this.prepared) return this.prepared;
    const lp = this.connected && this.room ? this.room.localParticipant : null;
    const pub = lp && lp.getTrackPublication(this.LK.Track.Source.Camera);
    return pub && pub.track ? pub.track : null;
  }

  switchCamera() {
    return this.cameraWork(() => this.flipCamera());
  }

  async flipCamera() {
    const track = this.currentCameraTrack();
    if (!track) return { ok: false, error: 'off' };
    try {
      if (this.mobile) {
        this.facing = this.facing === 'environment' ? 'user' : 'environment';
        this.videoDeviceId = null;
        await track.restartTrack({ facingMode: this.facing, resolution: this.preset.capture.resolution });
      } else {
        const cameras = (await this.LK.Room.getLocalDevices('videoinput', false))
          .filter((d) => d.deviceId && !ALIASES.has(d.deviceId));
        if (cameras.length < 2) return { ok: false, error: 'single' };
        const current = await track.getDeviceId(true);
        const index = cameras.findIndex((c) => c.deviceId === current);
        const next = cameras[(index + 1) % cameras.length];
        if (this.connected && this.room && !this.prepared) await this.room.switchActiveDevice('videoinput', next.deviceId);
        else await track.setDeviceId(next.deviceId);
        this.videoDeviceId = next.deviceId;
        const guess = this.LK.facingModeFromDeviceLabel ? this.LK.facingModeFromDeviceLabel(next.label || '') : undefined;
        this.facing = guess && guess.facingMode === 'environment' ? 'environment' : 'user';
      }
      this.watchCamera(this.currentCameraTrack());
      this.changed();
      return { ok: true, error: null };
    } catch (e) {
      this.changed();
      return { ok: false, error: errorCode(e) };
    }
  }

  async setScreenShare(enabled) {
    const lp = this.connected && this.room ? this.room.localParticipant : null;
    if (!lp) return { ok: false, error: 'notConnected' };
    try {
      await lp.setScreenShareEnabled(!!enabled, {
        audio: false,
        contentHint: 'detail',
        selfBrowserSurface: 'exclude',
        surfaceSwitching: 'include',
        systemAudio: 'exclude',
      });
      this.changed();
      return { ok: true, error: null };
    } catch (e) {
      this.changed();
      const code = errorCode(e);
      return { ok: false, error: code === 'denied' ? 'cancelled' : code };
    }
  }

  /**
   * The browser's own "Stop sharing" ends the capture track. LiveKit unpublishes it by itself (LocalTrackUnpublished
   * updates the call screen); this makes sure nothing stays published and the screen hears about it either way.
   */
  watchScreen(track) {
    const mst = track && track.mediaStreamTrack;
    if (!mst || mst.__smScreenWatched) return;
    mst.__smScreenWatched = true;
    mst.addEventListener('ended', () => {
      setTimeout(async () => {
        if (this.disposed) return;
        const lp = this.connected && this.room ? this.room.localParticipant : null;
        const pub = lp && this.LK ? lp.getTrackPublication(this.LK.Track.Source.ScreenShare) : null;
        if (pub && pub.track && pub.track.mediaStreamTrack === mst) {
          try { await lp.setScreenShareEnabled(false); } catch (e) { /* already unpublished */ }
        }
        this.changed();
      }, 0);
    });
  }

  async setSpeaker(deviceId) {
    this.speakerId = deviceId || null;
    if (!this.room) return { ok: true, error: null };
    try {
      await this.room.switchActiveDevice('audiooutput', this.speakerId || 'default');
      return { ok: true, error: null };
    } catch (e) {
      return { ok: false, error: errorCode(e) };
    }
  }

  async setMicrophoneDevice(deviceId) {
    const previous = this.audioOptions.deviceId || null;
    const next = deviceId || null;
    if (next) this.audioOptions.deviceId = next;
    else delete this.audioOptions.deviceId;
    if (!this.room || !this.connected || next === previous) return { ok: true, error: null };
    try {
      // The system default is a preference, never an exact device: Firefox and Safari have no 'default' id, and an
      // exact constraint that cannot be met would leave the microphone stopped (and every later restart failing).
      await this.room.switchActiveDevice('audioinput', next || 'default', !!next);
      return { ok: true, error: null };
    } catch (e) {
      // The failed switch already stopped the old capture: get a microphone back so the call does not go silent.
      if (previous) this.audioOptions.deviceId = previous;
      else delete this.audioOptions.deviceId;
      try {
        if (this.room) await this.room.switchActiveDevice('audioinput', previous || 'default', false);
      } catch (e2) {
        // No microphone at all; the next mic toggle reports it.
      }
      this.localChanged();
      return { ok: false, error: errorCode(e) };
    }
  }

  /** Browsers may block autoplay until a user gesture; call from a click. */
  async startAudio() {
    if (!this.room) return true;
    try {
      await this.room.startAudio();
    } catch (e) {
      // Still blocked.
    }
    const ok = this.room ? this.room.canPlaybackAudio : true;
    this.notice('audioBlocked', ok ? 'false' : 'true');
    return ok;
  }

  // ---- camera after background --------------------------------------------------------------------------------

  watchCamera(track) {
    const mst = track && track.mediaStreamTrack;
    if (!mst || mst.__smWatched) return;
    mst.__smWatched = true;
    const check = () => this.scheduleCameraCheck();
    mst.addEventListener('ended', check);
    mst.addEventListener('mute', check);
  }

  scheduleCameraCheck() {
    clearTimeout(this.camTimer);
    // Give LiveKit a moment: it re-acquires some tracks by itself when the page becomes visible again.
    this.camTimer = setTimeout(() => this.checkCamera(true), 1500);
  }

  /** True when the camera should be on but its track ended or went silent (app was in background). */
  checkCamera(notify) {
    if (!this.wantCamera || document.visibilityState !== 'visible' || !this.LK) return false;
    const track = this.currentCameraTrack();
    const mst = track && track.mediaStreamTrack;
    const broken = !mst || mst.readyState === 'ended' || mst.muted;
    if (broken && notify) this.notice('cameraInterrupted');
    return broken;
  }

  restartCamera() {
    return this.cameraWork(() => this.reopenCamera());
  }

  async reopenCamera() {
    const track = this.currentCameraTrack();
    try {
      if (track) await track.restartTrack(this.cameraOptions());
      else if (this.connected && this.room) await this.room.localParticipant.setCameraEnabled(true, this.cameraOptions());
      this.wantCamera = true;
      this.watchCamera(this.currentCameraTrack());
      this.changed();
      return { ok: true, error: null };
    } catch (e) {
      try {
        if (this.connected && this.room) {
          const lp = this.room.localParticipant;
          await lp.setCameraEnabled(false);
          await lp.setCameraEnabled(true, this.cameraOptions());
          this.wantCamera = true;
          this.watchCamera(this.currentCameraTrack());
          this.changed();
          return { ok: true, error: null };
        }
      } catch (e2) {
        this.changed();
        return { ok: false, error: errorCode(e2) };
      }
      this.changed();
      return { ok: false, error: errorCode(e) };
    }
  }

  // ---- audio --------------------------------------------------------------------------------------------------

  ensureAudioHost() {
    if (!this.audioHost) {
      const host = document.createElement('div');
      host.className = 'sm-call-audio';
      host.hidden = true;
      host.setAttribute('aria-hidden', 'true');
      document.body.appendChild(host);
      this.audioHost = host;
    }
    return this.audioHost;
  }

  attachAudio(track) {
    const element = track.attach();
    element.autoplay = true;
    // Nothing here pauses call audio. When something else does (an audio focus change on a phone), play on — at
    // most once a second, so a system that insists is not fought in a loop.
    let resumedAt = 0;
    element.addEventListener('pause', () => {
      if (this.disposed || !this.connected || !element.isConnected || performance.now() - resumedAt < 1000) return;
      resumedAt = performance.now();
      const playing = element.play();
      if (playing && playing.catch) playing.catch(() => { });
    });
    this.ensureAudioHost().appendChild(element);
  }

  detachAudio(track) {
    try {
      for (const element of track.detach()) element.remove();
    } catch (e) {
      // Track already released.
    }
  }

  cleanupMedia() {
    for (const [slot, info] of this.slots) this.unbindSlot(slot, info);
    this.slots.clear();
    if (this.audioHost) this.audioHost.replaceChildren();
  }

  // ---- video slots --------------------------------------------------------------------------------------------

  /** Binds (or unbinds with null) the element that contains the [data-slot] and [data-pip] elements. */
  setStage(element) {
    if (this.stage !== element) {
      if (this.observer) this.observer.disconnect();
      this.observer = null;
      this.stage = element || null;
      if (this.stage) {
        this.observer = new MutationObserver(() => this.scheduleSlots());
        this.observer.observe(this.stage, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-slot'] });
      }
    }
    this.refreshSlots();
  }

  scheduleSlots() {
    if (this.slotRaf) return;
    this.slotRaf = requestAnimationFrame(() => {
      this.slotRaf = 0;
      this.refreshSlots();
    });
  }

  trackFor(key) {
    if (!this.LK) return null;
    const S = this.LK.Track.Source;
    if (key === 'local-camera') {
      if (this.prepared) return this.prepared;
      const lp = this.connected && this.room ? this.room.localParticipant : null;
      const pub = lp && lp.getTrackPublication(S.Camera);
      return pub && pub.track && !pub.isMuted ? pub.track : null;
    }
    if (key === 'local-screen') {
      const lp = this.connected && this.room ? this.room.localParticipant : null;
      const pub = lp && lp.getTrackPublication(S.ScreenShare);
      return pub && pub.track ? pub.track : null;
    }
    if (!this.room) return null;
    const colon = key.indexOf(':');
    if (colon < 0) return null;
    const source = key.slice(0, colon) === 'screen' ? S.ScreenShare : S.Camera;
    const uid = normId(key.slice(colon + 1));
    for (const p of this.room.remoteParticipants.values()) {
      if (userIdOf(p) !== uid) continue;
      const pub = p.getTrackPublication(source);
      if (pub && pub.isSubscribed && pub.track && !pub.isMuted) return pub.track;
    }
    return null;
  }

  unbindSlot(slot, info) {
    try { info.track.detach(info.video); } catch (e) { /* already detached */ }
    info.video.remove();
    this.slots.delete(slot);
  }

  refreshSlots() {
    for (const [slot, info] of [...this.slots]) {
      if (!slot.isConnected || !this.stage || !this.stage.contains(slot) || slot.dataset.slot !== info.key) this.unbindSlot(slot, info);
    }
    if (!this.stage) return;
    for (const slot of this.stage.querySelectorAll('[data-slot]')) {
      const key = slot.dataset.slot;
      const track = this.trackFor(key);
      const info = this.slots.get(slot);
      if (info && info.track === track) continue;
      if (info) this.unbindSlot(slot, info);
      if (!track) continue;
      const video = document.createElement('video');
      video.className = 'sm-call-video';
      video.autoplay = true;
      video.muted = true;
      video.playsInline = true;
      video.setAttribute('playsinline', '');
      // Created outside Razor, so scoped CSS cannot reach it: style it inline. Screens are letterboxed, faces fill.
      const fit = slot.dataset.fit || (key.indexOf('screen') >= 0 ? 'contain' : 'cover');
      video.style.cssText = `position:absolute;inset:0;width:100%;height:100%;object-fit:${fit};display:block;background:transparent;`;
      slot.appendChild(video);
      track.attach(video);
      this.slots.set(slot, { key, track, video });
    }
    for (const pip of this.stage.querySelectorAll('[data-pip]')) this.bindPip(pip);
  }

  bindPip(element) {
    if (this.pips.has(element)) return;
    this.pips.add(element);
    element.style.touchAction = 'none';
    let drag = null;
    let settle = null;
    element.addEventListener('pointerdown', (e) => {
      if (e.button !== undefined && e.button !== 0) return;
      const box = element.parentElement && element.parentElement.getBoundingClientRect();
      if (!box) return;
      // Caught while still springing into a corner: carry on from where it is on screen.
      const base = settle ? shownOffset(element) : { x: 0, y: 0 };
      if (settle) {
        settle.cancel();
        settle = null;
      }
      element.style.transform = base.x || base.y ? `translate3d(${base.x}px, ${base.y}px, 0)` : '';
      drag = { id: e.pointerId, x: e.clientX, y: e.clientY, moved: false, rect: element.getBoundingClientRect(), box, base, samples: [] };
      pushSample(drag.samples, e);
      try { element.setPointerCapture(e.pointerId); } catch (err) { /* ignore */ }
    });
    element.addEventListener('pointermove', (e) => {
      if (!drag || e.pointerId !== drag.id) return;
      const dx = e.clientX - drag.x;
      const dy = e.clientY - drag.y;
      if (!drag.moved && Math.hypot(dx, dy) < 6) return;
      if (!drag.moved) {
        drag.moved = true;
        element.classList.add('is-dragging');
      }
      pushSample(drag.samples, e);
      const { rect, box, base } = drag;
      const x = Math.max(box.left - rect.left, Math.min(box.right - rect.right, dx));
      const y = Math.max(box.top - rect.top, Math.min(box.bottom - rect.bottom, dy));
      element.style.transform = `translate3d(${base.x + x}px, ${base.y + y}px, 0)`;
      e.preventDefault();
    });
    const end = (e) => {
      if (!drag || e.pointerId !== drag.id) return;
      const current = drag;
      drag = null;
      try { element.releasePointerCapture(e.pointerId); } catch (err) { /* ignore */ }
      if (!current.moved) {
        if (current.base.x || current.base.y) this.snapPip(element, current.box, { x: 0, y: 0 }, (a) => { settle = a; });
        return;
      }
      element.classList.remove('is-dragging');
      const velocity = e.type === 'pointerup' ? velocityOf(current.samples) : { x: 0, y: 0 };
      this.snapPip(element, current.box, velocity, (a) => { settle = a; });
      swallowNextClick(element);
    };
    element.addEventListener('pointerup', end);
    element.addEventListener('pointercancel', end);
  }

  /**
   * Moves a released picture-in-picture to the nearest corner (where a fling was heading, not just where the finger
   * let go) and springs it there from its current position, carrying the fling's speed.
   */
  snapPip(element, box, velocity, setSettle) {
    const before = element.getBoundingClientRect();
    const cx = before.left + before.width / 2 + velocity.x * 140;
    const cy = before.top + before.height / 2 + velocity.y * 140;
    element.dataset.corner = (cy < box.top + box.height / 2 ? 't' : 'b') + (cx < box.left + box.width / 2 ? 'l' : 'r');
    element.style.transform = '';
    const after = element.getBoundingClientRect();
    const from = [before.left - after.left, before.top - after.top];
    if (reduceMotion() || typeof element.animate !== 'function' || (!from[0] && !from[1])) {
      setSettle(null);
      return;
    }
    const { frames, duration } = springFrames(from, [0, 0], [velocity.x, velocity.y]);
    const animation = element.animate(frames.map((p) => ({ transform: `translate3d(${p[0]}px, ${p[1]}px, 0)` })), { duration, easing: 'linear' });
    setSettle(animation);
    const done = () => setSettle(null);
    animation.onfinish = done;
  }

  // ---- screen wake lock (browsers; the native apps keep the screen on themselves) --------------------------------

  async acquireWakeLock() {
    if (this.wakeLock || !navigator.wakeLock || document.visibilityState !== 'visible') return;
    try {
      const lock = await navigator.wakeLock.request('screen');
      if (!this.connected) {
        lock.release().catch(() => { });
        return;
      }
      this.wakeLock = lock;
      lock.addEventListener('release', () => {
        if (this.wakeLock === lock) this.wakeLock = null;
      });
    } catch (e) {
      // Not allowed here (battery saver, insecure context…).
    }
  }

  releaseWakeLock() {
    const lock = this.wakeLock;
    this.wakeLock = null;
    if (lock) lock.release().catch(() => { });
  }
}

// ---- Expanded call screen: drag down to minimize ------------------------------------------------------------------

const AXIS_SLOP = 8;

function noop() { }

/**
 * Drag down on the expanded call screen (touch or pen; not on buttons, the picture-in-picture, menus or anything marked
 * [data-no-minimize]): the screen follows the finger, shrinking toward the top with rounded corners while `backdrop` (a
 * dark layer between the screen and the app) thins out so the app shows through. Released past the commit point or
 * flung down, it collapses toward the call bar at the top and .NET hears OnMinimizeGesture(); otherwise it springs
 * back. OnMinimizeArmed() fires when the drag crosses the commit point (haptic). collapse() plays the same collapse for
 * the minimize button and Back and resolves when it is done. With reduced motion the screen still follows the finger,
 * it just does not animate afterwards.
 */
export function attachMinimize(screen, backdrop, dotnetRef) {
  if (!screen) return { collapse: () => Promise.resolve(true), dispose: noop };
  let drag = null;
  let settling = null;   // { card, dim }: a spring back in progress
  let collapsing = null; // promise of the collapse (kept: the screen goes away right after it)
  let safety = 0;
  let disposed = false;

  const invoke = (method) => {
    if (disposed || !dotnetRef) return;
    try {
      dotnetRef.invokeMethodAsync(method).catch(noop);
    } catch (e) {
      // The .NET side is gone.
    }
  };
  const height = () => window.innerHeight || screen.clientHeight || 800;
  const commitAt = () => Math.max(96, height() * 0.18);

  // Pull distance -> look: follows the finger (with resistance past 45% of the height), shrinks by up to 12% toward
  // the top, the backdrop thins out to let the app show through.
  function pose(pull) {
    const h = height();
    const down = Math.max(0, pull);
    const y = pull <= h * 0.45 ? pull : h * 0.45 + (pull - h * 0.45) * 0.4;
    return { y, s: 1 - 0.12 * Math.min(1, down / (h * 0.5)), dim: 1 - 0.65 * Math.min(1, down / (h * 0.45)) };
  }

  const cardTransform = (p) => `translate3d(0, ${p.y.toFixed(2)}px, 0) scale(${p.s.toFixed(4)})`;

  function show(p) {
    screen.style.transform = cardTransform(p);
    if (backdrop) backdrop.style.opacity = p.dim.toFixed(3);
  }

  function lift() {
    screen.classList.add('is-dragged');
    if (backdrop) backdrop.classList.add('is-shown');
  }

  function rest() {
    screen.classList.remove('is-dragged');
    screen.style.transform = '';
    if (backdrop) {
      backdrop.classList.remove('is-shown');
      backdrop.style.opacity = '';
    }
  }

  function stopSettling() {
    if (!settling) return 0;
    const pull = shownOffset(screen).y;
    settling.card.cancel();
    if (settling.dim) settling.dim.cancel();
    settling = null;
    return pull;
  }

  function springBack(pull, velocity) {
    if (reduceMotion() || typeof screen.animate !== 'function') {
      rest();
      return;
    }
    const { frames, duration } = springFrames([pull], [0], [velocity]);
    const poses = frames.map((f) => pose(f[0]));
    show(pose(0));
    const card = screen.animate(poses.map((p) => ({ transform: cardTransform(p) })), { duration, easing: 'linear' });
    const dim = backdrop ? backdrop.animate(poses.map((p) => ({ opacity: p.dim })), { duration, easing: 'linear' }) : null;
    const current = { card, dim };
    settling = current;
    card.onfinish = () => {
      if (settling !== current) return;
      settling = null;
      rest();
    };
  }

  function collapseFrom(pull) {
    if (collapsing) return collapsing;
    if (reduceMotion() || typeof screen.animate !== 'function' || !screen.isConnected) {
      collapsing = Promise.resolve(true);
      return collapsing;
    }
    const from = pose(pull);
    lift();
    show(from);
    // Toward the call bar at the top: shrinks into the top edge, fading out over the second half.
    const card = screen.animate([{ transform: cardTransform(from) }, { transform: 'translate3d(0, 0px, 0) scale(0.32)' }],
      { duration: 220, easing: 'cubic-bezier(0.4, 0, 1, 1)', fill: 'forwards' });
    const fade = screen.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 130, delay: 90, easing: 'linear', fill: 'forwards' });
    const dim = backdrop ? backdrop.animate([{ opacity: from.dim }, { opacity: 0 }], { duration: 220, easing: 'cubic-bezier(0.2, 0.8, 0.2, 1)', fill: 'forwards' }) : null;
    collapsing = new Promise((resolve) => {
      card.onfinish = () => resolve(true);
      card.oncancel = () => resolve(false);
      // Animations stall in a hidden page: never keep .NET waiting for this one.
      setTimeout(() => resolve(true), 400);
    });
    // .NET removes the screen right after; should that not happen, make it usable again.
    safety = setTimeout(() => {
      if (disposed || !screen.isConnected) return;
      card.cancel();
      fade.cancel();
      if (dim) dim.cancel();
      rest();
      collapsing = null;
    }, 1500);
    return collapsing;
  }

  const onDown = (e) => {
    if (disposed || drag || collapsing || !e.isPrimary) return;
    if (e.pointerType === 'mouse' || (e.button !== undefined && e.button !== 0)) return;
    const t = e.target;
    if (!t || !t.closest || t.closest('button, a, input, textarea, select, [role="button"], [data-pip], [data-no-minimize], .sm-overlay, .sm-menu-layer')) return;
    const base = stopSettling();
    drag = { id: e.pointerId, x0: e.clientX, y0: e.clientY, base, claimed: false, armed: false, samples: [] };
    pushSample(drag.samples, e);
    // Caught while springing back: it is already a card under the finger (or, nearly home, the full screen again).
    if (base > 0) claim(e);
    else rest();
  };

  function claim(e) {
    drag.claimed = true;
    try { screen.setPointerCapture(e.pointerId); } catch (err) { /* ignore */ }
    lift();
    show(pose(drag.base));
  }

  const onMove = (e) => {
    if (!drag || e.pointerId !== drag.id) return;
    pushSample(drag.samples, e);
    const dx = e.clientX - drag.x0;
    const dy = e.clientY - drag.y0;
    if (!drag.claimed) {
      if (Math.abs(dx) < AXIS_SLOP && Math.abs(dy) < AXIS_SLOP) return;
      // Only a downward, mostly vertical drag minimizes; anything else is not ours.
      if (dy <= 0 || Math.abs(dx) >= Math.abs(dy)) {
        drag = null;
        return;
      }
      claim(e);
    }
    const pull = drag.base + dy;
    show(pose(pull));
    const armed = pull > commitAt();
    if (armed !== drag.armed) {
      drag.armed = armed;
      if (armed) invoke('OnMinimizeArmed');
    }
    e.preventDefault();
  };

  const onUp = (e) => {
    if (!drag || e.pointerId !== drag.id) return;
    const current = drag;
    drag = null;
    if (!current.claimed) return;
    try { screen.releasePointerCapture(e.pointerId); } catch (err) { /* ignore */ }
    swallowNextClick(screen);
    const pull = current.base + (e.clientY - current.y0);
    const v = e.type === 'pointerup' ? velocityOf(current.samples).y : 0;
    if (e.type === 'pointerup' && (pull > commitAt() || (v > 0.5 && pull > 24))) {
      collapseFrom(pull).then((done) => {
        if (done) invoke('OnMinimizeGesture');
      });
    } else {
      springBack(pull, v);
    }
  };

  screen.addEventListener('pointerdown', onDown);
  screen.addEventListener('pointermove', onMove);
  screen.addEventListener('pointerup', onUp);
  screen.addEventListener('pointercancel', onUp);

  return {
    /** Plays the collapse (minimize button, Back); resolves when the screen can go. */
    collapse() {
      if (disposed) return Promise.resolve(true);
      drag = null;
      // From wherever a spring back has got to.
      return collapseFrom(Math.max(0, stopSettling()));
    },
    dispose() {
      disposed = true;
      drag = null;
      clearTimeout(safety);
      screen.removeEventListener('pointerdown', onDown);
      screen.removeEventListener('pointermove', onMove);
      screen.removeEventListener('pointerup', onUp);
      screen.removeEventListener('pointercancel', onUp);
      // A finished collapse holds the screen invisible until .NET removes it; only a spring back is undone.
      if (settling) {
        stopSettling();
        rest();
      }
    }
  };
}
