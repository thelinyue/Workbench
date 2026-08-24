/* Offline bridge for @xterm/xterm 6.0.0 and @xterm/addon-fit 0.11.0. */
(() => {
  'use strict';
  const VERSION = 'terminal-v1';
  const params = new URLSearchParams(location.search);
  const fontSize = Number(params.get('fontSize'));
  const terminal = new Terminal({
    cursorBlink: true,
    convertEol: false,
    fontFamily: params.get('fontFamily') || 'Cascadia Mono',
    fontSize: Number.isFinite(fontSize) && fontSize >= 10 && fontSize <= 24 ? fontSize : 14,
    scrollback: 5000,
    theme: {
      background: '#0f1720',
      foreground: '#e5edf5',
      cursor: '#60a5fa',
      selectionBackground: '#315b88'
    }
  });
  const fitAddon = new FitAddon.FitAddon();
  terminal.loadAddon(fitAddon);
  terminal.open(document.getElementById('terminal'));

  let requestId = 0;
  const post = message => window.chrome.webview.postMessage(JSON.stringify({ version: VERSION, ...message }));
  const toBase64 = bytes => {
    let binary = '';
    for (let offset = 0; offset < bytes.length; offset += 0x8000) {
      binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
    }
    return btoa(binary);
  };
  const fromBase64 = value => {
    const binary = atob(value);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
    return bytes;
  };

  terminal.onData(data => post({
    type: 'input',
    requestId: `input-${++requestId}`,
    data: toBase64(new TextEncoder().encode(data))
  }));
  terminal.onResize(size => post({
    type: 'resize',
    requestId: `resize-${++requestId}`,
    columns: size.cols,
    rows: size.rows
  }));

  window.chrome.webview.addEventListener('message', event => {
    const message = event.data;
    if (!message || message.version !== VERSION || message.type !== 'output' ||
        !Number.isSafeInteger(message.sequence) || message.sequence <= 0 || typeof message.data !== 'string') return;
    let bytes;
    try { bytes = fromBase64(message.data); } catch { return; }
    terminal.write(bytes, () => post({ type: 'ack', sequence: message.sequence }));
  });

  const fit = () => {
    try { fitAddon.fit(); } catch { return; }
  };
  new ResizeObserver(fit).observe(document.body);
  window.addEventListener('resize', fit);
  fit();
  terminal.focus();
})();
