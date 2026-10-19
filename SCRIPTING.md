# clircs Scripting Guide

clircs scripting is a deliberately small extension system built on the embedded Jint JavaScript interpreter. An addon can add commands, observe semantic IRC events, keep private settings, use bounded timers, and contribute small items to the window header.

It is narrower than mIRC scripting, a browser, Node.js, or unrestricted .NET on purpose. Scripts receive only the `clircs` APIs described here. They do not receive arbitrary filesystem access, process launching, registry access, raw sockets, unrestricted CLR access, or unrestricted Internet access.

Scripts are still code. A sandbox is a boundary, not a certificate of good intentions. Read a script before loading it, inspect the permissions it requests, and grant `irc` or `localnetwork` only when you trust what it does.

## Installing and Managing Scripts

A script is not a package ecosystem. It is one directory containing a manifest named `clircs-script.json` and a JavaScript entry file:

```text
my-script\
  clircs-script.json
  main.js
```

User-installed scripts belong under:

```text
%APPDATA%\clircs\scripts\<script-id>\
```

clircs can also discover installed scripts under a `scripts` directory next to `clircs.exe`. The v0.9.0 release packages do not include any; the example scripts remain in the source repository. If an executable-adjacent script and a user-installed script have the same ID, the user-installed script takes precedence.

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

Loading and unloading are remembered immediately. A script that was loaded when clircs closed is loaded again at the next startup. One broken addon does not get to keep the others from loading.

Changing a permission reloads the script when it is already loaded. `/script errors` shows the 20 most recent recorded errors from the current clircs process.

## Manifest

A manifest tells clircs which script it is looking at, which file to execute, and which capabilities the script expects to use. Keeping that contract explicit is boring in the useful way.

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
    "localnetwork",
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

clircs executes one classic JavaScript entry file. It does not expose Node.js, a browser DOM, `require()`, package installation, or JavaScript module loading. If an addon expects npm to be waiting nearby, it has arrived at the wrong building.

## Permissions

A manifest must request a permission before the corresponding API can be used. Requesting a capability and being granted it are separate things for the two permissions that can act outside the script itself. Loading fails if the entry file immediately calls an API whose permission is not granted.

| Permission | API or capability |
| --- | --- |
| `commands` | Register clircs commands and aliases. |
| `events` | Observe semantic client and IRC events. |
| `timers` | Create and cancel bounded one-shot timers. |
| `output` | Print sanitized text in clircs. |
| `storage` | Read and write private durable string settings. |
| `irc` | Queue an existing clircs slash command through the normal command lane. |
| `localnetwork` | Open text WebSockets restricted to the local machine. |
| `secrets` | Prompt for and store values protected for the current Windows user. |
| `ui` | Contribute constrained items to the window header. |

Requested `commands`, `events`, `timers`, `output`, `storage`, `secrets`, and `ui` permissions are granted when a script is first loaded. The more powerful `irc` and `localnetwork` permissions are denied until the user explicitly grants them:

```text
/script permissions my-script irc on
/script permissions my-script localnetwork on
```

The `irc` permission controls `clircs.run()`. It opens a controlled route through the client, not around it. It permits only host-approved IRC automation commands, including messaging, channel operations, moderation, server queries, and services commands. Command aliases resolve to the same permission as their canonical commands. Raw protocol access, DCC, filesystem operations, configuration changes, credential management, connection management, and application administration are not available through `clircs.run()`. Unknown and unapproved commands fail closed. Allowed commands still pass through the ordinary parser, validation, captured network/window context, command execution lane, and outbound scheduler. Scripts never receive direct access to an IRC socket.

Local means local here. The `localnetwork` permission permits only `ws://` and `wss://` WebSockets whose host is `localhost` or a loopback IP address. It does not permit general HTTP requests or connections to external hosts.

A script can test a permission without causing an error:

```javascript
if (!clircs.permissions.has("localnetwork")) {
  clircs.print("Grant localnetwork permission first.");
}
```

## A Minimal Script

Here is a complete addon with one command and no machinery it does not need.

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

The global host object is named `clircs`. It is the script's entire doorway into the client.

### Printing Output

```javascript
clircs.print("hello");
```

Requires `output`. Text is prefixed with the script ID when displayed, terminal controls are sanitized, and one call cannot exceed 4,096 characters.

### Registering Commands

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

### Observing Events

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

Events are delivered after clircs has applied protocol state and application routing. Scripts observe semantic events rather than parsing themed terminal output, so changing a theme does not quietly break an addon. Stable IDs distinguish identical channel names on simultaneous networks.

One script may register at most 256 event handlers.

### Requesting clircs Commands

```javascript
clircs.run("/notice Nick hello");
```

Requires `irc`. Only slash commands are accepted. The command is queued asynchronously using the network and window context active for the script callback that requested it.

Despite the name, `clircs.run()` runs a clircs command. It is not a Windows shell command and cannot launch an executable.

### Timers

```javascript
const timerId = clircs.setTimeout(() => clircs.print("done"), 1000);
clircs.clearTimeout(timerId);
```

Requires `timers`. Timers are one-shot and accept delays from 10 milliseconds through 24 hours. A script may have at most 256 active timers. All of its timers are canceled when it unloads or faults.

### Private Storage

```javascript
const value = clircs.storage.get("key", "fallback");
clircs.storage.set("key", "value");
clircs.storage.remove("key");
```

Requires `storage`. Keys and values are strings. Each script receives its own JSON file under `%APPDATA%\clircs\script-data`. Private means isolated from other scripts here, not encrypted.

Keys cannot exceed 128 characters, an individual value cannot exceed 65,536 characters, and one script's complete storage file cannot exceed one MiB. Ordinary script storage is not encrypted; use the secrets API for credentials.

### Windows-Protected Secrets

```javascript
if (!clircs.secrets.get("password")) {
  clircs.secrets.prompt("password", "Service password");
}

const password = clircs.secrets.get("password");
clircs.secrets.set("token", "value supplied some other way");
clircs.secrets.remove("password");
```

Requires `secrets`. `prompt()` uses masked terminal input and returns `true` when a value was entered or `false` when the prompt was canceled. Time spent waiting for the user is not charged against the script's execution timeout.

Secret keys contain 1–128 characters, and values cannot exceed 16,384 characters. Values are encrypted with Windows DPAPI for the current Windows user and stored under `%APPDATA%\clircs\script-secrets`. Backups contain the encrypted files, but another Windows account cannot decrypt them. Enter the secrets again after restoring under another account.

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

Requires `localnetwork`. This API is for local companion services, not a narrow-looking route to the wider Internet. Callback event types are `open`, `message`, `error`, and `close`. Messages are text-only and cannot exceed one MiB. Connections time out after five seconds, a script may open at most 16 WebSockets, and all of its connections close when it unloads or faults.

Callbacks for events, timers, commands, and WebSockets are serialized for each script.

### Window-Header Items

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

Header contributions are suggestions, not ownership of the terminal. The channel topic remains the primary content, and the presenter decides which auxiliary items fit at the current width. All contributions disappear when their script unloads or faults.

## Execution Limits and Failure Isolation

Every script receives its own Jint engine and serialized callback queue. The limits below are hard guardrails, not performance recommendations. Each execution is constrained to:

- 25,000 JavaScript statements;
- 250 milliseconds of execution time;
- recursion depth 64;
- 16 MiB of tracked memory;
- JavaScript arrays no larger than 100,000 elements.

A script also cannot queue more than 512 pending callbacks. Exceeding a hard execution or queue limit faults that script. Three consecutive ordinary handler failures also fault it.

Faulting a script unregisters its commands, cancels its timers, removes its header items, and closes its local WebSockets. A script can ruin its own afternoon; it does not get to disconnect IRC sessions or disable other scripts. Inspect failures with:

```text
/script errors
/script list
```

## Examples in the Source Tree

The source repository contains two examples under `examples`:

- `script-demo` demonstrates a registered command, semantic JOIN events, a bounded timer, and private storage.
- `musikcube` is a (mostly) complete (and sort of buggy) addon that controls an already-running local musikcube WebSocket service, stores its password with Windows protection, and contributes playback information to the header.

Neither example is included in the v0.9.0 release packages. They remain available in the source tree for anyone who wants to inspect or install them. See their individual README files for commands and setup.

## Author Checklist

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
