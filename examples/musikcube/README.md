# musikcube integration

This script controls a local musikcube instance through musikcube's bundled
WebSocket server plugin.

In musikcube, enable the metadata/WebSocket server in Settings > Server Setup.
The default port is `7905`. Set a password unless you have an unusually
compelling reason not to.

Copy this directory to `%APPDATA%\clircs\scripts\musikcube`, then load it:

```text
/script load musikcube
```

Approve the requested permissions when prompted, then configure the password:

```text
/music password
```

The `localnetwork` permission is required to connect to musikcube. `/song` sends track information to IRC; `/song --local` displays it locally. Sending through `/song` requires no additional permission.

Playback broadcasts update the shared buffer header immediately. The addon
also requests a fresh overview every two seconds so a missed broadcast does
not leave an old song there forever.

Use `/help music` or `/help song` for command help. Individual musikcube
operations have their own help topics, such as `/help music volume`.

Commands:

```text
/song [--local]
/music status
/music connect
/music disconnect
/music password
/music address [host]
/music port <1-65535>
/music bar [on|off]
/music play
/music pause
/music stop
/music next
/music previous
/music volume <0-100>
```

Addresses are restricted by clircs to localhost. Passwords are protected with
the current Windows user's credentials and are not stored in the script's
ordinary JSON settings. A backup moved to another Windows account cannot
decrypt that password, so enter it again there.
