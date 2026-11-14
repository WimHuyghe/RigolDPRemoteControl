# Drivers

**No instrument drivers are required.**

Rigol DP Remote Control communicates with the Rigol power supplies using SCPI text commands

- over a plain TCP socket (LAN, port 5555) for the DP800, DP900 and DP2000 series, and
- over a serial port (RS232) for the DP700 series (which has no LAN interface).

Both use only what is built into Windows/.NET (the serial port support, `System.IO.Ports`, is bundled with
the application), so NI-VISA, Rigol Ultra Sigma, IVI drivers or USB-TMC drivers are **not** needed.

The only possible exception: a **USB-to-RS232 adapter** (for a DP700 on a PC without a COM port) needs the
driver of that adapter (FTDI, Prolific, WCH CH340, …). Windows normally installs it automatically through
Windows Update when the adapter is plugged in; it depends on the adapter you use, so it is not included here.

Runtime requirement:

- Self-contained build (`publish\win-x64\RigolDPRemoteControl.exe`): nothing to install.
- Framework-dependent build (normal Visual Studio Debug/Release output): the free
  [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) — usually already
  present on a PC with Visual Studio 2026.

That is why this folder is intentionally empty.
