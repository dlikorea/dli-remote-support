import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createServer } from 'node:net';
import { mkdtemp, mkdir, writeFile, unlink, rmdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const relay = fileURLToPath(new URL('../server.js', import.meta.url));

async function startRelay(feed, storeProductId = '') {
  const storeProductFile = path.join(feed, 'store-product-id.txt');
  if (storeProductId) await writeFile(storeProductFile, storeProductId);
  else await unlink(storeProductFile).catch(error => { if (error.code !== 'ENOENT') throw error; });
  const reservation = createServer();
  reservation.listen(0, '127.0.0.1');
  await once(reservation, 'listening');
  const port = reservation.address().port;
  await new Promise(resolve => reservation.close(resolve));
  const env = Object.fromEntries(Object.entries(process.env).filter(([name]) => !name.startsWith('DLI_')));
  Object.assign(env, {
    DLI_PORT: String(port), DLI_BIND: '127.0.0.1',
    DLI_OPERATOR_KEY: 'download-route-test-only', DLI_UPDATE_DIR: feed,
  });
  const child = spawn(process.execPath, [relay], { env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '';
  await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => { child.kill(); reject(new Error(`Relay startup timed out: ${output}`)); }, 5000);
    child.once('error', error => { clearTimeout(timeout); reject(error); });
    child.once('exit', code => { clearTimeout(timeout); reject(new Error(`Relay exited ${code}: ${output}`)); });
    child.stderr.on('data', bytes => { output += bytes; });
    child.stdout.on('data', bytes => {
      output += bytes;
      if (output.includes(`http://127.0.0.1:${port}`)) { clearTimeout(timeout); resolve(); }
    });
  });
  return {
    url: `http://127.0.0.1:${port}`,
    async stop() { const exited = once(child, 'exit'); child.kill(); await exited; },
  };
}

test('Store download preserves the existing signed update feed and legacy fallback', async () => {
  const feed = await mkdtemp(path.join(tmpdir(), 'dli-download-test-'));
  const version = '1.2.0.0';
  const manifest = JSON.stringify({ version, signature: 'fixture-signed-legacy-manifest' });
  const artifact = Buffer.from('legacy installer fixture');
  const versionDir = path.join(feed, version);
  const manifestFile = path.join(feed, 'manifest.json');
  const artifactFile = path.join(versionDir, 'DLI-RemoteSupport-Setup.exe');
  await mkdir(versionDir);
  await writeFile(manifestFile, manifest);
  await writeFile(artifactFile, artifact);
  let active;
  try {
    active = await startRelay(feed, '9P0HBM1W0KKZ');
    let response = await fetch(`${active.url}/download`, { redirect: 'manual' });
    assert.equal(response.status, 302);
    assert.equal(response.headers.get('location'), 'https://get.microsoft.com/installer/download/9P0HBM1W0KKZ?referrer=appbadge');
    assert.equal(response.headers.get('cache-control'), 'no-store');
    assert.equal((await fetch(`${active.url}/updates/manifest.json`)).status, 200);
    assert.equal(await (await fetch(`${active.url}/updates/manifest.json`)).text(), manifest);
    assert.deepEqual(Buffer.from(await (await fetch(`${active.url}/updates/${version}/DLI-RemoteSupport-Setup.exe`)).arrayBuffer()), artifact);
    assert.equal((await fetch(`${active.url}/updates/../server.js`)).status, 404);
    assert.equal((await fetch(`${active.url}/healthz`)).status, 200);
    await active.stop(); active = null;
    active = await startRelay(feed);
    response = await fetch(`${active.url}/download`, { redirect: 'manual' });
    assert.equal(response.status, 302);
    assert.equal(response.headers.get('location'), `/updates/${version}/DLI-RemoteSupport-Setup.exe`);
  } finally {
    if (active) await active.stop();
    await unlink(artifactFile); await rmdir(versionDir);
    await unlink(manifestFile); await rmdir(feed);
  }
});

test('an invalid Store ID fails startup instead of allowing an arbitrary redirect', async () => {
  const feed = await mkdtemp(path.join(tmpdir(), 'dli-download-test-'));
  try { await assert.rejects(startRelay(feed, 'https://example.com/'), /store-product-id\.txt/); }
  finally { await unlink(path.join(feed, 'store-product-id.txt')); await rmdir(feed); }
});
