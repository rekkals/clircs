"use strict";

const HEADER_ID = "now-playing";
const API_VERSION = 20;
let socketId = 0;
let generation = 0;
let authenticated = false;
let reconnectTimer = 0;
let refreshTimer = 0;
let requestId = 0;
let lastError = "";
let environment = null;
let overview = null;

function setting(key, fallback) {
  return clircs.storage.get(key, fallback);
}

function boolSetting(key, fallback) {
  return setting(key, fallback ? "on" : "off") === "on";
}

function address() {
  return setting("address", "127.0.0.1");
}

function port() {
  const parsed = Number(setting("port", "7905"));
  return Number.isInteger(parsed) && parsed > 0 && parsed <= 65535 ? parsed : 7905;
}

function endpoint() {
  return `ws://${address()}:${port()}`;
}

function message(name, options) {
  requestId += 1;
  return JSON.stringify({
    name: name,
    type: "request",
    id: `clircs-musikcube-${requestId}`,
    device_id: "clircs",
    options: options || {}
  });
}

function send(name, options) {
  if (!authenticated || !socketId) {
    return false;
  }
  clircs.local.websocket.send(socketId, message(name, options));
  return true;
}

function connect() {
  if (!clircs.permissions.has("localNetwork")) {
    lastError = "localNetwork permission is not granted";
    return false;
  }
  if (socketId) {
    return true;
  }

  const currentGeneration = ++generation;
  lastError = "";
  socketId = clircs.local.websocket.connect(endpoint(), event => {
    if (currentGeneration !== generation) {
      return;
    }
    handleSocketEvent(event);
  });
  return true;
}

function stopRefreshLoop() {
  if (refreshTimer) {
    clircs.clearTimeout(refreshTimer);
    refreshTimer = 0;
  }
}

function scheduleRefresh() {
  stopRefreshLoop();
  if (!authenticated) {
    return;
  }
  refreshTimer = clircs.setTimeout(() => {
    refreshTimer = 0;
    if (authenticated) {
      send("get_playback_overview");
      scheduleRefresh();
    }
  }, 2000);
}

function disconnect(scheduleReconnect) {
  generation += 1;
  stopRefreshLoop();
  if (reconnectTimer) {
    clircs.clearTimeout(reconnectTimer);
    reconnectTimer = 0;
  }
  if (socketId) {
    clircs.local.websocket.close(socketId);
  }
  socketId = 0;
  authenticated = false;
  environment = null;
  overview = null;
  clircs.ui.clearHeader(HEADER_ID);
  if (scheduleReconnect) {
    reconnectTimer = clircs.setTimeout(() => {
      reconnectTimer = 0;
      connect();
    }, 5000);
  }
}

function handleSocketEvent(event) {
  if (event.type === "open") {
    clircs.local.websocket.send(socketId, message("authenticate", {
      password: clircs.secrets.get("password") || ""
    }));
    return;
  }
  if (event.type === "message") {
    handleMessage(event.data);
    return;
  }
  if (event.type === "error") {
    lastError = event.message || "connection failed";
    return;
  }
  if (event.type === "close") {
    const closedSocket = socketId;
    socketId = 0;
    if (closedSocket) {
      clircs.local.websocket.close(closedSocket);
    }
    authenticated = false;
    stopRefreshLoop();
    overview = null;
    clircs.ui.clearHeader(HEADER_ID);
    if (!reconnectTimer) {
      reconnectTimer = clircs.setTimeout(() => {
        reconnectTimer = 0;
        connect();
      }, 5000);
    }
  }
}

function handleMessage(text) {
  let incoming;
  try {
    incoming = JSON.parse(text);
  }
  catch (_) {
    lastError = "musikcube returned invalid JSON";
    return;
  }

  if (incoming.name === "authenticate") {
    const options = incoming.options || {};
    if (!options.authenticated) {
      lastError = "authentication failed";
      disconnect(false);
      return;
    }
    authenticated = true;
    environment = options.environment || {};
    if (Number(environment.api_version) !== API_VERSION) {
      lastError = `unsupported musikcube API ${environment.api_version || "unknown"}; expected ${API_VERSION}`;
      disconnect(false);
      return;
    }
    send("get_playback_overview");
    scheduleRefresh();
    return;
  }

  if (incoming.name === "get_playback_overview" ||
      incoming.name === "playback_overview_changed") {
    overview = incoming.options || {};
    updateHeader();
  }
}

function formatDuration(value) {
  const total = Math.max(0, Math.round(Number(value) || 0));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`
    : `${minutes}:${String(seconds).padStart(2, "0")}`;
}

function nowPlayingText() {
  if (!overview || !overview.playing_track ||
      overview.state === "stopped" || overview.state === "invalid") {
    return null;
  }
  const track = overview.playing_track;
  const artist = track.artist || track.album_artist || "Unknown artist";
  const title = track.title || "Unknown track";
  const prefix = overview.state === "paused" ? "Paused" : "Playing";
  return `${prefix}: ${artist} - ${title} [${formatDuration(overview.playing_duration)}]`;
}

function updateHeader() {
  const text = nowPlayingText();
  if (!text || !boolSetting("showHeader", true)) {
    clircs.ui.clearHeader(HEADER_ID);
    return;
  }
  clircs.ui.setHeader(HEADER_ID, text, {
    scope: "all",
    priority: 100,
    minimumWidth: 24
  });
}

function requireConnection() {
  if (authenticated) {
    return null;
  }
  connect();
  return `musikcube is not connected${lastError ? `: ${lastError}` : ""}`;
}

function musicStatus() {
  if (!clircs.permissions.has("localNetwork")) {
    return "musikcube needs permission: /script permissions musikcube localnetwork on";
  }
  if (!authenticated) {
    return `musikcube: disconnected from ${endpoint()}${lastError ? ` (${lastError})` : ""}`;
  }
  const version = environment && environment.app_version ? ` v${environment.app_version}` : "";
  const playing = nowPlayingText();
  return `musikcube${version}: connected to ${endpoint()}${playing ? `; ${playing}` : "; nothing playing"}`;
}

clircs.registerCommand("song", [], "Send musikcube's current track; use --local to display it only in clircs: /song [--local]", context => {
  if (context.args.length > 1 ||
      (context.args.length === 1 && context.args[0] !== "--local")) {
    return "Usage: /song [--local]";
  }
  const failure = requireConnection();
  if (failure) {
    return failure;
  }
  const text = nowPlayingText();
  if (!text) {
    return "musikcube is not playing anything";
  }
  if (context.args[0] === "--local") {
    return text;
  }
  if (!clircs.permissions.has("irc")) {
    return "musikcube needs permission: /script permissions musikcube irc on";
  }
  clircs.run(`/say ${text}`);
  return null;
});

clircs.registerCommand("music", [], "Inspect, configure, or control musikcube playback: /music <operation> [arguments]", context => {
  const args = context.args;
  const operation = (args[0] || "status").toLowerCase();

  if (operation === "status") {
    if (authenticated) {
      send("get_playback_overview");
    }
    return musicStatus();
  }
  if (operation === "connect") {
    disconnect(false);
    connect();
    return `Connecting to musikcube at ${endpoint()}`;
  }
  if (operation === "disconnect") {
    disconnect(false);
    return "Disconnected from musikcube";
  }
  if (operation === "password") {
    if (!clircs.secrets.prompt("password", "musikcube password")) {
      return "musikcube password was not changed";
    }
    disconnect(false);
    connect();
    return "musikcube password changed";
  }
  if (operation === "address") {
    if (args.length !== 2) {
      return `musikcube address: ${address()}`;
    }
    clircs.storage.set("address", args[1]);
    disconnect(false);
    connect();
    return `musikcube address changed to ${args[1]}`;
  }
  if (operation === "port") {
    const requested = Number(args[1]);
    if (args.length !== 2 || !Number.isInteger(requested) || requested < 1 || requested > 65535) {
      return "Usage: /music port <1-65535>";
    }
    clircs.storage.set("port", String(requested));
    disconnect(false);
    connect();
    return `musikcube port changed to ${requested}`;
  }
  if (operation === "bar") {
    if (args[1] !== "on" && args[1] !== "off") {
      return `musikcube header bar is ${boolSetting("showHeader", true) ? "on" : "off"}`;
    }
    clircs.storage.set("showHeader", args[1]);
    updateHeader();
    return `musikcube header bar turned ${args[1]}`;
  }

  const failure = requireConnection();
  if (failure) {
    return failure;
  }

  if (operation === "play") {
    if (!overview || overview.state !== "playing") {
      send("pause_or_resume");
    }
    return "musikcube playback started";
  }
  if (operation === "pause") {
    if (overview && (overview.state === "playing" || overview.state === "prepared")) {
      send("pause_or_resume");
    }
    return "musikcube playback paused";
  }
  if (operation === "stop") {
    send("stop");
    return "musikcube playback stopped";
  }
  if (operation === "next" || operation === "previous") {
    send(operation);
    return `musikcube: ${operation}`;
  }
  if (operation === "volume") {
    const requested = Number(args[1]);
    if (args.length !== 2 || !Number.isFinite(requested) || requested < 0 || requested > 100) {
      return "Usage: /music volume <0-100>";
    }
    send("set_volume", { volume: requested / 100 });
    return `musikcube volume changed to ${Math.round(requested)}%`;
  }

  return "Usage: /music status|connect|disconnect|password|address [host]|port [number]|bar <on|off>|play|pause|stop|next|previous|volume <0-100>";
});

if (clircs.permissions.has("localNetwork")) {
  connect();
}
