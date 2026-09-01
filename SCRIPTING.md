# clircs scripting guide

clircs scripts are optional JavaScript addons executed by the embedded Jint interpreter. They can add commands, observe semantic IRC events, keep private settings, use bounded timers, and contribute small items to the window header.

The scripting system is deliberately narrower than mIRC scripting, a browser, Node.js, or unrestricted .NET. Scripts receive only the `clircs` APIs described here. They do not receive arbitrary filesystem access, process launching, registry access, raw sockets, unrestricted CLR access, or unrestricted Internet access.

Scripts are still code. Read a script before loading it, inspect the permissions it requests, and grant `irc` or `localnetwork` only when you trust what it does.

## Installing and managing scripts

A script is one directory containing a manifest named `clircs-script.json` and a JavaScript entry file:

```text
my-script\
  clircs-script.json
  main.js
```

User-installed scripts belong under:

```text
%APPDATA%\clircs\scripts\<script-id>\
```

Release packages may also contain bundled scripts under the `scripts` directory next to `clircs.exe`. If a user-installed script and a bundled script have the same ID, the user-installed script takes precedence.

Use `/script` without arguments or `/script list` to inspect installed scripts. The complete management commands are:

```text
/script list
/script load <id>
/script unload <id>
/script reload <id>
/script errors
/script permissions
/script permissions <id>
/script permissions <id> <permission> on|off
```

Loading and unloading are remembered immediately. A script that was loaded when clircs closed is loaded again at the next startup. One script failing to load does not prevent other remembered scripts from loading.

Changing a permission reloads the script when it is already loaded. `/script errors` shows the 20 most recent recorded errors from the current clircs process.

## Manifest

A complete manifest looks like this:

```json
{
  "schemaVersion": 1,
  "id": "my-script",
  "name": "My script",
  "version": "1.0.0",
  "entry": "main.js",
  "permissions": [
    "commands",
    "events",
    "timers",
    "output",
    "storage",
    "irc",
    "localNetwork",
    "secrets",
    "ui"
  ]
}
```

The fields are:

| Field | Meaning |
| --- | --- |
| `schemaVersion` | Manifest format. The current and only supported value is `1`. |
| `id` | Stable script identity used by commands, permissions, storage, and errors. |
| `name` | Human-readable script name. |
| `version` | Script-supplied version text. |
| `entry` | JavaScript file executed when the script loads. |
| `permissions` | Capabilities requested from the host. |

Script IDs contain 1–64 lowercase letters, digits, dots, underscores, or hyphens and must begin with a letter or digit. The entry point must remain inside the script directory and cannot exceed 256 KiB.

clircs executes one classic JavaScript entry file. It does not expose Node.js, a browser DOM, `require()`, package installation, or JavaScript module loading.

## Permissions

A manifest must request a permission before the corresponding API can be used. Loading fails if the entry file immediately calls an API whose permission is not granted.

| Permission | API or capability |
| --- | --- |
| `commands` | Register clircs commands and aliases. |
| `events` | Observe semantic client and IRC events. |
| `timers` | Create and cancel bounded one-shot timers. |
| `output` | Print sanitized text in clircs. |
| `storage` | Read and write private durable string settings. |
| `irc` | Queue an existing clircs slash command through the normal command lane. |
| `localNetwork` | Open text WebSockets restricted to the local machine. |
| `secrets` | Prompt for and store values protected for the current Windows user. |
| `ui` | Contribute constrained items to the window header. |

Requested `commands`, `events`, `timers`, `output`, `storage`, `secrets`, and `ui` permissions are granted when a script is first loaded. The more powerful `irc` and `localnetwork` permissions are denied until the user explicitly grants them:

```text
/script permissions my-script irc on
/script permissions my-script localnetwork on
```

The `irc` permission controls `clircs.run()`. It permits a script to request registered clircs slash commands, including commands that may send IRC traffic or change client state. Those commands still pass through the ordinary parser, validation, captured network/window context, command execution lane, and outbound scheduler. Scripts cannot request `/script` and therefore cannot administer scripts or change their own permissions. Scripts never receive direct access to an IRC socket.

The `localnetwork` permission permits only `ws://` and `wss://` WebSockets whose host is `localhost` or a loopback IP address. It does not permit general HTTP requests or connections to external hosts.

A script can test a permission without causing an error:

```javascript
if (!clircs.permissions.has("localnetwork")) {
  clircs.print("Grant localnetwork permission first.");
}
```

## A minimal script

Create `%APPDATA%\clircs\scripts\hello\clircs-script.json`:

```json
{
  "schemaVersion": 1,
  "id": "hello",
  "name": "Hello example",
  "version": "1.0.0",
  "entry": "main.js",
  "permissions": ["commands"]
}
```

Create `main.js` in the same directory:

```javascript
clircs.registerCommand(
  "hello",
  ["hi"],
  "Say hello from a script.",
  context => `Hello ${context.args.join(" ") || "there"}!`
);
```

Then load and use it:

```text
/script load hello
/hello world
```

Edit `main.js` and run `/script reload hello` to load the new source.

## JavaScript API

The global host object is named `clircs`.

### Printing output

```javascript
clircs.print("hello");
```

Requires `output`. Text is prefixed with the script ID when displayed, terminal controls are sanitized, and one call cannot exceed 4,096 characters.

### Registering commands

```javascript
clircs.registerCommand("greet", ["gr"], "Greet somebody.", context => {
  const target = context.args[0] || "everybody";
  return `Hello, ${target}.`;
});
```

Requires `commands`. Arguments are:

1. canonical command name;
2. array of aliases;
3. help summary;
4. command handler.

The handler receives:

```javascript
{
  name,       // canonical command name
  args,       // array of parsed arguments
  networkId,  // stable ID, or null when no network context exists
  bufferId    // stable ID, or null when no window context exists
}
```

Returning `null` or `undefined` succeeds silently. Returning `false` reports command failure. Any other returned value is converted to text and displayed as a successful command result.

Script commands participate in the ordinary command registry, appear in `/help`, and are removed when the script unloads or faults. A script may register at most 64 commands.

### Observing events

```javascript
clircs.on("message", event => {
  clircs.print(`${event.networkId}: ${event.text}`);
});
```

Requires `events`. Use `"*"` to observe every semantic event kind. Current event names are:

```text
status server message highlight notice action join part nick topic
channelinfo channelsync messageguard mode protection error diagnostic
```

Each event contains:

```javascript
{
  kind,       // lowercase semantic event name
  text,       // safe plain-text representation
  networkId,  // stable network-session ID
  bufferId,   // stable destination-buffer ID
  timestamp,  // ISO 8601 timestamp
  fields      // event-specific semantic object, or null
}
```

When present, `fields` is an object whose names depend on the event. IRC events commonly provide values such as `nick`, `user`, `host`, `channel`, `actor`, `reason`, `modes`, or request-routing identifiers. Field values are strings or `null`; scripts should check that `fields` and the particular field exist before using them.

Events are delivered after clircs has applied protocol state and application routing. Scripts observe semantic events rather than parsing themed terminal output. Stable IDs distinguish identical channel names on simultaneous networks.

One script may register at most 256 event handlers.

### Requesting clircs commands

```javascript
clircs.run("/notice Nick hello");
```

Requires `irc`. Only slash commands are accepted. The command is queued asynchronously using the network and window context active for the script callback that requested it.

`clircs.run()` runs a clircs command. It is not a Windows shell command and cannot launch an executable.

### Timers

```javascript
const timerId = clircs.setTimeout(() => clircs.print("done"), 1000);
clircs.clearTimeout(timerId);
```

Requires `timers`. Timers are one-shot and accept delays from 10 milliseconds through 24 hours. A script may have at most 256 active timers. All of its timers are canceled when it unloads or faults.

### Private storage

```javascript
const value = clircs.storage.get("key", "fallback");
clircs.storage.set("key", "value");
clircs.storage.remove("key");
```

Requires `storage`. Keys and values are strings. Each script receives its own JSON file under `%APPDATA%\clircs\script-data`.

Keys cannot exceed 128 characters, an individual value cannot exceed 65,536 characters, and one script's complete storage file cannot exceed one MiB. Ordinary script storage is not encrypted; use the secrets API for credentials.

### Windows-protected secrets

```javascript
if (!clircs.secrets.get("password")) {
  clircs.secrets.prompt("password", "Service password");
}

const password = clircs.secrets.get("password");
clircs.secrets.set("token", "value supplied some other way");
clircs.secrets.remove("password");
```

Requires `secrets`. `prompt()` uses masked terminal input and returns `true` when a value was entered or `false` when the prompt was canceled. Time spent waiting for the user is not charged against the script's execution timeout.

Secret values are encrypted with Windows DPAPI for the current Windows user and stored under `%APPDATA%\clircs\script-secrets`. Backups contain the encrypted files, but another Windows account cannot decrypt them. Enter the secrets again after restoring under another account.

### Local WebSockets

```javascript
const socketId = clircs.local.websocket.connect(
  "ws://127.0.0.1:7905",
  event => {
    if (event.type === "open") {
      clircs.local.websocket.send(socketId, "hello");
    } else if (event.type === "message") {
      clircs.print(event.data);
    } else if (event.type === "error") {
      clircs.print(event.message);
    }
  }
);

clircs.local.websocket.close(socketId);
```

Requires `localnetwork`. Callback event types are `open`, `message`, `error`, and `close`. Messages are text-only and cannot exceed one MiB. Connections time out after five seconds, a script may open at most 16 WebSockets, and all of its connections close when it unloads or faults.

Callbacks for events, timers, commands, and WebSockets are serialized for each script.

### Window-header items

```javascript
clircs.ui.setHeader("example", "CPU: 7%", {
  scope: "all",
  priority: 10,
  minimumWidth: 12
});

clircs.ui.clearHeader("example");
```

Requires `ui`. Header IDs contain 1–64 letters, digits, dots, underscores, or hyphens. A script may contribute at most 32 items.

With `scope: "all"`, the item is available in every window. Supply `bufferId` to target a stable buffer explicitly. Without either option, the item uses the current callback's buffer context. `priority` is clamped from -1000 through 1000 and `minimumWidth` from 8 through 200.

The channel topic remains the primary header content. The presenter decides which auxiliary items fit at the current terminal width. All contributions disappear when their script unloads or faults.

## Execution limits and failure isolation

Every script receives its own Jint engine and serialized callback queue. Each execution is constrained to:

- 25,000 JavaScript statements;
- 250 milliseconds of execution time;
- recursion depth 64;
- 16 MiB of tracked memory;
- JavaScript arrays no larger than 100,000 elements.

A script also cannot queue more than 512 pending callbacks. Exceeding a hard execution or queue limit faults that script. Three consecutive ordinary handler failures also fault it.

Faulting a script unregisters its commands, cancels its timers, removes its header items, and closes its local WebSockets. It does not disconnect IRC sessions or disable other scripts. Inspect failures with:

```text
/script errors
/script list
```

## Bundled examples

The source repository contains two examples under `examples`:

- `script-demo` demonstrates a registered command, semantic JOIN events, a bounded timer, and private storage.
- `musikcube` is a complete addon that controls an already-running local musikcube WebSocket service, stores its password with Windows protection, and contributes playback information to the header.

Both are copied into release output as bundled scripts but remain inactive until explicitly loaded. See their individual README files for commands and setup.

## Author checklist

Before distributing a script:

1. Request only the permissions it actually uses.
2. Keep credentials in `clircs.secrets`, never ordinary storage or source code.
3. Check optional permissions with `clircs.permissions.has()` and report a useful instruction.
4. Use stable `networkId` and `bufferId` values instead of assuming names are globally unique.
5. Treat event fields as optional and event-specific.
6. Keep callbacks short; use a timer or local service rather than blocking the client.
7. Handle WebSocket `error` and `close` events.
8. Test load, reload, unload, startup restoration, permission revocation, and service failure.
9. Confirm `/script errors` remains empty during normal use.
