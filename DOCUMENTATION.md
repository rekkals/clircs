# Documentación de clircs

Hey! This is the (much) longer reference. If all you want to do is get shit running, start with the [README](README.md). Or hell, just launch the client and use `/help`. Also, `/help <command>`. It's pretty easy.

## Installation

clircs runs on 64-bit Windows 10 and 11. The v0.9.0 packaged release comes as a ZIP in two versions: one with .NET 10 included and one that uses the .NET 10 Runtime already installed on your computer. If you're building clircs from source, you'll need the SDK instead.

### Get the Precompiled Package

Choose whichever package makes sense for you:

- **Self-contained:** Big download, with .NET 10 included. Nothing else needs to be installed. If you don't know which package you need, it's this one.
- **Framework-dependent:** The smaller download. If it's not already installed, grab the [.NET 10 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) for Windows x64 and install that first. You don't need the full Desktop Runtime or the SDK.

Extract all of the ZIP's contents wherever you want and run `clircs.exe`.

That's it.

There is no installer for v0.9.0. Extracting the ZIP **is** the installation.

Except for the part about Windows ShitScreen potentially whining about it being an unknown publisher. If it says that, click More Info, then Run Anyway. Also, it may refuse to run it at all. If it does, do not, under any circumstances, disable SmartScreen system-wide on your machine. Just build it from source ... and then hope Windows will let you run **that**.

### Build From Source

Get the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) first. You can't just install the Runtime.

Go to the [0.9.0 release page](https://github.com/rekkals/clircs/releases/tag/v0.9.0) and download **Source code (zip)**. Extract that, get the folder with `clircs.sln` open in PowerShell and run:

```
dotnet build clircs.sln -c Release
dotnet run --project src/Clircs.Console/Clircs.Console.csproj -c Release --no-build
```

If you're passionate about using software that doesn't work quite right, you're welcome to clone whatever's sitting in the repo **right this second** and give that a whirl:

```
git clone https://github.com/rekkals/clircs.git
cd clircs
dotnet build clircs.sln -c Release
dotnet run --project src/Clircs.Console/Clircs.Console.csproj -c Release --no-build
```

## Data and Configuration

User data is stored under the clircs directory in the current Windows roaming application-data folder. Use `/config` to display the exact folder.

Use `/set` to inspect or change client settings. Use `/help set` to see a list of settings and `/help set <setting>` for an explanation of that setting.

Use `/network` to inspect live network sessions and saved profiles. Network-specific stuff like servers, autojoin channels, user modes, and SASL settings belong to profiles instead of individual server addresses.

Use `/backup create` to make a backup that includes user data and logs, and `/backup list` to show saved backups. Restore is, for now, a manual, unzip-and-overwrite process.

## Connecting

```
/server <host> [port] [--tls] [--new] [--password]
```

The `--new` option creates another network session instead of replacing the current connection/window.

TLS certificates are validated normally. A self-signed or otherwise untrusted certificate is shown to the user and may be accepted once or pinned for later. They are not accepted all willy nilly.

Automatic reconnect (after unintentional disconnection) can be enabled via the `network.reconnect` setting, including after KILL by turning `kill.reconnect` on. Existing channel and query windows stay open unless you `/part`.

### Network Profiles

Profiles can be configured for multiple networks, each with their own TLS and SASL settings, plus multiple server endpoints per profile.

#### Adding and Configuring Profiles

Add a new network profile with:

```
/network add <name> <host> [port] [--tls]
```

You can add specific servers to network profiles using:

```
/network server add <profile> <host> [port] [--tls]
```

Check current profile settings with `/network profiles`, and remove individual servers with:

```
/network server remove <profile> <number>
```

Then connect using:

```
/server <profile> [number]
```

#### SASL

Saved TLS profiles support `SASL PLAIN` account authentication:

```
/network sasl <profile> <account>
/network sasl <profile> <account> required
/network sasl <profile> <account> optional
/network sasl <profile>
/network sasl <profile> off
```

The command prompts you for the password and doesn't display it locally. `PLAIN` is allowed only over TLS.

Default is `required`. If SASL is unavailable or authentication fails, clircs disconnects instead of trying to continue unidentified in a loop. Using `optional` reports the failure and allows client registration to continue.

The account, mechanism, and what happens when it fails are readable in `networks.toml`. The password is stored separately in `network-secrets.json` using Windows DPAPI. Reconnects authenticate again using the saved profile credentials.

`SASL EXTERNAL` authenticates with a TLS client certificate instead of an account password:

```
/network sasl <profile> external C:\path\irc-client.pfx
/network sasl <profile> external "C:\path with spaces\irc-client.p12" optional
```

The file must be a PKCS#12 bundle (.pfx or .p12) containing both the certificate and its private key. clircs prompts for its password without echoing it. Submit a blank password if the bundle is unencrypted.

After validating the bundle, clircs gives you the SHA-256 fingerprint to use with the network's account services. The certificate password is stored with the same Windows protection as a `PLAIN` password.

The server must advertise `SASL EXTERNAL`, and the certificate fingerprint must already be associated with the IRC account according to that network's services instructions.

Use `/network sasl <profile>` to inspect and `/network sasl <profile> off` to remove the settings.

### Bouncers/Proxies/Relays

clircs officially supports the following bouncers:

+ ZNC
+ soju
+ Lurker
+ IRCCloud
+ shroudBNC

clircs keeps the client-to-bouncer connection separate from the bouncer-to-IRC-server state when it can so that, ideally, ordinary IRC can still feel like ordinary IRC. Connecting to bouncers via TLS is supported.

You may need to temporarily `/set username` and issue a properly-formatted `--password` to comply with specific bouncers' authentication requirements (check the bouncer's docs). For example:

```
/set username SilentBob/EFnet
/server my.shell.provider 31337 --tls --password
```

Alternatively, you can set up a profile for your bouncer connection with `/network` and use `/network username <profile> <username>`.

## Windows and Input

Windows like `status`, `channel`, `query`, `result`, `DCC`, and `debug` are managed with a stable number-based system. Use `/window` (or `/win`) to list them, `/win <number|name>` to switch directly, `/next` or `/wn` to move forward, and `/previous` or `/wp` to move backward. Unread activity is indicated in the status bar and in `/window`, and can be cleared wholesale with `/clear activity`.

`Page Up` and `Page Down` move through window scrollback. `Up Arrow` and `Down Arrow` go through command history for the current window. `Tab` completes nicknames and cycles through multiple matches.

Traditional IRC formatting controls are available. `Ctrl-B` toggles bold, `Ctrl-I` toggles italic, `Ctrl-U` toggles underline, `Ctrl-R` toggles reverse, and `Ctrl-O` resets formatting. `Ctrl-K` handles color formatting like mIRC: enter a foreground color number and optionally a comma followed by a background color number.

Use `/close` to close the active window. Closing a joined channel parts it. Closing a connected status window requires `/close --force` since that also closes the network connection and its associated windows.

The status bar has a lag meter that measures connection response time in seconds and can be toggled on or off with `/set lagmeter on|off`.

## Everyday IRC

The usual commands: `/join /part /cycle /msg /query /notice /me /nick /away /back /who /whois /iwhois /whowas /list /links /invite /ignore /unignore /kick /ban /kb /op /voice /mode /topic /names`. Convenience aliases are part of the client.

`/msg` sends from the active window. It does not open a query by itself. If a query already exists, the outgoing line is recorded there. Incoming private messages open the query. `/query` explicitly opens one.

`/ignore` is available for any assholes you encounter, and `/unignore` for when you've made amends with those assholes after they've messaged you from a different nickname/host and apologized.

`/uptime` displays how long clircs has been running, and you can share it in a conversation with `/uptime say`.

Service shortcuts are available as `/nickserv`, `/chanserv`, `/memoserv`, `/operserv`, `/hostserv`, `/botserv`, and `/limitserv`. They send the remaining command text to the named service without opening a query.

Recognized `/*serv` commands that require a password prompt do so without echoing it. Ordinary `/msg` remains an ordinary message and is not inspected for secrets, so be smart about using it when communicating with the *Servs.

Notify lists are network-specific and use `MONITOR` when available. Use `/notify add <nickname>`, `/notify remove <nickname>`, and `/notify list`. Servers without `MONITOR` get the old school `ISON` polling.

`WHO`, `WHOIS`, `WHOWAS`, `CTCP`, `WALLOPS`, `notices`, `invites`, `links`, `list`, and `DNS` all have window routing settings. You can set up those events to go to the active, status, or a dedicated window. You'll find all the `*.output` settings in `/set`.

## Channel Management and Protection

The userlist stores network user records, masks, flags, comments, and channel policy. Use `/help adduser`, `/help users`, `/help uwhois`, `/help cprot`, and `/help pprot` for all the syntax and settings.

Channel protection watches channel events and can monitor, kick, or kickban. Personal protection watches events directed at you and temporarily ignores offenders when enforcement is enabled.

Protection settings may inherit from global to network to channel scope. `/cprot` and `/pprot` display how it works.

Clone detection is separate from flood protection. It is informational only, reports users sharing an address, and is enabled by default. Use `/clones` to see the clones and their addresses.

## Logging and Debugging

Use `/logging` or `/log` to check logging settings for the active window. Logging may be enabled network-wide and overridden for an individual `status`, `channel`, `query`, `DCC CHAT`, or `debug` window. Logs are stored as daily UTF-8 text files under the clircs user-data directory.

`/debug` opens a window for monitoring raw incoming and outgoing IRC traffic for the active network. Be careful using it if you're authenticating or whatever, since anything sent to and from the server will be clearly visible in this window.

That includes `PASS`, service identification, oper credentials, the reversible Base64 `AUTHENTICATE` payload used by `SASL PLAIN`, and anything else actually sent on the wire. `debug` windows can also be logged, so either close the `debug` window before entering sensitive information (smart), or inspect copied debug output before sharing it if you do it anyway (less smart).

## Scripting

Scripts are optional JavaScript addons that can be loaded into clircs through Jint. Use `/script` to `list`, `load`, `reload`, and `unload` them.

Loaded script state is remembered across restarts. Script data belongs to that script, script secrets use Windows-protected storage, and runtime limits stop a runaway script from blocking the client indefinitely.

Script manifests declare permissions, which clircs uses, because it does not grant IRC command access, local-network access, secret storage, or other capabilities automatically. Scripts receive only the APIs clircs deliberately exposes. They cannot launch programs, access arbitrary files or the Windows registry, load unrestricted CLR or .NET APIs, open raw sockets, or make unrestricted external network connections. `clircs.run()` is restricted to an allowlist of IRC automation commands and cannot invoke raw protocol access, DCC, filesystem, configuration, credential-management, connection-management, or application-administration commands.

See [SCRIPTING.md](SCRIPTING.md) for all the (un)sexy details.

## DCC

You might think nobody uses DCC anymore, like I did. Then you remember eggdrops are still controlled via `DCC CHAT`, and then you learn they do that with TLS now, and next thing you know your client has to do all the DCC things.

I guess people still use XDCC, too.

### Ordinary DCC

clircs handles `CHAT` and `SEND` requests, uses dedicated windows, supports active and passive connections, `RESUME`, collision-safe renaming, traditional IPv4 offers, and literal IPv6 offers.

Incoming requests receive an ID and require an explicit `/dcc accept <id>` or `/dcc reject <id>`.

To offer a regular file transfer:

```
/dcc send <nick> <file>
/dcc send <nick> <file> --passive
```

To start a chat:

```
/dcc chat <nick>
/dcc chat <nick> --passive
```

### Secure DCC

Secure `DCC CHAT` uses the established `SCHAT` extension and TLS 1.2 or TLS 1.3:

```
/dcc schat <nick>
/dcc schat <nick> --passive
```

Secure `DCC SEND` uses `SSEND` and the same TLS versions:

```
/dcc ssend <nick> <file>
/dcc ssend <nick> <file> --passive
```

Incoming `SCHAT` and `SSEND` requests use the ordinary `/dcc accept`, `/dcc reject`, and `/dcc cancel` commands, and `SSEND` can use `/dcc resume`.

`SCHAT` encrypts the direct connection, but as of this version, does not do certificate authentication. I know, but that's how eggdrop does it by default, and eggdrop is the only reason SCHAT was even implemented in clircs.

### XDCC Downloads

XDCC pack requests use the normal incoming DCC transfer system:

```
/xdcc list <bot>
/xdcc get <bot> <pack>
/xdcc sget <bot> <pack>
```

The first asks the bot for its `LIST`, the second requests an ordinary `SEND`, and the third asks for `SSEND` (and doesn't fall back to plaintext). Pack numbers may be written as `42` or `#42`.

The resulting offer still requires `/dcc accept <id>` or `/dcc reject <id>`. Normal download, validation, progress, resume, cancellation, and collision-safe filename behavior applies.

### DCC Settings and Passive Connections

Use `/set` to configure the advertised DCC address, listening ports, and download directory. The relevant settings include `dcc.address`, `dcc.ports`, and `dcc.downloads`.

Passive DCC is supported.

Check `/help dcc` and `/help dcc <type>` for commands and details.

## Themes

Use `/theme` to show the current and available themes. Use `/theme list`, `/theme load <name>`, or `/theme reload` to manage them.

Theme files control colors and presentation choices including window-name and nick-prefix display.

## Current Limitations

Many.

Most notably, clircs deliberately rejects IRCv3 message tags that were not properly negotiated by the server.

Additionally, some server output may display ... weirdly. I've tested on a number of IRCds, and everything appears to format pretty well, but different networks use numerics differently and support different user modes and all sorts of stuff. There could be work left to do here, is what I'm saying.
