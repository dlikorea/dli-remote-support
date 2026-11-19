"use strict";

const form = document.querySelector("#join-form");
const codeField = document.querySelector("#code");
const keyField = document.querySelector("#key");
const joinButton = document.querySelector("#join-button");
const endButton = document.querySelector("#end-button");
const sessionInfo = document.querySelector("#session-info");
const connectedCode = document.querySelector("#connected-code");
const statusLabel = document.querySelector("#status-label");
const statusDetail = document.querySelector("#status-detail");
const screenMode = document.querySelector("#screen-mode");
const emptyScreen = document.querySelector("#empty-screen");
const emptyTitle = document.querySelector("#empty-title");
const emptyDetail = document.querySelector("#empty-detail");
const stage = document.querySelector("#stage");
const remoteImage = document.querySelector("#remote-image");
const keyboard = document.querySelector("#remote-keyboard");
const screenPanel = document.querySelector("#screen-panel");
const monitorSelect = document.querySelector("#monitor-select");
const fullscreenButton = document.querySelector("#fullscreen-button");
const inputHelp = document.querySelector("#input-help");
const filePanel = document.querySelector("#file-panel");
const fileForm = document.querySelector("#file-form");
const filePicker = document.querySelector("#file-picker");
const sendFile = document.querySelector("#send-file");
const emptyFiles = document.querySelector("#empty-files");
const fileList = document.querySelector("#file-list");
const fileStatus = document.querySelector("#file-status");
const memoPanel = document.querySelector("#memo-panel");
const memoForm = document.querySelector("#memo-form");
const memoField = document.querySelector("#memo");
const memoButton = document.querySelector("#memo-button");
const memoStatus = document.querySelector("#memo-status");
const fileBrowser = document.querySelector("#file-browser");
const browserUnavailable = document.querySelector("#browser-unavailable");
const browseRoot = document.querySelector("#file-root");
const browseOpen = document.querySelector("#browse-open");
const browseUp = document.querySelector("#browse-up");
const browseDrives = document.querySelector("#browse-drives");
const driveOptions = document.querySelector("#customer-drives");
const browseList = document.querySelector("#browse-list");
const browsePath = document.querySelector("#browse-path");

const framesStaleAfter = 2500;
const stateStaleAfter = 3200;
const fileLimit = 20 * 1024 * 1024;
const keyboardKeys = {
  Backspace: 0x08, Tab: 0x09, Enter: 0x0d, CapsLock: 0x14, Escape: 0x1b,
  Space: 0x20, HangulMode: 0x15, HanjaMode: 0x19, Lang1: 0x15, Lang2: 0x19,
  ShiftLeft: 0xa0, ShiftRight: 0xa1, ControlLeft: 0xa2, AltLeft: 0xa4,
  Semicolon: 0xba, Equal: 0xbb, Comma: 0xbc, Minus: 0xbd, Period: 0xbe,
  Slash: 0xbf, Backquote: 0xc0, BracketLeft: 0xdb, Backslash: 0xdc,
  BracketRight: 0xdd, Quote: 0xde, IntlBackslash: 0xe2,
  NumpadMultiply: 0x6a, NumpadAdd: 0x6b, NumpadSubtract: 0x6d,
  NumpadDecimal: 0x6e,
};
const extendedKeys = {
  PageUp: 0x21, PageDown: 0x22, End: 0x23, Home: 0x24,
  ArrowLeft: 0x25, ArrowUp: 0x26, ArrowRight: 0x27, ArrowDown: 0x28,
  Insert: 0x2d, Delete: 0x2e, NumpadEnter: 0x0d, NumpadDivide: 0x6f,
  ControlRight: 0xa3, AltRight: 0xa5,
};
const pointerButtons = [[0, 1], [1, 4], [2, 2]];

let current = null;
let joining = false;
let closing = false;
let leaving = false;
let idleDetail = "접속 코드는 한 번만 사용할 수 있습니다.";
let idleError = false;

const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const displayCode = value => value.replace(/^(\d{4})(\d+)/, "$1 $2");

async function api(path, options = {}) {
  const headers = new Headers();
  if (options.token) headers.set("Authorization", `Bearer ${options.token}`);
  if (path === "join" && monitorSelect) headers.set("X-DLI-Monitor-Switch", "1");
  if (options.data) headers.set("Content-Type", "application/json");
  if (options.etag) headers.set("If-None-Match", options.etag);
  if (options.file) {
    headers.set("Content-Type", "application/octet-stream");
    headers.set("X-File-Name", encodeURIComponent(options.file.name));
  }
  const timeout = AbortSignal.timeout(options.timeout ?? 3500);
  const response = await fetch(`/api/operator/${path}`, {
    method: options.method ?? (options.data || options.file ? "POST" : "GET"),
    headers,
    body: options.data ? JSON.stringify(options.data) : options.file,
    cache: "no-store",
    signal: options.signal ? AbortSignal.any([timeout, options.signal]) : timeout,
  });
  if (!response.ok && response.status !== 304) {
    const payload = await response.json().catch(() => null);
    const message = typeof payload?.error === "string" && payload.error.length <= 160
      ? payload.error : "서버 요청을 처리할 수 없습니다.";
    throw Object.assign(new Error(message), { status: response.status });
  }
  return response;
}

function sessionEnded(error) {
  return [401, 404, 410].includes(error.status);
}

function canInput(session) {
  return current === session && session.phase === "controlling" && session.url !== ""
    && !session.switchingMonitor && session.frameMonitorRevision === session.monitorRevision
    && session.hasFrame && performance.now() - session.lastState < stateStaleAfter
    && performance.now() - session.lastFrame < framesStaleAfter && !document.hidden && document.hasFocus();
}

function setStatusDetail(message, error = false) {
  if (statusDetail.textContent !== message) statusDetail.textContent = message;
  statusDetail.dataset.error = String(error);
}

function render() {
  const session = current;
  const inputEnabled = session ? canInput(session) : false;
  form.hidden = Boolean(session);
  sessionInfo.hidden = !session;
  filePanel.hidden = !session;
  memoPanel.hidden = !session;
  memoButton.disabled = Boolean(session?.sendingMemo);
  fileBrowser.hidden = !session?.canBrowse;
  browserUnavailable.hidden = !session || session.canBrowse;
  browseDrives.hidden = !session?.canBrowseDrives;
  fullscreenButton.hidden = !session;
  if (monitorSelect) {
    monitorSelect.hidden = (session?.monitors.length ?? 0) < 2;
    monitorSelect.disabled = Boolean(session?.switchingMonitor);
  }
  codeField.disabled = joining || closing;
  keyField.disabled = joining || closing;
  joinButton.disabled = joining || closing;
  joinButton.textContent = joining ? "연결 중…" : "고객과 연결";
  endButton.disabled = closing;
  keyboard.tabIndex = inputEnabled ? 0 : -1;
  remoteImage.dataset.control = String(inputEnabled);
  const showImage = Boolean(session?.url);
  remoteImage.hidden = !showImage;
  emptyScreen.hidden = showImage;

  if (!session) {
    statusLabel.textContent = joining ? "연결 중" : closing ? "종료 중" : idleError ? "확인 필요" : "연결 전";
    statusLabel.dataset.tone = joining || closing ? "pending" : idleError ? "lost" : "idle";
    setStatusDetail(idleDetail, idleError);
    screenMode.textContent = "연결 대기";
    emptyTitle.textContent = "고객 연결 대기";
    emptyDetail.textContent = "고객에게 받은 접속 코드를 입력하세요.";
    inputHelp.textContent = "연결하면 고객의 기본 모니터가 표시됩니다.";
    return;
  }

  const interrupted = performance.now() - session.lastState >= stateStaleAfter
    || (showImage && performance.now() - session.lastFrame >= framesStaleAfter);
  connectedCode.textContent = displayCode(session.code);
  statusLabel.textContent = interrupted ? "연결 확인 중" : showImage ? "지원 중" : "연결 중";
  statusLabel.dataset.tone = inputEnabled ? "live" : "pending";
  setStatusDetail(interrupted ? "고객과 다시 통신할 때까지 입력을 일시 중지합니다."
    : showImage ? "지원이 끝나면 고객 연결을 종료하세요." : "고객의 화면이 준비되면 자동으로 표시됩니다.");
  const selected = session.monitors.findIndex(monitor => monitor.id === session.selectedMonitor);
  const monitorName = selected < 0 ? "기본 모니터" : `모니터 ${selected + 1}`;
  screenMode.textContent = session.monitorError || (session.switchingMonitor ? "모니터 전환 중"
    : interrupted ? "화면 갱신 대기" : showImage ? `${monitorName} · 실시간` : "화면 수신 중");
  emptyTitle.textContent = session.switchingMonitor ? "모니터 전환 중" : "고객 화면을 불러오는 중";
  emptyDetail.textContent = "잠시 기다려 주세요.";
  inputHelp.textContent = inputEnabled
    ? "화면을 클릭하면 키보드 입력이 전달됩니다. F8 키로 키보드 입력을 멈출 수 있습니다."
    : "화면이 준비되면 마우스와 키보드로 조작할 수 있습니다.";
}

function clearFrame(session) {
  remoteImage.removeAttribute("src");
  if (session.url) URL.revokeObjectURL(session.url);
  session.url = "";
  session.etag = "";
  session.lastFrame = 0;
  session.frameMonitorRevision = null;
}

function detach(session, message, error = false) {
  if (current !== session) return;
  current = null;
  session.abort.abort();
  clearLocalInput(session);
  clearFrame(session);
  clearInterval(session.inputTimer);
  filePicker.value = "";
  filePicker.disabled = false;
  fileList.replaceChildren();
  fileStatus.hidden = true;
  emptyFiles.hidden = false;
  sendFile.disabled = false;
  memoField.value = "";
  memoStatus.hidden = true;
  monitorSelect?.replaceChildren();
  browseList.replaceChildren();
  browsePath.hidden = true;
  driveOptions.replaceChildren();
  driveOptions.hidden = true;
  browseRoot.value = "desktop";
  keyboard.value = "";
  void exitFullscreen();
  idleDetail = message;
  idleError = error;
  render();
}

function updateState(session, state) {
  if (current !== session) return;
  if (state.phase === "ended") {
    detach(session, "고객과의 연결이 종료되었습니다.");
    return;
  }
  if (state.monitorSwitch === true) {
    session.monitorSwitch = true;
    if (state.monitorRevision < session.monitorRevision) return;
    if (state.monitorRevision !== session.monitorRevision) {
      clearLocalInput(session);
      session.events.length = 0;
      clearFrame(session);
      session.monitorRevision = state.monitorRevision;
      session.selectedMonitor = state.selectedMonitor;
      session.monitors = state.monitors;
      session.monitorError = "";
      monitorSelect?.replaceChildren(...session.monitors.map((monitor, index) => new Option(
        `모니터 ${index + 1}${monitor.primary ? " (기본)" : ""} · ${monitor.width}×${monitor.height}`, monitor.id
      )));
      if (monitorSelect) monitorSelect.value = session.selectedMonitor ?? "";
    }
  }
  if (session.controlEpoch !== state.controlEpoch || session.phase !== state.phase) {
    clearLocalInput(session);
    session.events = [];
  }
  session.controlEpoch = state.controlEpoch;
  session.phase = state.phase;
  session.hasFrame = state.hasFrame;
  session.canBrowse = state.canBrowse === true;
  session.canBrowseDrives = state.canBrowseDrives === true;
  session.lastState = performance.now();
  if (!state.hasFrame && session.url) {
    resetInput(session);
    clearFrame(session);
  }
  render();
  if (session.canBrowseDrives && !session.driveScanStarted && !session.browseBusy) {
    session.driveScanStarted = true;
    void browse(session, "drives");
  }
}

async function pollState(session) {
  while (current === session) {
    try {
      const response = await api("state", { token: session.token, signal: session.abort.signal, timeout: 1800 });
      updateState(session, await response.json());
    } catch (error) {
      if (sessionEnded(error)) detach(session, "고객과의 연결이 종료되었습니다.");
      if (current === session && performance.now() - session.lastState >= 15000) {
        detach(session, "연결이 끊겼습니다. 고객에게 새 접속 코드를 받아 다시 연결하세요.", true);
      }
    }
    if (current === session) await pause(650);
  }
}

async function showFrame(session, response, revision) {
  if (current !== session || session.switchingMonitor || revision !== session.monitorRevision) return;
  if (response.status === 204) {
    if (current === session && session.url) {
      resetInput(session);
      clearFrame(session);
      render();
    }
    return;
  }
  if (response.status === 304) {
    if (current === session && session.hasFrame && session.url) session.lastFrame = performance.now();
    return;
  }
  if (!response.headers.get("Content-Type")?.startsWith("image/jpeg")) return;
  if (session.monitorSwitch && response.headers.get("X-DLI-Monitor-Revision") !== String(revision)) return;
  const blob = await response.blob();
  if (current !== session || !session.hasFrame) return;

  const url = URL.createObjectURL(blob);
  const next = new Image();
  next.src = url;
  try {
    await next.decode();
    if (current === session && session.hasFrame && !session.switchingMonitor && revision === session.monitorRevision) {
      const old = session.url;
      session.url = url;
      session.etag = response.headers.get("ETag") ?? "";
      session.lastFrame = performance.now();
      session.frameMonitorRevision = revision;
      remoteImage.src = url;
      if (old) URL.revokeObjectURL(old);
      render();
    }
  } finally {
    if (session.url !== url) URL.revokeObjectURL(url);
  }
}

async function pollFrame(session) {
  while (current === session) {
    let delay = 140;
    if (session.hasFrame && !session.switchingMonitor && !document.hidden) {
      try {
        const revision = session.monitorRevision;
        const response = await api("frame", {
          token: session.token, signal: session.abort.signal, etag: session.etag, timeout: 1900,
        });
        await showFrame(session, response, revision);
      } catch (error) {
        if (sessionEnded(error)) detach(session, "고객과의 연결이 종료되었습니다.");
        delay = 450;
      }
    } else {
      delay = 250;
    }
    if (current === session) await pause(delay);
  }
}

monitorSelect?.addEventListener("change", async () => {
  const session = current;
  if (!session || session.switchingMonitor || monitorSelect.value === session.selectedMonitor) return;
  session.switchingMonitor = true;
  session.monitorError = "";
  resetInput(session, true);
  clearFrame(session);
  render();
  try {
    const response = await api("monitors", { token: session.token, signal: session.abort.signal, data: { id: monitorSelect.value } });
    updateState(session, await response.json());
  } catch (error) {
    if (sessionEnded(error)) detach(session, "고객과의 연결이 종료되었습니다.");
    else session.monitorError = error.status ? error.message : "모니터를 전환하지 못했습니다. 다시 시도하세요.";
  } finally {
    session.switchingMonitor = false;
    if (current === session) {
      monitorSelect.value = session.selectedMonitor ?? "";
      render();
    }
  }
});

codeField.addEventListener("input", () => {
  const before = codeField.value.slice(0, codeField.selectionStart).replace(/\D/g, "").length;
  codeField.value = displayCode(codeField.value.replace(/\D/g, "").slice(0, 8));
  const caret = Math.min(before + (before > 4 ? 1 : 0), codeField.value.length);
  codeField.setSelectionRange(caret, caret);
});

form.addEventListener("submit", async event => {
  event.preventDefault();
  if (current || joining || closing) return;
  const code = codeField.value.replace(/\s/g, "");
  const key = keyField.value;
  if (!/^\d{8}$/.test(code) || !key) {
    idleDetail = "숫자 8자리 접속 코드와 상담원 키를 입력하세요.";
    idleError = true;
    render();
    if (!/^\d{8}$/.test(code)) codeField.focus();
    return;
  }
  joining = true;
  idleDetail = "고객과 연결하는 중입니다.";
  idleError = false;
  render();
  try {
    const response = await api("join", { data: { code, key } });
    const payload = await response.json();
    if (typeof payload.operatorToken !== "string" || !payload.operatorToken) throw new Error("Invalid response");
    if (leaving) {
      endOnPageExit(payload.operatorToken);
      return;
    }
    const session = {
      code, token: payload.operatorToken, phase: payload.phase ?? "controlling",
      controlEpoch: payload.controlEpoch, hasFrame: Boolean(payload.hasFrame),
      lastState: performance.now(), lastFrame: 0, url: "", etag: "",
      monitorSwitch: false, monitors: [], selectedMonitor: null, monitorRevision: 0, frameMonitorRevision: null,
      switchingMonitor: false, monitorError: "",
      events: [], inputBusy: false, inputSequence: 0, lastInput: 0, nextInputAt: 0, composing: false,
      pointerId: null, buttons: new Set(), keys: new Set(), lastWheel: -Infinity,
      files: [], fileBusy: new Set(), filesVersion: 0, abort: new AbortController(),
      canBrowse: payload.canBrowse === true, folder: null, browseBusy: false, sendingMemo: false,
      canBrowseDrives: payload.canBrowseDrives === true, driveScanStarted: false,
    };
    current = session;
    updateState(session, payload);
    keyField.value = "";
    session.inputTimer = setInterval(() => inputTick(session), 32);
    void pollState(session);
    void pollFrame(session);
    void pollFiles(session);
  } catch (error) {
    idleDetail = error.status ? error.message : "연결할 수 없습니다. 잠시 후 다시 시도하세요.";
    idleError = true;
  } finally {
    joining = false;
    render();
  }
});

endButton.addEventListener("click", async () => {
  const session = current;
  if (!session || closing) return;
  closing = true;
  detach(session, "지원을 종료하는 중입니다.");
  codeField.value = "";
  try {
    await api("action", { token: session.token, data: { action: "end" } });
    idleDetail = "지원이 종료되었습니다. 새 지원에는 새 접속 코드가 필요합니다.";
  } catch (error) {
    if (sessionEnded(error)) {
      idleDetail = "지원이 종료되었습니다. 새 지원에는 새 접속 코드가 필요합니다.";
    } else {
      idleDetail = "이 화면의 연결을 닫았습니다. 고객에게도 지원 종료를 확인해 주세요.";
      idleError = true;
    }
  } finally {
    closing = false;
    render();
    codeField.focus();
  }
});

function endOnPageExit(token) {
  void fetch("/api/operator/action", {
    method: "POST", keepalive: true,
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify({ action: "end" }),
  }).catch(() => {});
}

window.addEventListener("pagehide", () => {
  leaving = true;
  if (current) {
    endOnPageExit(current.token);
    detach(current, "지원이 종료되었습니다. 새 지원에는 새 접속 코드가 필요합니다.");
  }
});
window.addEventListener("pageshow", () => { leaving = false; });

function clearLocalInput(session) {
  const pointerId = session.pointerId;
  session.pointerId = null;
  session.buttons.clear();
  session.keys.clear();
  session.composing = false;
  keyboard.value = "";
  if (pointerId !== null && remoteImage.hasPointerCapture(pointerId)) {
    remoteImage.releasePointerCapture(pointerId);
  }
}

function resetInput(session, force = false) {
  const mightBeHeld = session.buttons.size || session.keys.size || session.events.length || session.inputBusy;
  clearLocalInput(session);
  if ((force || mightBeHeld) && session.phase === "controlling") {
    session.events = [{ type: "reset" }];
  }
}

function enqueue(session, event) {
  const events = session.events;
  if (event.type === "move" && events.at(-1)?.type === "move") {
    events[events.length - 1] = event;
  } else if (events.length < 96) {
    events.push(event);
  } else {
    resetInput(session, true);
  }
}

function inputTick(session) {
  if (current !== session) return;
  const enabled = canInput(session);
  if (!enabled && (session.keys.size || session.buttons.size || session.events.some(item => item.type !== "reset"))) {
    resetInput(session, true);
  }
  if (enabled !== (remoteImage.dataset.control === "true")) render();
  if (session.inputBusy || session.phase !== "controlling" || !Number.isInteger(session.controlEpoch)) return;
  const now = performance.now();
  if (now < session.nextInputAt) return;
  const heartbeat = enabled && (session.keys.size || session.buttons.size) && now - session.lastInput >= 700;
  if (!session.events.length && !heartbeat) return;

  const events = session.events.splice(0, 24);
  const controlEpoch = session.controlEpoch;
  const monitorRevision = session.monitorRevision;
  const sequence = ++session.inputSequence;
  session.inputBusy = true;
  session.lastInput = now;
  void api("input", {
    token: session.token, signal: session.abort.signal, data: { events, controlEpoch, sequence, monitorRevision }, timeout: 1100,
  })
    .catch(error => {
      if (current !== session || session.controlEpoch !== controlEpoch || session.monitorRevision !== monitorRevision) return;
      if (sessionEnded(error)) {
        detach(session, "고객과의 연결이 종료되었습니다.");
      } else {
        session.nextInputAt = performance.now() + 500;
        resetInput(session, true);
      }
    })
    .finally(() => { session.inputBusy = false; });
}

function coordinates(event) {
  const rect = remoteImage.getBoundingClientRect();
  if (rect.width <= 0 || rect.height <= 0) return null;
  return {
    x: Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width)),
    y: Math.min(1, Math.max(0, (event.clientY - rect.top) / rect.height)),
  };
}

function syncButtons(session, event, point) {
  for (const [button, mask] of pointerButtons) {
    const down = Boolean(event.buttons & mask);
    if (down === session.buttons.has(button)) continue;
    if (down) session.buttons.add(button);
    else session.buttons.delete(button);
    enqueue(session, { type: down ? "down" : "up", button, ...point });
  }
}

remoteImage.addEventListener("pointerdown", event => {
  const session = current;
  if (!session || !canInput(session) || event.button > 2 || !event.isPrimary) return;
  const point = coordinates(event);
  if (!point) return;
  event.preventDefault();
  keyboard.focus({ preventScroll: true });
  session.pointerId = event.pointerId;
  remoteImage.setPointerCapture(event.pointerId);
  syncButtons(session, event, point);
});

remoteImage.addEventListener("pointermove", event => {
  const session = current;
  if (!session || !canInput(session) || !event.isPrimary) return;
  const point = coordinates(event);
  if (!point) return;
  if (session.pointerId === event.pointerId) syncButtons(session, event, point);
  enqueue(session, { type: "move", ...point });
});

remoteImage.addEventListener("pointerup", event => {
  const session = current;
  if (!session || session.pointerId !== event.pointerId) return;
  const point = coordinates(event);
  if (canInput(session) && point) syncButtons(session, event, point);
  else resetInput(session);
  session.pointerId = null;
});

for (const type of ["pointercancel", "lostpointercapture"]) {
  remoteImage.addEventListener(type, event => {
    if (current?.pointerId === event.pointerId) resetInput(current);
  });
}
for (const type of ["contextmenu", "auxclick"]) {
  remoteImage.addEventListener(type, event => {
    if (current && canInput(current)) event.preventDefault();
  });
}

remoteImage.addEventListener("wheel", event => {
  const session = current;
  if (!session || !canInput(session) || !event.deltaY) return;
  const point = coordinates(event);
  if (!point) return;
  event.preventDefault();
  const now = performance.now();
  if (now - session.lastWheel >= 95) {
    session.lastWheel = now;
    enqueue(session, { type: "wheel", delta: Math.sign(event.deltaY), ...point });
  }
}, { passive: false });

function virtualKey(code) {
  if (Object.hasOwn(keyboardKeys, code)) return { key: keyboardKeys[code], extended: false };
  if (Object.hasOwn(extendedKeys, code)) return { key: extendedKeys[code], extended: true };
  if (/^Key[A-Z]$/.test(code)) return { key: code.charCodeAt(3), extended: false };
  if (/^Digit\d$/.test(code)) return { key: code.charCodeAt(5), extended: false };
  if (/^Numpad\d$/.test(code)) return { key: 0x60 + Number(code[6]), extended: false };
  if (/^F(?:[1-9]|1[0-2])$/.test(code)) return { key: 0x6f + Number(code.slice(1)), extended: false };
  return null;
}

keyboard.addEventListener("keydown", event => {
  const session = current;
  if (!session || !canInput(session)) return;
  if (event.code === "F8") {
    event.preventDefault();
    stage.focus({ preventScroll: true });
    return;
  }
  if (session.composing || event.isComposing || event.keyCode === 229) return;
  if (!event.ctrlKey && !event.altKey && !event.metaKey && (event.key.length === 1 || event.key === "Dead")) return;
  const key = virtualKey(event.code);
  if (!key || event.metaKey) return;
  event.preventDefault();
  session.keys.add(event.code);
  enqueue(session, { type: "keydown", ...key });
});

keyboard.addEventListener("keyup", event => {
  const session = current;
  if (!session || !session.keys.delete(event.code)) return;
  event.preventDefault();
  enqueue(session, { type: "keyup", ...virtualKey(event.code) });
});

keyboard.addEventListener("compositionstart", () => { if (current) current.composing = true; });
keyboard.addEventListener("compositionend", event => {
  const session = current;
  if (!session?.composing) return;
  session.composing = false;
  if (canInput(session)) queueText(session, event.data);
  keyboard.value = "";
});
keyboard.addEventListener("input", event => {
  const session = current;
  if (session?.composing || event.isComposing) return;
  if (session && canInput(session) && event.inputType === "insertText") queueText(session, event.data);
  keyboard.value = "";
});
keyboard.addEventListener("blur", () => { if (current) resetInput(current); });
window.addEventListener("blur", () => { if (current) resetInput(current); });
document.addEventListener("visibilitychange", () => { if (document.hidden && current) resetInput(current); });

function queueText(session, text) {
  if (typeof text !== "string" || !text || text.length > 256 || !text.isWellFormed()
      || /[\u0000-\u001f\u007f-\u009f]/u.test(text)) return;
  enqueue(session, { type: "text", text });
}

function isFullscreen() {
  return document.fullscreenElement === screenPanel || screenPanel.dataset.expanded === "true";
}

function updateFullscreenLabel() {
  fullscreenButton.textContent = isFullscreen() ? "전체 화면 종료" : "전체 화면";
  fullscreenButton.setAttribute("aria-pressed", String(isFullscreen()));
}

async function exitFullscreen() {
  screenPanel.removeAttribute("data-expanded");
  if (document.fullscreenElement === screenPanel) await document.exitFullscreen().catch(() => {});
  updateFullscreenLabel();
}

fullscreenButton.addEventListener("click", async () => {
  if (!current) return;
  if (isFullscreen()) { await exitFullscreen(); return; }
  try { await screenPanel.requestFullscreen(); }
  catch { if (current) screenPanel.dataset.expanded = "true"; }
  updateFullscreenLabel();
  if (current && canInput(current)) keyboard.focus({ preventScroll: true });
});
document.addEventListener("fullscreenchange", updateFullscreenLabel);
document.addEventListener("keydown", event => {
  if (event.key === "Escape" && screenPanel.dataset.expanded === "true") {
    event.preventDefault();
    void exitFullscreen();
  }
}, { capture: true });

memoForm.addEventListener("submit", async event => {
  event.preventDefault();
  const session = current;
  if (!session || session.sendingMemo) return;
  session.sendingMemo = true;
  render();
  try {
    const text = memoField.value.trim();
    await api("memo", { token: session.token, signal: session.abort.signal, data: { text } });
    if (current !== session) return;
    memoStatus.textContent = text ? "고객 창에 메모를 보냈습니다." : "고객 창의 메모를 비웠습니다.";
    memoStatus.dataset.error = "false";
  } catch (error) {
    if (current !== session) return;
    memoStatus.textContent = error.message || "메모를 보내지 못했습니다.";
    memoStatus.dataset.error = "true";
  } finally {
    session.sendingMemo = false;
    if (current === session) { memoStatus.hidden = false; render(); }
  }
});

function renderBrowse(session) {
  const { folder, browseBusy } = session;
  browseRoot.disabled = browseBusy;
  browseOpen.disabled = browseBusy;
  browseUp.disabled = browseBusy || !folder?.segments.length;
  browseDrives.disabled = browseBusy;
  for (const button of browseList.querySelectorAll("button")) button.disabled = browseBusy;
}

function showFolder(session, root, segments, result) {
  session.folder = { root, segments };
  browsePath.hidden = false;
  const label = [...browseRoot.options].find(option => option.value === root)?.text || root;
  browsePath.textContent = [label, ...segments].join(" / ") + (result.truncated ? " · 최대 500개 표시" : "");
  browseList.replaceChildren();
  if (!result.entries.length) {
    const item = document.createElement("li");
    item.className = "file-empty";
    item.textContent = "표시할 파일이 없습니다.";
    browseList.append(item);
  }
  for (const entry of result.entries) {
    const item = document.createElement("li");
    item.className = "file-item";
    const name = document.createElement("span");
    name.className = "file-name";
    name.textContent = entry.name;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "button secondary";
    button.textContent = entry.folder ? "열기" : "가져오기";
    button.setAttribute("aria-label", `${entry.name} ${button.textContent}`);
    button.addEventListener("click", () => void browse(session, entry.folder ? "list" : "pull", root, [...segments, entry.name]));
    item.append(name, button);
    browseList.append(item);
  }
}

function showDrives(session, drives) {
  const selected = browseRoot.value;
  driveOptions.replaceChildren(...drives.map(drive => new Option(drive, drive)));
  driveOptions.hidden = drives.length === 0;
  browseRoot.value = [...browseRoot.options].some(option => option.value === selected) ? selected : "desktop";
  if (session.folder && /^[A-Z]:$/.test(session.folder.root) && !drives.includes(session.folder.root)) {
    session.folder = null;
    browsePath.hidden = true;
    browseList.replaceChildren();
  }
}

async function browse(session, action, root, segments) {
  if (current !== session || session.browseBusy) return;
  session.browseBusy = true;
  renderBrowse(session);
  const pending = { drives: "고객 PC의 드라이브 목록을 확인하고 있습니다.", list: "고객 PC의 폴더를 열고 있습니다.",
    pull: "고객 PC에서 파일을 가져오는 중입니다." };
  fileNotice(pending[action]);
  try {
    const { id } = await (await api("browse", { token: session.token, signal: session.abort.signal, data: { action, root, segments } })).json();
    const deadline = Date.now() + (action === "pull" ? 95000 : 12000);
    while (current === session && Date.now() < deadline) {
      const result = await (await api("browse", { token: session.token, signal: session.abort.signal })).json();
      if (result.id === id && !result.pending) {
        if (result.error) throw new Error(result.error);
        if (action === "drives") {
          if (!Array.isArray(result.drives)) throw new Error("드라이브 목록을 불러오지 못했습니다.");
          showDrives(session, result.drives);
          fileNotice(result.drives.length ? "드라이브 목록을 불러왔습니다." : "사용할 수 있는 드라이브가 없습니다.");
        } else if (action === "list") {
          if (!Array.isArray(result.entries)) throw new Error("폴더 목록을 불러오지 못했습니다.");
          showFolder(session, root, segments, result);
          fileNotice("고객 PC의 폴더를 열었습니다.");
        } else fileNotice("파일을 가져왔습니다. 아래 받은 파일 목록에서 다운로드하세요.");
        return;
      }
      await pause(400);
    }
    if (current === session) throw new Error("작업 응답 시간이 초과되었습니다.");
  } catch (error) {
    if (current === session) fileNotice(error.message || "파일 작업에 실패했습니다.", true);
  } finally {
    session.browseBusy = false;
    if (current === session) renderBrowse(session);
  }
}

browseOpen.addEventListener("click", () => { if (current) void browse(current, "list", browseRoot.value, []); });
browseDrives.addEventListener("click", () => { if (current?.canBrowseDrives) void browse(current, "drives"); });
browseUp.addEventListener("click", () => {
  const session = current;
  if (session?.folder?.segments.length) void browse(session, "list", session.folder.root, session.folder.segments.slice(0, -1));
});

function fileNotice(text, error = false) {
  fileStatus.textContent = text;
  fileStatus.dataset.error = String(error);
  fileStatus.hidden = false;
}

function fileSize(size) {
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${Math.round(size / 1024)} KiB`;
  return `${(size / (1024 * 1024)).toFixed(1)} MiB`;
}

function renderFiles(session, files) {
  const focus = document.activeElement?.dataset;
  session.files = files;
  fileList.replaceChildren();
  emptyFiles.hidden = files.length > 0;
  for (const file of files) {
    const item = document.createElement("li");
    item.className = "file-item";
    const name = document.createElement("span");
    name.className = "file-name";
    name.textContent = file.name;
    const size = document.createElement("span");
    size.className = "file-size";
    size.textContent = fileSize(file.size);
    const actions = document.createElement("div");
    actions.className = "file-actions";
    for (const [action, label] of [["save", "다운로드"], ["delete", "목록에서 삭제"]]) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "button secondary";
      button.textContent = label;
      button.setAttribute("aria-label", `${file.name} ${label}`);
      button.dataset.id = file.id;
      button.dataset.action = action;
      button.disabled = session.fileBusy.has(file.id);
      button.addEventListener("click", () => { void handleFile(session, file, action); });
      actions.append(button);
    }
    item.append(name, size, actions);
    fileList.append(item);
  }
  if (focus?.id && focus.action) {
    const replacement = [...fileList.querySelectorAll("button")].find(button =>
      button.dataset.id === focus.id && button.dataset.action === focus.action);
    (replacement ?? filePicker).focus({ preventScroll: true });
  }
}

async function pollFiles(session) {
  while (current === session) {
    if (!document.hidden) {
      const version = session.filesVersion;
      try {
        const response = await api("files", { token: session.token, signal: session.abort.signal });
        const { files } = await response.json();
        if (current === session && session.filesVersion === version && Array.isArray(files)) {
          const visible = files.filter(file => typeof file.id === "string" && typeof file.name === "string"
            && Number.isSafeInteger(file.size) && file.size >= 0 && file.size <= fileLimit);
          const changed = visible.length !== session.files.length || visible.some((file, index) => {
            const previous = session.files[index];
            return file.id !== previous.id || file.name !== previous.name || file.size !== previous.size;
          });
          if (changed) renderFiles(session, visible);
        }
      } catch (error) {
        if ([401, 410].includes(error.status)) detach(session, "고객과의 연결이 종료되었습니다.");
      }
    }
    if (current === session) await pause(1200);
  }
}

fileForm.addEventListener("submit", async event => {
  event.preventDefault();
  const session = current;
  const file = filePicker.files[0];
  if (!session || !file || sendFile.disabled) return;
  if (file.size > fileLimit) {
    fileNotice("20 MiB 이하의 파일을 선택하세요.", true);
    return;
  }
  sendFile.disabled = true;
  filePicker.disabled = true;
  fileNotice("파일을 보내는 중입니다.");
  try {
    await api("files", { token: session.token, signal: session.abort.signal, file, timeout: 90000 });
    if (current !== session) return;
    filePicker.value = "";
    fileNotice(`보냈습니다. ${file.name}`);
  } catch (error) {
    if (current === session) fileNotice(error.status ? error.message : "파일을 보낼 수 없습니다. 다시 시도하세요.", true);
  } finally {
    if (current === session) {
      sendFile.disabled = false;
      filePicker.disabled = false;
    }
  }
});

async function handleFile(session, file, action) {
  if (current !== session || session.fileBusy.has(file.id)) return;
  session.fileBusy.add(file.id);
  const updateButtons = disabled => {
    for (const button of fileList.querySelectorAll("button")) {
      if (button.dataset.id === file.id) button.disabled = disabled;
    }
  };
  updateButtons(true);
  const path = `files/${encodeURIComponent(file.id)}`;
  const request = { token: session.token, signal: session.abort.signal };
  fileNotice(action === "delete" ? "파일을 목록에서 삭제하는 중입니다." : "파일을 받는 중입니다.");
  try {
    if (action === "delete") {
      await api(path, { ...request, method: "DELETE" });
      if (current !== session) return;
      session.filesVersion += 1;
      renderFiles(session, session.files.filter(item => item.id !== file.id));
      fileNotice(`목록에서 삭제했습니다. ${file.name}`);
    } else {
      const response = await api(path, { ...request, timeout: 90000 });
      const blob = await response.blob();
      if (current !== session) return;
      if (blob.size !== file.size) throw new Error("Size mismatch");
      const url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = file.name;
      link.hidden = true;
      document.body.append(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 30000);
      fileNotice(`다운로드를 시작했습니다. ${file.name}`);
    }
  } catch (error) {
    if (current === session) fileNotice(error.status ? error.message : "파일 작업을 완료하지 못했습니다. 다시 시도하세요.", true);
  } finally {
    session.fileBusy.delete(file.id);
    if (current === session) updateButtons(false);
  }
}

render();
