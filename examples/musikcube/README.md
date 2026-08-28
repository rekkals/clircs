# musikcube integration

This is the first serious clircs addon. It controls a local musikcube instance
through musikcube's bundled WebSocket server plugin.

In musikcube, enable the metadata/WebSocket server in Settings > Server Setup.
The default port is `7905`. Set a password unless you have an unusually
compelling reason not to.

The script is installed with clircs but is not loaded automatically:

```text
/script permissions musikcube localnetwork on
/script permissions musikcube irc on
/script load musikcube
/music password
```

The `localnetwork` permission is required to connect to musikcube. The `irc`
permission is required only when `/song` sends track information to IRC;
`/song --local` remains local.

Playback broadcasts update the shared buffer header immediately. The addon
also requests a fresh overview every two seconds so a missed broadcast does
not leave an old song there forever.

Commands:

```text
/song [--local]
/music status
/music connect
/music disconnect
/music password
/music address [host]
/music port [number]
/music bar <on|off>
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
