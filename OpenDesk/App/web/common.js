'use strict';
// Shared by the host and viewer pages. The app posts JSON messages with a `t` type field; binary
// data crosses the bridge as base64. MessageKinds (the C# MessageKind numbers) is injected by the app.
const post = message => chrome.webview.postMessage(message);
window.onerror = message => post({ t: 'error', message: String(message) });
window.onunhandledrejection = e => post({ t: 'error', message: String(e.reason?.message ?? e.reason) });

function toBase64(bytes) {
  let text = '';
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  return btoa(text);
}
// Decodes base64 into `target` at `offset` (a new array when omitted) and returns the bytes written.
function fromBase64(text, target, offset = 0) {
  const binary = atob(text), out = target ?? new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) out[offset + i] = binary.charCodeAt(i);
  return target ? binary.length : out;
}
const decodedLength = text => text.length / 4 * 3 - (text.endsWith('==') ? 2 : text.endsWith('=') ? 1 : 0);

// A Core Message frame: kind, big-endian length, payload.
function pack(kind, payload = new Uint8Array(0)) {
  const packet = new Uint8Array(5 + payload.length);
  packet[0] = kind; new DataView(packet.buffer).setInt32(1, payload.length); packet.set(payload, 5);
  return packet;
}

// Waits until ICE gathering completes, or 3 s, so the description carries its candidates (no trickle).
function gathered(pc) {
  return new Promise(resolve => {
    if (pc.iceGatheringState === 'complete') return resolve();
    pc.addEventListener('icegatheringstatechange', () => { if (pc.iceGatheringState === 'complete') resolve(); });
    setTimeout(resolve, 3000);
  });
}

// Video frames: flags (bit 0 keyframe), display generation, timestamp, length, then H.264 Annex B,
// in 60 KB messages.
const VIDEO_HEADER = 21, VIDEO_PIECE = 60000;

// Sends one packed message, then reports back to the app once the channel has drained below 1 MB.
function sendWithAck(channel, bytes, ack) {
  if (channel?.readyState !== 'open') { post({ t: 'sent', ack, failed: true }); return; }
  channel.send(bytes);
  if (channel.bufferedAmount < 1048576) { post({ t: 'sent', ack }); return; }
  channel.pendingAcks ??= [];
  channel.pendingAcks.push(ack);
  channel.bufferedAmountLowThreshold = 262144;
  channel.onbufferedamountlow = () => { for (const id of channel.pendingAcks.splice(0)) post({ t: 'sent', ack: id }); };
}
function failAcks(channel) { for (const id of channel?.pendingAcks?.splice(0) ?? []) post({ t: 'sent', ack: id, failed: true }); }

// Whether the selected ICE path is direct or through TURN, and its round trip.
async function route(pc) {
  const report = await pc.getStats();
  let pair;
  report.forEach(s => { if (s.type === 'transport' && s.selectedCandidatePairId) pair = report.get(s.selectedCandidatePairId); });
  if (!pair) return null;
  const local = report.get(pair.localCandidateId), remote = report.get(pair.remoteCandidateId);
  return { relay: local?.candidateType === 'relay' || remote?.candidateType === 'relay', rtt: Math.round((pair.currentRoundTripTime ?? 0) * 1000), protocol: local?.protocol ?? '' };
}
