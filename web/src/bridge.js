// Thin bridge to the native shell (the Windows app, through WebView2). In a plain browser
// every call is a no-op, so the same bundle runs under `vite dev` too.

const webview = window.chrome?.webview;

export const isNative = Boolean(webview);

// 'windows' inside the app; ?platform=… in a browser to work on another layout.
export const platform = window.__HOUSE_BOOT__?.platform
  || new URLSearchParams(location.search).get('platform') || (webview ? 'windows' : 'web');
export const isPhone = platform === 'ios';
export const isWindows = platform === 'windows' || (!webview && platform === 'web');

export function post(type, payload = {}) {
  webview?.postMessage({ type, ...payload });
}

// A tap you can feel: there is nothing to feel on a PC, so this is a no-op kept for the
// shared UI code that calls it.
export function haptic() {}

// Native -> JS entry points are attached here by main.js.
export const incoming = (window.house = window.house || {});

// Request/response on top of the one-way message channel.
let nextId = 1;
const pending = new Map();

export function call(type, payload = {}) {
  if (!webview) return Promise.reject(new Error('Only available inside the app'));
  return new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    webview.postMessage({ type, callId: id, ...payload });
  });
}

incoming.reply = (id, result) => {
  const p = pending.get(id);
  if (!p) return;
  pending.delete(id);
  if (result && result.error) p.reject(Object.assign(new Error(result.error), { detail: result }));
  else p.resolve(result);
};
