const pending = new Map();
const listeners = new Set();
let nextId = 0;

function receive(event) {
  const message = event.data;
  if (!message || typeof message !== "object") return;
  if (message.type === "result" && pending.has(message.id)) {
    const { resolve, reject, timer } = pending.get(message.id);
    clearTimeout(timer);
    pending.delete(message.id);
    if (message.ok) resolve(message.data);
    else reject(new Error(message.error || "The command could not be completed."));
    return;
  }
  if (message.type === "event") for (const listener of listeners) listener(message.name, message.data);
}

if (window.chrome?.webview) window.chrome.webview.addEventListener("message", receive);
window.addEventListener("error", () => { void command("reportFrontendError", { kind:"script" }).catch(() => {}); });
window.addEventListener("unhandledrejection", () => { void command("reportFrontendError", { kind:"rejection" }).catch(() => {}); });

export function command(name, payload = {}) {
  if (!window.chrome?.webview) return Promise.reject(new Error("The native Music Player connection is unavailable."));
  const id = `web-${++nextId}`;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(new Error("The request timed out.")); }, 30000);
    pending.set(id, { resolve, reject, timer });
    window.chrome.webview.postMessage({ type: "command", id, name, payload });
  });
}

export function onEvent(listener) { listeners.add(listener); return () => listeners.delete(listener); }
