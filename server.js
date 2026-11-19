'use strict';

const http = require('node:http');
const https = require('node:https');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { pipeline } = require('node:stream/promises');

const MiB = 1024 * 1024;
const FILE_LIMIT = 20 * MiB;
const FILE_BUDGET = 100 * MiB;
const FRAME_LIMIT = 1.5 * MiB;
const CODE_LIFETIME = 4 * 60_000;
const SESSION_LIFETIME = 45 * 60_000;
const PEER_TIMEOUT = 15_000;
const INPUT_TIMEOUT = 2_000;
const FRAME_TIMEOUT = 2_500;
const MAX_SESSIONS = 8;
const assetPaths = { '/': ['index.html', 'text/html; charset=utf-8'], '/index.html': ['index.html', 'text/html; charset=utf-8'], '/app.js': ['app.js', 'text/javascript; charset=utf-8'], '/styles.css': ['styles.css', 'text/css; charset=utf-8'] };

const port = Number(process.env.DLI_PORT || 8765);
const bind = process.env.DLI_BIND || '127.0.0.1';
const loopback = ['127.0.0.1', '::1'].includes(bind);
const certPath = process.env.DLI_TLS_CERT;
const keyPath = process.env.DLI_TLS_KEY;
const tls = Boolean(certPath && keyPath);
const publicOrigin = process.env.DLI_PUBLIC_ORIGIN ? new URL(process.env.DLI_PUBLIC_ORIGIN) : null;
const proxyToken = process.env.DLI_PROXY_TOKEN;
const privateProxy = Boolean(proxyToken && publicOrigin);
const updateRoot = process.env.DLI_UPDATE_DIR ? path.resolve(process.env.DLI_UPDATE_DIR) : null;
const configuredKey = process.env.DLI_OPERATOR_KEY;
const protectedKeyPath = process.env.DLI_OPERATOR_KEY_FILE;
const storedKey = protectedKeyPath ? fs.readFileSync(protectedKeyPath, 'utf8').trim() : '';
const operatorKey = configuredKey || storedKey || crypto.randomBytes(15).toString('base64url');

if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('DLI_PORT 범위는 1~65535입니다.');
if (Boolean(certPath) !== Boolean(keyPath) || (!loopback && !tls && !privateProxy)) throw new Error('외부 연결에는 HTTPS 인증서 또는 인증된 HTTPS 프록시가 필요합니다.');
if (proxyToken && (!publicOrigin || !/^[a-f0-9]{64}$/.test(proxyToken))) throw new Error('DLI_PROXY_TOKEN에 64자리 무작위 16진수 키를 지정하세요.');
if (publicOrigin && (publicOrigin.protocol !== 'https:' || publicOrigin.href !== publicOrigin.origin + '/')) {
  throw new Error('DLI_PUBLIC_ORIGIN에는 HTTPS 서버 주소를 지정하세요.');
}
if ((configuredKey && protectedKeyPath) || (protectedKeyPath && !storedKey) || operatorKey.length < 12 || operatorKey.length > 128
  || ((!loopback || publicOrigin) && !(configuredKey || storedKey))) throw new Error('12~128자 상담원 키를 한 곳에 지정하세요.');

const operatorDigest = digest(operatorKey);
const proxyDigest = proxyToken ? digest(proxyToken) : null;
const sessions = new Set();
const byCode = new Map();
const byToken = new Map();
let fileBytes = 0;
let joinFailures = [];

class HttpError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

function digest(value) {
  return crypto.createHash('sha256').update(value).digest();
}

function secret() {
  return crypto.randomBytes(32).toString('base64url');
}

function assert(condition, status, message) {
  if (!condition) throw new HttpError(status, message);
}

function headers(res) {
  res.setHeader('Cache-Control', 'no-store');
  res.setHeader('X-Content-Type-Options', 'nosniff');
  res.setHeader('X-Frame-Options', 'DENY');
  res.setHeader('Referrer-Policy', 'no-referrer');
  res.setHeader('Content-Security-Policy', "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'");
}

function json(res, status, value) {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify(value));
}

function readBytes(req, limit, timeout = 8_000) {
  const declared = req.headers['content-length'];
  if (declared !== undefined && (!/^\d+$/.test(declared) || Number(declared) > limit)) {
    req.resume();
    throw new HttpError(413, '전송할 데이터가 너무 큽니다.');
  }
  return new Promise((resolve, reject) => {
    let size = 0;
    let complete = false;
    const chunks = [];
    const timer = setTimeout(() => fail(408, '전송 시간이 초과되었습니다.'), timeout);
    function fail(status, message) {
      if (complete) return;
      complete = true;
      clearTimeout(timer);
      req.removeListener('data', receive);
      req.resume();
      chunks.length = 0;
      reject(new HttpError(status, message));
    }
    function receive(chunk) {
      size += chunk.length;
      if (size > limit) {
        fail(413, '전송할 데이터가 너무 큽니다.');
        return;
      }
      chunks.push(chunk);
    }
    req.on('data', receive);
    req.once('end', () => {
      if (complete) return;
      complete = true;
      clearTimeout(timer);
      resolve(Buffer.concat(chunks, size));
    });
    req.once('error', () => fail(400, '전송이 중단되었습니다.'));
    req.once('aborted', () => fail(400, '전송이 중단되었습니다.'));
  });
}

async function readJson(req, limit = 4096) {
  assert(req.headers['content-type']?.split(';')[0] === 'application/json', 415, 'JSON 요청이 필요합니다.');
  const bytes = await readBytes(req, limit);
  let value;
  try { value = JSON.parse(bytes.toString('utf8')); } catch {
    throw new HttpError(400, '요청 형식이 올바르지 않습니다.');
  }
  assert(value && typeof value === 'object' && !Array.isArray(value), 400, '요청 형식이 올바르지 않습니다.');
  return value;
}

function resetInput(session) {
  session.inputs = [{ type: 'reset' }];
  session.held.clear();
}

function expire(session, now) {
  if (session.phase === 'ended') return;
  const waiting = session.phase === 'waiting';
  const deadline = waiting ? session.codeExpires : session.connectedAt + SESSION_LIFETIME;
  if (now >= deadline || now - session.hostSeen >= PEER_TIMEOUT || (!waiting && now - session.operatorSeen >= PEER_TIMEOUT)) {
    end(session, now);
    return;
  }
  if (session.frame && now - session.frame.receivedAt >= FRAME_TIMEOUT) {
    session.frame = null;
    resetInput(session);
  }
  if (session.held.size && now - session.inputSeen >= INPUT_TIMEOUT) resetInput(session);
}

function end(session, now = Date.now()) {
  if (session.phase === 'ended') return;
  if (byCode.get(session.code) === session) byCode.delete(session.code);
  session.phase = 'ended';
  session.endedAt = now;
  session.controlEpoch++;
  session.frame = null;
  session.memo = '';
  session.fileAccess = null;
  resetInput(session);
  for (const role of ['host', 'operator']) {
    for (const file of session.files[role].values()) fileBytes -= file.size;
    session.files[role].clear();
  }
}

function sweep() {
  const now = Date.now();
  for (const session of sessions) {
    expire(session, now);
    if (session.phase === 'ended' && now - session.endedAt >= 30_000) {
      sessions.delete(session);
      byToken.delete(session.hostToken);
      if (session.operatorToken) byToken.delete(session.operatorToken);
    }
  }
  joinFailures = joinFailures.filter(time => now - time < 60_000);
}

setInterval(sweep, 400).unref();

function newSession(fileBrowserVersion, supportsMonitors) {
  sweep();
  const running = [...sessions].filter(session => session.phase !== 'ended').length;
  assert(running < MAX_SESSIONS && sessions.size < MAX_SESSIONS * 8, 429, '지원 연결이 가득 찼습니다. 잠시 후 다시 시도하세요.');
  let code;
  do { code = crypto.randomInt(10_000_000, 100_000_000).toString(); } while (byCode.has(code));
  const now = Date.now();
  const session = {
    code, codeExpires: now + CODE_LIFETIME, hostToken: secret(), operatorToken: null,
    hostSeen: now, operatorSeen: 0, connectedAt: 0, endedAt: 0,
    phase: 'waiting', controlEpoch: 0, frame: null, frameRequest: 0, memo: '', fileAccess: null, fileBrowserVersion,
    supportsMonitors, monitorSwitch: false, monitors: [], selectedMonitor: null, monitorRevision: 0,
    inputs: [], held: new Set(), inputSeen: now, inputSequence: 0,
    files: { host: new Map(), operator: new Map() }, pending: { host: 0, operator: 0 },
  };
  sessions.add(session);
  byCode.set(code, session);
  byToken.set(session.hostToken, { session, role: 'host' });
  return { code, hostToken: session.hostToken, expiresInSeconds: CODE_LIFETIME / 1000 };
}

function join({ code, key }, supportsMonitors) {
  sweep();
  assert(joinFailures.length < 40, 429, '연결 시도가 너무 많습니다. 잠시 후 다시 시도하세요.');
  if (typeof key !== 'string' || key.length > 128 || !crypto.timingSafeEqual(digest(key), operatorDigest)) {
    joinFailures.push(Date.now());
    throw new HttpError(401, '상담원 키를 확인하세요.');
  }
  const session = typeof code === 'string' ? byCode.get(code) : null;
  if (!session || session.phase !== 'waiting') {
    joinFailures.push(Date.now());
    throw new HttpError(404, '사용할 수 없는 코드입니다. 고객에게 새 코드를 요청하세요.');
  }
  byCode.delete(code);
  session.operatorToken = secret();
  session.connectedAt = session.operatorSeen = session.inputSeen = Date.now();
  session.phase = 'controlling';
  session.controlEpoch = 1;
  session.monitorSwitch = session.supportsMonitors && supportsMonitors;
  byToken.set(session.operatorToken, { session, role: 'operator' });
  return { operatorToken: session.operatorToken, ...state(session) };
}

function authorized(req, role) {
  const token = /^Bearer ([A-Za-z0-9_-]{43})$/.exec(req.headers.authorization || '')?.[1];
  const auth = byToken.get(token);
  assert(auth?.role === role, 401, '지원 연결을 확인할 수 없습니다. 새 코드로 연결하세요.');
  const { session } = auth;
  const now = Date.now();
  expire(session, now);
  if (session.phase !== 'ended') session[role === 'host' ? 'hostSeen' : 'operatorSeen'] = now;
  return session;
}

function active(session) {
  expire(session, Date.now());
  assert(session.phase === 'controlling', 410, '지원 연결이 종료되었습니다.');
}

function state(session) {
  return { phase: session.phase, controlEpoch: session.controlEpoch, controlRequested: false, hasFrame: Boolean(session.frame),
    canBrowse: session.fileBrowserVersion > 0, canBrowseDrives: session.fileBrowserVersion >= 2,
    ...(session.monitorSwitch && { monitorSwitch: true, monitors: session.monitors,
      selectedMonitor: session.selectedMonitor, monitorRevision: session.monitorRevision }) };
}

function setMonitor(session, id) {
  session.selectedMonitor = id;
  session.monitorRevision++;
  session.frameRequest++;
  session.frame = null;
  resetInput(session);
}

function setMonitors(session, monitors) {
  assert(Array.isArray(monitors) && monitors.length <= 32 && monitors.every(monitor => monitor
    && typeof monitor.id === 'string' && /^[A-Za-z0-9_.\\-]{1,64}$/.test(monitor.id)
    && Number.isInteger(monitor.width) && monitor.width > 0 && monitor.width <= 32768
    && Number.isInteger(monitor.height) && monitor.height > 0 && monitor.height <= 32768
    && typeof monitor.primary === 'boolean') && new Set(monitors.map(monitor => monitor.id)).size === monitors.length,
  400, '모니터 목록을 읽을 수 없습니다.');
  session.monitors = monitors.map(({ id, width, height, primary }) => ({ id, width, height, primary }));
  const current = session.monitors.find(monitor => monitor.id === session.selectedMonitor);
  const primary = session.monitors.find(monitor => monitor.primary);
  setMonitor(session, (current || primary || session.monitors[0])?.id ?? null);
}

function browseName(name) {
  return typeof name === 'string' && name.length > 0 && name.length <= 255 && name !== '.' && name !== '..'
    && !/[<>:"/\\|?*\u0000-\u001f]/u.test(name) && !/[. ]$/.test(name);
}

function isDrive(root) {
  return typeof root === 'string' && /^[A-Z]:$/.test(root);
}

function browseRequest({ action, root, segments }, version) {
  const drive = isDrive(root);
  if (action === 'drives' || drive) assert(version >= 2, 409, '드라이브를 보려면 고객 앱을 업데이트하세요.');
  if (action === 'drives') return { id: crypto.randomUUID(), action };
  assert(['list', 'pull'].includes(action) && (drive || ['desktop', 'documents', 'downloads'].includes(root))
    && Array.isArray(segments) && segments.length <= (drive ? 64 : 16) && segments.every(browseName)
    && (action !== 'pull' || segments.length > 0), 400, '파일 경로를 확인하세요.');
  return { id: crypto.randomUUID(), action, root, segments };
}

function inputEvent(value) {
  assert(value && typeof value === 'object', 400, '조작 입력 형식이 올바르지 않습니다.');
  const { type, x, y, button, delta, key, extended } = value;
  const validNumber = number => typeof number === 'number' && Number.isFinite(number);
  switch (type) {
    case 'reset': return { type };
    case 'move':
    case 'down':
    case 'up':
    case 'wheel': {
      assert(validNumber(x) && x >= 0 && x <= 1 && validNumber(y) && y >= 0 && y <= 1, 400, '화면 좌표가 올바르지 않습니다.');
      if (type === 'wheel') {
        assert(validNumber(delta) && delta !== 0, 400, '스크롤 입력이 올바르지 않습니다.');
        return { type, x, y, delta: Math.sign(delta) };
      }
      if (type !== 'move') {
        assert([0, 1, 2].includes(button), 400, '마우스 입력이 올바르지 않습니다.');
        return { type, x, y, button };
      }
      return { type, x, y };
    }
    case 'keydown':
    case 'keyup':
      assert(Number.isInteger(key) && key >= 8 && key <= 254, 400, '키보드 입력이 올바르지 않습니다.');
      return { type, key, extended: extended === true };
    case 'text':
      assert(typeof value.text === 'string' && value.text.length > 0 && value.text.length <= 256 && value.text.isWellFormed() && !/[\u0000-\u001f\u007f-\u009f]/u.test(value.text), 400, '입력할 문자를 확인하세요.');
      return { type, text: value.text };
    default: throw new HttpError(400, '지원되지 않는 조작 입력입니다.');
  }
}

function addInput(session, payload) {
  active(session);
  assert(payload.controlEpoch === session.controlEpoch, 409, '조작 권한이 만료되었습니다.');
  assert(!session.monitorSwitch || payload.monitorRevision === session.monitorRevision,
    409, '전환한 모니터가 표시될 때 다시 조작하세요.');
  const { sequence } = payload;
  if (sequence !== undefined) {
    assert(Number.isSafeInteger(sequence) && sequence > session.inputSequence, 409, '이전 조작 입력을 무시했습니다.');
    session.inputSequence = sequence;
  } else {
    assert(session.inputSequence === 0, 409, '조작 입력 순서가 필요합니다.');
  }
  assert(Array.isArray(payload.events) && payload.events.length <= 32, 400, '조작 입력이 너무 많습니다.');
  const events = payload.events.map(inputEvent);
  assert(session.frame || events.every(event => event.type === 'reset'), 409, '고객 화면이 표시될 때 다시 조작하세요.');
  session.inputSeen = Date.now();
  if (session.inputs.length + events.length > 96) resetInput(session);
  for (const event of events) {
    const { type, button, key, extended } = event;
    if (type === 'reset') { resetInput(session); continue; }
    if (type === 'down') session.held.add(`b${button}`);
    if (type === 'up') session.held.delete(`b${button}`);
    if (type === 'keydown') session.held.add(`k${key}:${extended}`);
    if (type === 'keyup') session.held.delete(`k${key}:${extended}`);
    if (type === 'move' && session.inputs.at(-1)?.type === 'move') session.inputs.pop();
    session.inputs.push(event);
  }
}

function publicFile({ id, name, size }) {
  return { id, name, size };
}

function fileName(req) {
  const encoded = req.headers['x-file-name'];
  let name;
  try { name = decodeURIComponent(encoded || ''); } catch { name = ''; }
  assert(name && name.length <= 180 && name === name.trim() && !/[<>:"/\\|?*\u0000-\u001f\u007f\u202a-\u202e\u2066-\u2069]/u.test(name) && !/[ .]$/.test(name) && !/^(?:con|prn|aux|nul|com[1-9¹²³]|lpt[1-9¹²³])(?:\.|$)/i.test(name), 400, '파일 이름을 확인하세요.');
  return name;
}

async function upload(req, session, recipient) {
  const size = Number(req.headers['content-length']);
  assert(Number.isSafeInteger(size) && size >= 0, 411, '파일 크기를 확인할 수 없습니다.');
  assert(size <= FILE_LIMIT, 413, '파일당 최대 20MB까지 보낼 수 있습니다.');
  assert(req.headers['content-type'] === 'application/octet-stream', 415, '파일 요청 형식이 올바르지 않습니다.');
  const name = fileName(req);
  const inbox = session.files[recipient];
  assert(inbox.size + session.pending[recipient] < 3, 409, '상대방이 대기 중인 파일을 저장하거나 목록에서 지운 뒤 다시 보내세요.');
  assert(fileBytes + size <= FILE_BUDGET, 429, '파일 전송 공간이 가득 찼습니다. 잠시 후 다시 시도하세요.');
  session.pending[recipient]++;
  fileBytes += size;
  let committed = false;
  try {
    const bytes = await readBytes(req, size, 90_000);
    active(session);
    assert(bytes.length === size, 400, '파일 전송이 중단되었습니다.');
    const file = { id: crypto.randomBytes(16).toString('hex'), name, size, bytes };
    inbox.set(file.id, file);
    committed = true;
    return publicFile(file);
  } finally {
    session.pending[recipient]--;
    if (!committed) fileBytes -= size;
  }
}

function fileDownload(res, file) {
  const name = encodeURIComponent(file.name).replace(/[!'()*]/g, char => '%' + char.charCodeAt(0).toString(16));
  res.writeHead(200, {
    'Content-Type': 'application/octet-stream', 'Content-Length': file.size,
    'Content-Disposition': `attachment; filename*=UTF-8''${name}`,
  });
  res.end(file.bytes);
}

async function route(req, res) {
  headers(res);
  const { method } = req;
  const requestPath = req.url?.split('?')[0];
  const scheme = tls ? 'https' : 'http';
  const requestHost = req.headers.host || '';
  const localAddress = ['localhost', '127.0.0.1', '[::1]'].some(host => requestHost === `${host}:${port}` || (port === 80 && requestHost === host));
  if (method === 'GET' && requestPath === '/healthz' && !tls && localAddress) {
    json(res, 200, { service: 'dli-remote-support', status: 'ok' });
    return;
  }
  const expectedOrigin = publicOrigin?.origin || `${scheme}://${requestHost}`;
  if (publicOrigin) {
    assert(requestHost.toLowerCase() === publicOrigin.host && (tls || req.headers['x-forwarded-proto'] === 'https'), 403, '운영 HTTPS 주소로 접속하세요.');
    if (proxyDigest) {
      const token = req.headers['x-dli-proxy-token'];
      assert(typeof token === 'string' && token.length === 64 && crypto.timingSafeEqual(digest(token), proxyDigest), 403, '등록된 중계 프록시를 사용하세요.');
    }
    res.setHeader('Strict-Transport-Security', 'max-age=31536000');
  } else if (!tls) {
    assert(localAddress, 403, '로컬 지원 주소로 접속하세요.');
  }
  assert((!req.headers.origin || req.headers.origin === expectedOrigin)
    && (!['cross-site', 'same-site'].includes(req.headers['sec-fetch-site']) || method === 'GET'), 403, '이 사이트에서 요청할 수 없습니다.');
  if (requestPath === '/healthz' && method === 'GET') {
    json(res, 200, { service: 'dli-remote-support', status: 'ok' });
    return;
  }
  if (method === 'GET' && Object.hasOwn(assetPaths, requestPath)) {
    const [filename, mime] = assetPaths[requestPath];
    const content = await fs.promises.readFile(path.join(__dirname, 'public', filename));
    res.writeHead(200, { 'Content-Type': mime, 'Content-Length': content.length });
    res.end(content);
    return;
  }
  if (requestPath === '/favicon.ico' && method === 'GET') { res.writeHead(204).end(); return; }
  if (method === 'GET' && updateRoot && requestPath === '/download') {
    let release;
    try { release = JSON.parse(await fs.promises.readFile(path.join(updateRoot, 'manifest.json'), 'utf8')); }
    catch (error) {
      if (error.code === 'ENOENT') throw new HttpError(404, '게시된 설치 파일이 없습니다.');
      throw error;
    }
    assert(typeof release?.version === 'string' && /^\d{1,5}(\.\d{1,5}){3}$/.test(release.version), 503, '설치 파일 정보를 확인할 수 없습니다.');
    res.writeHead(302, { Location: `/updates/${release.version}/DLI-RemoteSupport-Setup.exe` }).end();
    return;
  }
  if (method === 'GET' && updateRoot && requestPath.startsWith('/updates/')) {
    const manifest = requestPath === '/updates/manifest.json';
    const artifact = /^\/updates\/(\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5})\/DLI-RemoteSupport-Setup\.exe$/.exec(requestPath);
    assert(manifest || artifact, 404, '업데이트 파일을 찾을 수 없습니다.');
    const filename = manifest ? path.join(updateRoot, 'manifest.json') : path.join(updateRoot, artifact[1], 'DLI-RemoteSupport-Setup.exe');
    let file;
    try { file = await fs.promises.open(filename, 'r'); }
    catch (error) {
      if (error.code === 'ENOENT' || error.code === 'ENOTDIR') throw new HttpError(404, '업데이트 파일을 찾을 수 없습니다.');
      throw error;
    }
    try {
      const info = await file.stat();
      assert(info.isFile() && info.size <= (manifest ? 8192 : 30 * MiB), 404, '업데이트 파일을 찾을 수 없습니다.');
      if (!manifest) res.setHeader('Content-Disposition', 'attachment; filename="DLI-RemoteSupport-Setup.exe"');
      res.writeHead(200, { 'Content-Type': manifest ? 'application/json; charset=utf-8' : 'application/octet-stream', 'Content-Length': info.size });
      await pipeline(file.createReadStream({ autoClose: false }), res);
    } finally { await file.close(); }
    return;
  }
  if (requestPath === '/api/session' && method === 'POST') {
    const browser = req.headers['x-dli-file-browser'];
    req.resume();
    json(res, 201, newSession(browser === '2' ? 2 : browser === '1' ? 1 : 0, req.headers['x-dli-monitor-switch'] === '1'));
    return;
  }
  if (requestPath === '/api/operator/join' && method === 'POST') {
    json(res, 200, join(await readJson(req), req.headers['x-dli-monitor-switch'] === '1'));
    return;
  }
  const match = /^\/api\/(host|operator)\/(state|action|frame|input|files|memo|browse|monitors)(?:\/([a-f0-9]{32}))?$/.exec(requestPath);
  assert(match, 404, '요청한 기능을 찾을 수 없습니다.');
  const [, role, target, id] = match;
  assert(!id || target === 'files', 404, '요청한 기능을 찾을 수 없습니다.');
  const session = authorized(req, role);
  if (method === 'GET' && target === 'state') {
    const snapshot = state(session);
    if (role === 'host') {
      snapshot.memo = session.memo;
      snapshot.fileRequest = session.fileAccess?.request || null;
    }
    json(res, 200, snapshot);
    return;
  }
  if (method === 'POST' && target === 'action') {
    const { action } = await readJson(req);
    assert(action === 'end', 400, '지원되지 않는 연결 동작입니다.');
    end(session);
    json(res, 200, state(session));
    return;
  }
  if (method === 'GET' && role === 'host' && target === 'input') {
    const events = session.phase === 'controlling' ? session.inputs.splice(0) : [];
    json(res, 200, { ...state(session), events });
    return;
  }
  active(session);
  if (target === 'monitors' && method === 'POST') {
    assert(session.monitorSwitch, 409, '모니터를 전환하려면 고객 앱을 업데이트하세요.');
    const request = await readJson(req, 8192);
    active(session);
    if (role === 'host') {
      setMonitors(session, request.monitors);
    } else {
      assert(session.monitors.some(monitor => monitor.id === request.id), 409, '고객 PC에 이 모니터가 연결되어 있지 않습니다.');
      if (session.selectedMonitor !== request.id) setMonitor(session, request.id);
    }
    json(res, 200, state(session));
    return;
  }
  if (target === 'browse' && role === 'operator') {
    assert(session.fileBrowserVersion > 0, 409, '이 기능을 사용하려면 고객 앱을 업데이트하세요.');
    if (method === 'GET') {
      const task = session.fileAccess;
      json(res, 200, task?.result || { id: task?.request?.id, pending: Boolean(task?.request) });
      return;
    }
    if (method === 'POST') {
      const command = browseRequest(await readJson(req, 32768), session.fileBrowserVersion);
      active(session);
      assert(!session.fileAccess?.request, 409, '이전 파일 작업이 진행 중입니다.');
      session.fileAccess = { request: command, result: null };
      json(res, 202, { id: command.id });
      return;
    }
  }
  if (target === 'browse' && role === 'host' && method === 'POST') {
    const result = await readJson(req, 512 * 1024);
    active(session);
    const task = session.fileAccess;
    assert(task?.request?.id === result.id, 409, '이미 완료된 파일 작업입니다.');
    const entries = result.entries;
    const drives = result.drives;
    assert(entries == null || (task.request.action === 'list' && Array.isArray(entries) && entries.length <= 500
      && entries.every(entry => entry && browseName(entry.name) && typeof entry.folder === 'boolean'
        && Number.isSafeInteger(entry.size) && entry.size >= 0)), 400, '파일 목록을 읽을 수 없습니다.');
    assert(drives == null || (task.request.action === 'drives' && Array.isArray(drives) && drives.length <= 26
      && drives.every(isDrive) && new Set(drives).size === drives.length), 400, '드라이브 목록을 읽을 수 없습니다.');
    task.result = { id: result.id, entries, drives, truncated: result.truncated === true,
      error: typeof result.error === 'string' ? result.error.slice(0, 180) : undefined };
    task.request = null;
    res.writeHead(204).end();
    return;
  }
  if (method === 'POST' && role === 'operator' && target === 'memo') {
    const { text } = await readJson(req, 8192);
    assert(typeof text === 'string' && text.length <= 600 && text.isWellFormed()
      && !/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/u.test(text), 400, '메모는 최대 600자까지 보낼 수 있습니다.');
    active(session);
    session.memo = text.replace(/\r\n?/g, '\n').trim();
    res.writeHead(204).end();
    return;
  }
  if (method === 'DELETE' && role === 'host' && target === 'frame') {
    session.frameRequest++;
    session.frame = null;
    resetInput(session);
    res.writeHead(204).end();
    return;
  }
  if (method === 'POST' && role === 'host' && target === 'frame') {
    assert(req.headers['content-type'] === 'image/jpeg', 415, 'JPEG 화면 데이터가 필요합니다.');
    const revision = req.headers['x-dli-monitor-revision'];
    const matchesMonitor = () => !session.monitorSwitch || (session.selectedMonitor !== null && revision === String(session.monitorRevision));
    const frameRequest = matchesMonitor() ? ++session.frameRequest : null;
    const bytes = await readBytes(req, FRAME_LIMIT);
    active(session);
    assert(bytes.length >= 4 && bytes[0] === 255 && bytes[1] === 216 && bytes.at(-2) === 255 && bytes.at(-1) === 217, 400, '화면 데이터를 읽을 수 없습니다.');
    if (frameRequest === session.frameRequest && matchesMonitor()) session.frame = { bytes, tag: `"${frameRequest}"`, receivedAt: Date.now() };
    res.writeHead(204).end();
    return;
  }
  if (method === 'GET' && role === 'operator' && target === 'frame') {
    if (!session.frame) { res.writeHead(204).end(); return; }
    const { bytes, tag } = session.frame;
    if (req.headers['if-none-match'] === tag) { res.writeHead(304, { ETag: tag }).end(); return; }
    res.writeHead(200, { 'Content-Type': 'image/jpeg', 'Content-Length': bytes.length, ETag: tag,
      ...(session.monitorSwitch && { 'X-DLI-Monitor-Revision': String(session.monitorRevision) }) }).end(bytes);
    return;
  }
  if (method === 'POST' && role === 'operator' && target === 'input') {
    addInput(session, await readJson(req, 16_384));
    res.writeHead(204).end();
    return;
  }
  if (target === 'files') {
    const inbox = session.files[role];
    if (!id && method === 'GET') { json(res, 200, { files: [...inbox.values()].map(publicFile) }); return; }
    if (!id && method === 'POST') { json(res, 201, await upload(req, session, role === 'host' ? 'operator' : 'host')); return; }
    if (id && ['GET', 'DELETE'].includes(method)) {
      const file = inbox.get(id);
      assert(file, 404, '받을 파일을 찾을 수 없습니다.');
      if (method === 'GET') { fileDownload(res, file); return; }
      inbox.delete(id);
      fileBytes -= file.size;
      res.writeHead(204).end();
      return;
    }
  }
  throw new HttpError(404, '요청한 기능을 찾을 수 없습니다.');
}

function serve(req, res) {
  route(req, res).catch(error => {
    if (res.headersSent || res.destroyed) { res.destroy(); return; }
    if (error instanceof HttpError) {
      req.resume();
      json(res, error.status, { error: error.message });
      return;
    }
    console.error('요청 오류', error.code || error.name);
    json(res, 500, { error: '지원 서버 오류가 발생했습니다.' });
  });
}

const options = { requestTimeout: 95_000, headersTimeout: 8000, connectionsCheckingInterval: 2000 };
const server = tls
  ? https.createServer({ ...options, cert: fs.readFileSync(certPath), key: fs.readFileSync(keyPath), minVersion: 'TLSv1.2' }, serve)
  : http.createServer(options, serve);
server.maxConnections = 128;
server.setTimeout(95_000, socket => socket.destroy());
server.on('error', error => {
  console.error('지원 서버를 시작할 수 없습니다.', error.code || error.name);
  process.exitCode = 1;
});
server.listen(port, bind, () => {
  const address = bind.includes(':') ? `[${bind}]` : bind;
  console.log(`DLI 지원 서버 ${tls ? 'https' : 'http'}://${address}:${port}`);
  if (!configuredKey && !storedKey) console.log(`상담원 키 ${operatorKey}`);
});
