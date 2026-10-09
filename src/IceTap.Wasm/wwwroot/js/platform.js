const keyToHole = {
  Digit1: 0,
  Numpad1: 0,
  Digit2: 1,
  Numpad2: 1,
  Digit3: 2,
  Numpad3: 2,
  Digit4: 3,
  Numpad4: 3,
  Digit5: 4,
  Numpad5: 4,
  Digit6: 5,
  Numpad6: 5,
  Digit7: 6,
  Numpad7: 6,
  Digit8: 7,
  Numpad8: 7,
  Digit9: 8,
  Numpad9: 8,
};

const saveKey = "ice-tap-v1";

let dotNet = null;
let phase = "title";
let looping = false;
let raf = 0;
let deferred = null;
let onKey = null;
let onHide = null;
let onInstall = null;
let onInstalled = null;

let audioCtx = null;
let master = null;
let noise = null;
let soundOn = true;

export function probe() {
  const appleTouch =
    /iphone|ipad|ipod/i.test(navigator.userAgent) ||
    (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
  const standalone =
    window.matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;
  return {
    appleTouch,
    standalone,
    reducedMotion: window.matchMedia("(prefers-reduced-motion: reduce)").matches,
  };
}

export function loadSave() {
  try {
    return localStorage.getItem(saveKey);
  } catch {
    return null;
  }
}

export function storeSave(json) {
  localStorage.setItem(saveKey, json);
}

export function now() {
  return performance.now();
}

export function bind(ref) {
  dotNet = ref;
  onKey = (event) => {
    if (event.repeat || !dotNet) return;
    const hole = keyToHole[event.code];
    if (hole !== undefined) {
      if (phase !== "playing") return;
      event.preventDefault();
      dotNet.invokeMethod("PressHole", hole);
      return;
    }
    if (event.key !== "Enter" && event.key !== " ") return;
    if (event.target instanceof HTMLButtonElement) return;
    if (phase === "title") {
      event.preventDefault();
      dotNet.invokeMethod("KeyBegin", "timed");
    } else if (phase === "over") {
      event.preventDefault();
      dotNet.invokeMethod("KeyBegin", "same");
    } else if (phase === "paused") {
      event.preventDefault();
      dotNet.invokeMethod("ResumeRound");
    }
  };
  onHide = () => {
    if (document.hidden && dotNet) dotNet.invokeMethod("PauseFromHide");
  };
  onInstall = (event) => {
    event.preventDefault();
    deferred = event;
    if (dotNet) dotNet.invokeMethod("InstallReady");
  };
  onInstalled = () => {
    deferred = null;
    if (dotNet) dotNet.invokeMethod("Installed");
  };
  document.addEventListener("keydown", onKey);
  document.addEventListener("visibilitychange", onHide);
  window.addEventListener("beforeinstallprompt", onInstall);
  window.addEventListener("appinstalled", onInstalled);
}

export function unbind() {
  if (onKey) document.removeEventListener("keydown", onKey);
  if (onHide) document.removeEventListener("visibilitychange", onHide);
  if (onInstall) window.removeEventListener("beforeinstallprompt", onInstall);
  if (onInstalled) window.removeEventListener("appinstalled", onInstalled);
  onKey = null;
  onHide = null;
  onInstall = null;
  onInstalled = null;
  dotNet = null;
  stopLoop();
}

export function setPhase(next) {
  phase = next;
}

export function startLoop() {
  stopLoop();
  if (!dotNet) return;
  looping = true;
  const step = (time) => {
    if (!looping || !dotNet) return;
    dotNet.invokeMethodAsync("Frame", time).then(
      () => {
        if (looping) raf = requestAnimationFrame(step);
      },
      (error) => {
        console.error(error);
        looping = false;
      },
    );
  };
  raf = requestAnimationFrame(step);
}

export function stopLoop() {
  looping = false;
  cancelAnimationFrame(raf);
}

export function blurActive() {
  const el = document.activeElement;
  if (el instanceof HTMLElement) el.blur();
}

export function promptInstall() {
  if (!deferred) return;
  const prompt = deferred;
  deferred = null;
  void prompt.prompt().then(() => prompt.userChoice);
}

export function vibrate(pattern) {
  if (typeof navigator.vibrate !== "function") return;
  navigator.vibrate(pattern);
}

export function setSound(enabled) {
  soundOn = enabled;
}

export function unlock() {
  const audio = context();
  if (audio && audio.state === "suspended") void audio.resume();
}

export function blip() {
  tone(990, 0, 0.05, "sine", 0.04);
}

export function go() {
  tone(392, 0, 0.09, "sine", 0.05);
  tone(523, 0.07, 0.1, "sine", 0.05);
  tone(659, 0.14, 0.12, "sine", 0.045);
}

export function catchFish(species) {
  if (species === "pike") {
    burst(0, 0.08, 0.1, 1700);
    [523, 659, 784, 1046].forEach((freq, index) => tone(freq, index * 0.048, 0.12, "sine", 0.05));
    return;
  }
  if (species === "char") {
    burst(0, 0.07, 0.12, 1200);
    tone(660, 0, 0.09, "sine", 0.05);
    tone(880, 0.04, 0.1, "sine", 0.045);
    return;
  }
  if (species === "trout") {
    burst(0, 0.07, 0.14, 900);
    tone(740, 0, 0.1, "sine", 0.06, 480);
    return;
  }
  burst(0, 0.07, 0.15, 700);
  tone(620, 0, 0.1, "sine", 0.06, 380);
}

export function hazard() {
  burst(0, 0.12, 0.2, 180);
  tone(140, 0, 0.16, "square", 0.04, 70);
}

export function dodge() {
  tone(1560, 0, 0.04, "sine", 0.02);
}

export function escape() {
  tone(360, 0, 0.12, "triangle", 0.04, 160);
}

export function milestone() {
  tone(880, 0, 0.12, "sine", 0.05);
  tone(1320, 0, 0.14, "sine", 0.04);
}

export function over() {
  tone(494, 0, 0.14, "triangle", 0.045);
  tone(392, 0.09, 0.16, "triangle", 0.04);
  tone(262, 0.18, 0.2, "triangle", 0.04);
}

function context() {
  if (audioCtx) return audioCtx;
  const Ctor = window.AudioContext || window.webkitAudioContext;
  if (!Ctor) return null;
  try {
    audioCtx = new Ctor();
  } catch {
    return null;
  }
  master = audioCtx.createGain();
  master.gain.value = 0.72;
  master.connect(audioCtx.destination);
  return audioCtx;
}

function output() {
  if (!soundOn) return null;
  const audio = context();
  if (!audio || !master) return null;
  return { ctx: audio, master };
}

function tone(freq, delay, duration, type, peak, slide) {
  const bus = output();
  if (!bus) return;
  const t0 = bus.ctx.currentTime + delay;
  const osc = bus.ctx.createOscillator();
  osc.type = type;
  osc.frequency.setValueAtTime(freq, t0);
  if (slide) osc.frequency.exponentialRampToValueAtTime(Math.max(40, slide), t0 + duration);
  const gain = bus.ctx.createGain();
  gain.gain.setValueAtTime(0.0001, t0);
  gain.gain.exponentialRampToValueAtTime(peak, t0 + Math.min(0.012, duration * 0.4));
  gain.gain.exponentialRampToValueAtTime(0.0001, t0 + duration);
  osc.connect(gain);
  gain.connect(bus.master);
  osc.start(t0);
  osc.stop(t0 + duration + 0.02);
}

function burst(delay, duration, peak, frequency) {
  const bus = output();
  if (!bus) return;
  if (!noise || noise.sampleRate !== bus.ctx.sampleRate) {
    noise = bus.ctx.createBuffer(1, Math.floor(bus.ctx.sampleRate * 0.25), bus.ctx.sampleRate);
    const data = noise.getChannelData(0);
    for (let i = 0; i < data.length; i += 1) data[i] = Math.random() * 2 - 1;
  }
  const src = bus.ctx.createBufferSource();
  src.buffer = noise;
  const filter = bus.ctx.createBiquadFilter();
  filter.type = "bandpass";
  filter.frequency.value = frequency;
  filter.Q.value = 0.7;
  const gain = bus.ctx.createGain();
  const t0 = bus.ctx.currentTime + delay;
  gain.gain.setValueAtTime(0.0001, t0);
  gain.gain.exponentialRampToValueAtTime(peak, t0 + 0.008);
  gain.gain.exponentialRampToValueAtTime(0.0001, t0 + duration);
  src.connect(filter);
  filter.connect(gain);
  gain.connect(bus.master);
  src.start(t0);
  src.stop(t0 + duration + 0.02);
}
