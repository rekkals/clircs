# clircs

Command line IRC software (for Windows)

clircs is a Windows-native console IRC client written in C#/.NET 10 with scriptability via Jint. It does not require WSL or Cygwin to run. That's actually the whole point of the client.

That, and for it to be useful out of the box. I've always disliked what I've come to call "the Wordpress approach," where you get supposedly amazing software, but it doesn't do anything until you install a bunch of other shit. I love mIRC, but it does fuck all for a normal human new to IRC, until you add some scripts. And I know it sounds crazy and you can call Ripley's if you don't believe me, but some people actually, really do, just want to chat.

## Current Version

This is the current development build, which includes normal IRC functions, multiple network support, TLS, SASL, bouncer connectivity, channel management, flood protection, logging, scripting, regular-old and the now-more-secure DCC SCHAT and SSEND including passive and resumed file transfers.

Support for all the various ircds could be ... mixed, but shouldn't be terrible. I'm an ircd-ratbox guy, and clircs is pretty solid on EFnet. But I make no promises, at least presently, on how well it may perform on, say, InspIRCd with a bunch of optional modules installed.

## Requirements

Windows 10 or Windows 11 and the .NET 10 runtime.

Final release packages will include what they need, obviously. But if you try to run clircs and Windows tells you that you need the .NET 10 runtime, you do.

## Installing

Just extract it somewhere and run clircs.exe. Regular installation and winget and command-line PATH setup and all that will come later.

## Source

Once I do the public release, it should be as easy as installing the .NET 10 SDK, opening up a command prompt in the source directory, and running:

```powershell
dotnet build clircs.sln
```

## Starting clircs

Run clircs.exe, then connect with:

```text
/server host port
/server host port --tls
/server profile (once set up)
```

Use `/help` for a list of commands and `/help <command>` for help with `<command>`.

## More Info

DOCUMENTATION.txt contains detailed user documentation, settings, command notes, scripting information, and current limitations.

VERSIONS.txt contains the glorious, me-struggling-through-C# version history.

THIRD-PARTY-NOTICES.txt contains third-party licensing notices as required.
