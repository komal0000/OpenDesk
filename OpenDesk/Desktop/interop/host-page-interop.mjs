// Interop check: the .NET viewer (OpenDesk.Viewer --probe) against the real Windows host page
// (App/web/host.html) running in Chromium, through a real OpenDesk.Service on this machine.
// This node script plays HostEngine's part: it registers over v2/host with a fresh device key, approves
// the viewer, signs the answer, sends Accepted, feeds H.264 frames to the page and counts input events.
//
// Needs: node 18+, the playwright package with Chromium, ffmpeg, a built Service and Desktop project.
// Usage (from OpenDesk/): node Desktop/interop/host-page-interop.mjs
import { createRequire } from 'node:module';
import { spawn, execFileSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
const { chromium } = createRequire(import.meta.url)('playwright');

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname), '../..');
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'opendesk-interop-'));
const port = 47900 + Math.floor(Math.random() * 90);
const log = (...a) => console.log(`[${(performance.now() / 1000).toFixed(2)} s]`, ...a);
const children = [];
process.on('exit', () => children.forEach(c => c.kill()));

// System.Text.Json's default escaping, so signed bytes match SignalProof on the C# side.
const stj = value => JSON.stringify(value).replace(/[+<>&'`]/g, c => '\\u' + c.charCodeAt(0).toString(16).toUpperCase().padStart(4, '0'));
const pack = (kind, payload = Buffer.alloc(0)) => { const b = Buffer.alloc(5 + payload.length); b[0] = kind; b.writeInt32BE(payload.length, 1); payload.copy(b, 5); return b; };

// 1. Test video: 1280x720, no B-frames, a keyframe every second, one access unit per frame.
const h264 = path.join(work, 'video.h264');
execFileSync('ffmpeg', ['-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=1280x720:rate=30', '-frames:v', '300', '-c:v', 'libx264', '-preset', 'ultrafast',
  '-tune', 'zerolatency', '-bf', '0', '-g', '30', '-pix_fmt', 'yuv420p', '-x264-params', 'aud=1', '-f', 'h264', h264]);
const stream = fs.readFileSync(h264), units = [];
for (let i = 0, start = -1; i <= stream.length; i++) {
  const aud = i + 4 < stream.length && stream[i] === 0 && stream[i + 1] === 0 && stream[i + 2] === 0 && stream[i + 3] === 1 && (stream[i + 4] & 0x1f) === 9;
  if (aud || i === stream.length) { if (start >= 0) units.push(stream.subarray(start, i)); start = i; }
}
const isKey = u => { for (let i = 0; i + 3 < u.length; i++) if (u[i] === 0 && u[i + 1] === 0 && u[i + 2] === 1 && (u[i + 3] & 0x1f) === 5) return true; return false; };
log(`video: ${units.length} access units, ${units.filter(isKey).length} keyframes`);

// 2. The device service, loopback only, with its own data folder.
const service = spawn('dotnet', [path.join(root, 'Service/bin/Debug/net10.0/OpenDesk.Service.dll'), '--data', path.join(work, 'service'), '--port', String(port), '--relay-port', String(port + 1), '--loopback']);
children.push(service);
const pin = await new Promise((resolve, reject) => {
  let text = '';
  service.stdout.on('data', d => { text += d; const m = text.match(/fingerprint: ([0-9A-F]{64})/); if (m) resolve(m[1]); });
  service.on('exit', code => reject(new Error('service exited ' + code + ' ' + text)));
});
log('service up, pin', pin.slice(0, 12) + '…');

// 3. The host page in Chromium, with a chrome.webview shim in place of WebView2.
const browser = await chromium.launch();
const page = await browser.newPage();
const handlers = {};
await page.exposeFunction('toNode', m => handlers.onPage?.(m));
await page.addInitScript(() => {
  const listeners = [];
  window.chrome = { webview: { postMessage: m => window.toNode(m), addEventListener: (_, f) => listeners.push(f) } };
  window.fromNode = m => listeners.forEach(f => f({ data: m }));
});
await page.route('http://app.opendesk/**', route => route.fulfill({ path: path.join(root, 'App/web', new URL(route.request().url()).pathname.slice(1)) }));
const pageReady = new Promise(r => handlers.ready = r);
const toPage = m => page.evaluate(x => window.fromNode(x), m);

// 4. Host signaling: register with a fresh P-256 device key.
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'; // the service certificate is self-signed; the probe pins it
const { privateKey, publicKey: spki } = crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' });
const publicKey = spki.export({ type: 'spki', format: 'der' }).toString('base64');
const sign = value => crypto.sign('sha256', Buffer.from(stj(value)), { key: privateKey, dsaEncoding: 'ieee-p1363' }).toString('base64');
const socket = new WebSocket(`wss://127.0.0.1:${port}/v2/host`);
const send = m => socket.send(JSON.stringify(m));
let id, registered, offers = new Map(), inputs = [], keyframeRequests = 0, ack = 0;
const isRegistered = new Promise(r => registered = r);
const status = { Control: true, Epoch: 1, Text: 'Connected', Clipboard: false, Files: false, FrameAcks: false, Tiles: false, Display: 0 };
socket.onmessage = async e => {
  const m = JSON.parse(e.data);
  if (m.type === 'challenge') send({ type: 'register', publicKey, signature: sign({ Scope: 'OpenDesk signaling v2 register', Challenge: m.challenge, PublicKey: publicKey }) });
  else if (m.type === 'registered') { id = m.id; registered(); }
  else if (m.type === 'offer') {
    log(`offer from "${m.name}" (${m.sdp.split('\n').filter(l => l.startsWith('a=candidate')).length} candidates): approving`);
    offers.set(m.session, m.sdp);
    await toPage({ t: 'offer', session: m.session, sdp: m.sdp, ice: m.ice ?? [] });
  }
  else log('signal', m.type, m.reason ?? '');
};
handlers.onPage = m => {
  if (m.t === 'ready') handlers.ready();
  else if (m.t === 'answer') {
    const offer = offers.get(m.session);
    send({ type: 'answer', session: m.session, sdp: m.sdp, signature: sign({ Scope: 'OpenDesk signaling v2 answer', Id: id, Session: m.session, Offer: offer, Answer: m.sdp }) });
    log('signed answer sent');
  }
  else if (m.t === 'open') { log('control channel open: sending Accepted'); toPage({ t: 'send', session: m.session, ch: 'control', data: pack(2, Buffer.from(JSON.stringify(status))).toString('base64'), ack: ++ack }); }
  else if (m.t === 'recv') {
    const b = Buffer.from(m.data, 'base64');
    if (b[0] === 5) inputs.push(JSON.parse(b.subarray(5).toString()));
    else if (b[0] === 16) keyframeRequests++;
  }
  else if (m.t === 'keyframe') keyframeRequests++;
  else if (m.t === 'error') log('page error:', m.message);
  else if (m.t === 'closed') log('peer closed', m.session);
};
await page.goto('http://app.opendesk/host.html');
await pageReady;
await isRegistered;
log('host registered as', id);

// 5. Video at 30 fps, as HostEngine posts it.
let next = 0;
const video = setInterval(() => { const u = units[next++ % units.length]; toPage({ t: 'video', key: isKey(u), display: 0, data: Buffer.from(u).toString('base64') }); }, 33);

// 6a. With --gui <file.png>: the real viewer window instead (needs DISPLAY, for example under xvfb-run),
// connected for a few seconds, then a screenshot of the whole display.
const gui = process.argv.indexOf('--gui');
if (gui > 0) {
  const settingsFile = path.join(work, 'viewer.json');
  fs.writeFileSync(settingsFile, JSON.stringify({ ServiceUrl: `https://127.0.0.1:${port}`, ServicePin: pin, DisplayName: 'Interop window' }));
  const app = spawn('dotnet', [path.join(root, 'Desktop/bin/Debug/net10.0/OpenDesk.Viewer.dll'), '--connect', id], { env: { ...process.env, OPENDESK_VIEWER_SETTINGS: settingsFile } });
  children.push(app);
  app.stderr.on('data', d => process.stderr.write('  viewer err: ' + d));
  await new Promise(r => setTimeout(r, 9000));
  const shot = path.resolve(process.argv[gui + 1]);
  execFileSync('ffmpeg', ['-v', 'error', '-y', '-f', 'x11grab', '-i', process.env.DISPLAY, '-frames:v', '1', shot]);
  clearInterval(video);
  log(`screenshot ${shot}; host received ${inputs.length} input events, ${keyframeRequests} keyframe requests`);
  app.kill(); await browser.close(); service.kill();
  process.exit(0);
}

// 6. The .NET viewer, without a window.
const settings = path.join(work, 'viewer.json');
fs.writeFileSync(settings, JSON.stringify({ ServiceUrl: `https://127.0.0.1:${port}`, ServicePin: pin, DisplayName: 'Interop probe' }));
const probe = spawn('dotnet', [path.join(root, 'Desktop/bin/Debug/net10.0/OpenDesk.Viewer.dll'), '--probe', id, '--seconds', '8'], { env: { ...process.env, OPENDESK_VIEWER_SETTINGS: settings } });
children.push(probe);
let output = '';
probe.stdout.on('data', d => { output += d; process.stdout.write('  viewer: ' + d.toString().replace(/\n(?=.)/g, '\n  viewer: ')); });
probe.stderr.on('data', d => process.stderr.write('  viewer err: ' + d));
const code = await new Promise(r => probe.on('exit', r));
clearInterval(video);
const moves = inputs.filter(i => i.Kind === 'move');
log(`viewer exit ${code}; host received ${inputs.length} input events (${moves.length} moves, epoch ${[...new Set(inputs.map(i => i.Epoch))]}), ${keyframeRequests} keyframe requests`);
await browser.close();
service.kill();
const decoded = Number(output.match(/Frames decoded: (\d+)/)?.[1] ?? 0);
const ok = code === 0 && decoded >= 100 && moves.length >= 5 && moves.every(m => m.X === 0.5 && m.Y === 0.5 && m.Epoch === 1);
console.log(ok ? 'PASS .NET viewer interoperates with host.html' : 'FAIL');
process.exit(ok ? 0 : 1);
