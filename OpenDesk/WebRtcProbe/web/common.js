const post = message => chrome.webview.postMessage(message);
let pc;
window.onerror = (message) => post({ type: 'error', message: String(message) });
window.onunhandledrejection = e => post({ type: 'error', message: String(e.reason?.message ?? e.reason) });

// Video RTP stats plus the negotiated codec and the selected candidate pair's round trip.
async function videoStats(kind) {
  const report = await pc.getStats(), out = {};
  report.forEach(s => { if (s.type === kind && s.kind === 'video') Object.assign(out, s); });
  report.forEach(s => {
    if (s.type === 'codec' && s.id === out.codecId) out.codec = s.mimeType;
    if (s.type === 'candidate-pair' && s.nominated && s.state === 'succeeded') { out.pairRttMs = s.currentRoundTripTime * 1000; out.pair = s.localCandidateId + ' -> ' + s.remoteCandidateId; }
  });
  return out;
}
