# clircs Scripting Guide

clircs scripting is a deliberately small extension system built on the embedded Jint JavaScript interpreter. An addon can add commands, observe semantic IRC events, write local output to existing or script-created windows, keep private settings, use bounded timers, and contribute small items to the window header.

Scripts extend clircs's behavior. Event handlers observe its normal processing and can add output; they cannot suppress, rewrite, or reroute the original event.

It is narrower than mIRC scripting, a browser, Node.js, or unrestricted .NET on purpose. Scripts receive only the `clircs` APIs described here. They do not receive arbitrary filesystem access, process launching, registry access, raw sockets, unrestricted CLR access, or unrestricted Internet access.

Scripts are still code. A sandbox is a boundary, not a certificate of good intentions. Read a script before loading it, inspect the permissions it requests, and approve them only when you trust what it does.

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

Use `/script list` to inspect installed scripts. The complete management commands are:

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

Successful loads and explicit unloads are remembered immediately. clircs restores remembered scripts at startup, and failed or declined restores do not block the others.

Changing a permission saves the decision immediately and reloads the script if it is loaded. If approval for additional permissions is declined during that reload, the permission change remains saved and the script stays unloaded.

`/script errors` shows the 20 most recent recorded errors from the current clircs process.

## Manifest

A manifest tells clircs which script it is looking at, which file to execute, and which capabilities the script expects to use. Keeping that contract explicit is boring in the useful way.

A complete manifest looks like this:

```json
{
  "schemaVersion": 1,
  "id": "my-script",
  "name": "My Script",
  "version": "1.0.0",
  "entry": "main.js",
  "permissions": [
    "commands",
    "events",
    "timers",
    "output",
    "storage",
    "raw",
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

The permissions below must be requested in the manifest and granted before their APIs can be used. Loading fails if the entry file immediately uses a capability whose permission has been revoked.

| Permission | API or capability |
| --- | --- |
| `commands` | Register clircs commands and aliases. |
| `events` | Observe semantic client and IRC events. |
| `timers` | Create and cancel bounded one-shot timers. |
| `output` | Print local output, look up or create windows, and write styled text to windows. |
| `storage` | Read and write private durable string settings. |
| `raw` | Allow `/raw` and `/quote` through `clircs.run()`. |
| `localnetwork` | Open text WebSockets restricted to the local machine. |
| `secrets` | Prompt for and store values protected for the current Windows user. |
| `ui` | Contribute constrained items to the window header. |

On first load, clircs displays the script's identity and requested permissions, then asks for one approval in the input bar before executing any script code. Enter `y` or `yes` to approve. Any other response, an empty response, or Escape cancels loading.

Approval is saved across reloads and restarts. Additional requested permissions require another approval; a version change alone does not. Approving new permissions preserves earlier revocations. Declining approval during `/script reload` leaves the existing instance running. A script requesting no permissions loads without an approval prompt.

Use the permission commands to inspect grants or change individual permissions:

```text
/script permissions my-script
/script permissions my-script raw off
/script permissions my-script raw on
```

Normal allowlisted IRC automation commands through `clircs.run()` require no additional permission. The `raw` permission permits `/raw` and its `/quote` alias to send arbitrary IRC protocol commands over the existing network connection.

clircs commands for DCC, filesystem operations, configuration changes, credential management, connection management, and application administration remain unavailable through `clircs.run()`. Unknown and unapproved clircs commands fail closed. Allowed commands still pass through the ordinary parser, validation, captured network/window context, command execution lane, and outbound scheduler. Scripts never receive direct access to an IRC socket.

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

Then load it, approve the requested permission, and use it:

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

### Writing to Windows

All functions below, including `clircs.style()`, require `output`.

| Function | Behavior |
| --- | --- |
| `clircs.windows.active()` | Returns the currently visible window's ID, or `null` when unavailable. |
| `clircs.windows.status(networkId = null)` | Returns that connection's status-window ID. Omitted or `null` selects the application's local status window. An unknown connection returns `null`. |
| `clircs.windows.find(name, networkId = null)` | Looks up a complete window name in the specified connection or local scope. Returns its ID or `null`. It does not perform substring or wildcard searches. |
| `clircs.windows.open(name, options = {})` | Creates or reuses a script-owned background window and returns its ID. The supported option is `networkId`; omitted or `null` creates a connection-independent window. |
| `clircs.windows.write(windowId, text, options = {})` | Appends local output to that specific window. Returns `true` when stored, or `false` when the destination is unavailable or the client is shutting down. |

Window IDs use the same format as `context.bufferId` and `event.bufferId`. Connection IDs come from `context.networkId` or `event.networkId`. Treat these IDs as opaque values.

`active()` reports the window visible when called. For delayed work, capture the intended ID beforehand or use the callback's context ID so switching windows cannot redirect the output.

Script window names contain 1–64 ASCII letters, digits, underscores, or hyphens. A script named `my-script` opening `report` creates `=my-script.report`. Names are scoped by script and connection, and reuse ignores case. Each script may have up to 64 open script-created windows across all scopes.

Opening a window does not select it. Users navigate and close script windows through the ordinary window commands.

```javascript
const report = clircs.windows.open("report");

clircs.windows.write(report, "Report initialized.", { role: "notice" });

clircs.windows.write(report, [
  "State: ",
  clircs.style("highlight", "ready"),
  "."
]);
```

Writes are local text. Sending IRC messages continues through `clircs.run()`. Output is stored without an automatic script-ID prefix; clircs records the originating script internally.

#### Presentation

`text` may be a string or a flat array containing strings and styled text objects. `clircs.style(role, text)` returns a frozen styled piece. Objects may also be supplied directly as `{ text, role }` or `{ text, color }`.

The write options are:

| Option | Meaning |
| --- | --- |
| `role` | Base theme color for the output text. |
| `color` | Fixed console color instead of a theme role. |
| `timestamp` | Optional ISO 8601 timestamp containing UTC `Z` or an explicit offset. Defaults to the current time. |

A style can specify either `role` or `color`. Names are case-insensitive.

Theme roles are `normal`, `dim`, `accent`, `label`, `message`, `highlight`, `action`, `notice`, `join`, `part`, `nick`, `mode`, `warning`, `error`, and `kick`. Roles remain attached to stored text and follow the current theme when history is redrawn. They affect presentation without changing the output's local-event classification.

Fixed colors are `black`, `darkblue`, `darkgreen`, `darkcyan`, `darkred`, `darkmagenta`, `darkyellow`, `gray`, `darkgray`, `blue`, `green`, `cyan`, `red`, `magenta`, `yellow`, and `white`.

Unstyled pieces inherit the write's base style. Each piece has its own IRC formatting state. Embedded IRC colors override that piece's base color; formatting resets return to its base style.

Output uses ordinary clircs wrapping, paging, resizing, Activity, and terminal-control filtering. One write may contain at most 4,096 characters across all pieces, including formatting codes, and at most 4,096 pieces. Existing logging settings apply when writing to a loggable window; script-created windows currently provide scrollback without transcript logging.

#### Lifetime and Failures

Script windows and their history remain available after script unload, fault, and ordinary connection disconnection. Reloading the same script can reuse them. Permanently closing a network session removes its associated script windows. Window history is not persisted across application restarts.

After a user closes a script window, its old ID becomes invalid. A later `open()` can recreate it with a new ID. Writes never fall back to a different window.

Invalid arguments, unknown options, and invalid role, color, or timestamp values raise errors. Opening a window for a connection that no longer exists also raises an error.

Text written through `clircs.windows.write()` is not delivered to `clircs.on()` handlers.

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
4. command handler;
5. optional help object.

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

#### Command Help

The optional fifth argument supplies usage text, additional fields, and topics for the regular `/help` command. It requires no permission beyond `commands`.

```javascript
clircs.registerCommand("greet", ["gr"], "Greet somebody.", context => {
  const target = context.args[0] || "everybody";
  return `Hello, ${target}.`;
}, {
  usage: "/greet [nick]",
  fields: [
    { label: "Default", value: "Greet everybody when no nickname is supplied." }
  ],
  topics: {
    examples: {
      usage: "",
      description: "Examples of using the greeting command.",
      fields: [
        { label: "Named greeting", value: "/greet Alice" },
        { label: "Default greeting", value: "/greet" },
        { label: "Alias", value: "/gr Alice" }
      ]
    }
  }
});
```

Users can read this help with:

```text
/help greet
/help greet examples
/help gr examples
```

The command summary supplies the root Description field. clircs adds the script ID and, when topics exist, an alphabetical topic list automatically. Aliases resolve to the same help, and topic lookup ignores case.

The help object supports:

| Property | Meaning |
| --- | --- |
| `usage` | Usage text. Omitted or `null` uses `/<command> [arguments]`; an empty string hides the Usage field. |
| `fields` | Optional array of `{ label, value }` text fields, displayed in the supplied order. |
| `topics` | Optional object mapping topic names to help objects. Each topic requires a nonempty `description` and may provide its own `usage` and `fields`. |

A topic's omitted or `null` usage defaults to `/<command> <topic> [arguments]`. An empty string hides it, as in the examples topic above.

Topics describe the command; they do not add subcommands or validate arguments. The command handler remains responsible for interpreting its arguments.

Help is captured when the command registers. Changing the original JavaScript object afterward does not change the registered help. Reading `/help` does not execute the script's handler. Reloading replaces the help with the new registration; unloading or faulting removes it with the command.

Help limits are:

- 65,536 characters for the complete JSON-serialized help object;
- 64 topics, with names containing 1–64 characters and no whitespace or control characters;
- topic names must be unique when compared without case;
- 32 custom fields on the root help and on each topic;
- 64 characters per field label;
- 4,096 characters per command summary, usage string, topic description, or field value.

Invalid help rejects the command registration. If that error escapes the entry file during loading, the load fails and its earlier registrations are removed.

### Observing Events

```javascript
clircs.on("message", event => {
  clircs.print(`${event.networkId}: ${event.text}`);
});
```

Requires `events`. Use `"*"` to observe every semantic event kind. Current event names are:

```text
status server message highlight notice action join part nick topic
channelinfo channelsync callerid mode protection error diagnostic
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

Server notices use the `notice` event kind with `fields.serverNotice` set to `"true"`. For notices outside channels, `fields.outputFamily` is `"snotice"` for server notices and `"notice"` for ordinary user notices. Channel notices retain their channel routing.

Events are delivered after clircs has applied protocol state, routing, and history storage, and handled the event's normal display. Scripts observe semantic events rather than parsing themed terminal output, so changing a theme does not quietly break an addon. Stable IDs distinguish identical channel names on simultaneous connections. Handlers can add notifications or other output while the original event follows clircs's normal behavior.

One script may register at most 256 event handlers.

### Requesting clircs Commands

```javascript
clircs.run("/notice Nick hello");
clircs.run("/quote VERSION"); // Requires raw.
```

Normal allowlisted commands require no additional permission. `/raw` and `/quote` require `raw`. Only slash commands are accepted. The command is queued asynchronously using the network and window context active for the script callback that requested it.

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

Faulting a script unregisters its commands, cancels its timers, removes its header items, and closes its local WebSockets. Its script windows and their history remain available. Normal IRC processing continues. Inspect failures with:

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
