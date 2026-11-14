# Rigol DP Remote Control

Lightweight WPF application (C#, .NET 10) to control several Rigol programmable DC power supplies:

| Series | Models | Connection |
|---|---|---|
| DP700 | DP711, DP712 | RS232 (COM port) |
| DP800 | DP832/A, DP831/A, DP822/A, DP821/A, DP813/A, DP811/A | LAN (raw socket 5555), RS232 |
| DP900 | DP932A, DP932U, DP932E | LAN (raw socket 5555) |
| DP2000 | DP2031 | LAN (raw socket 5555), RS232 |

SCPI over a raw TCP socket or a serial port. No VISA and no instrument drivers are needed.
NuGet dependencies: ScottPlot.WPF and System.IO.Ports (both bundled in the build output).

## Build

- Open `RigolDPRemoteControl.slnx` in Visual Studio 2026 and press F5, or `dotnet build`.
- Single-exe builds: Visual Studio → right-click project → **Publish** → choose a profile, or
  `dotnet publish RigolDPRemoteControl/RigolDPRemoteControl.csproj -p:PublishProfile=<profile>`:

| Profile | Output folder | Target PC needs | Size (approx.) |
|---|---|---|---|
| `SelfContained-win-x64` | `publish/win-x64/` | nothing (64-bit Windows 10/11) | 66 MB |
| `FrameworkDependent-win-x64` | `publish/win-x64-runtime/` | .NET 10 Desktop Runtime x64 | 22 MB |
| `SelfContained-win-x86` | `publish/win-x86/` | nothing (32- or 64-bit Windows) | 62 MB |
| `FrameworkDependent-win-x86` | `publish/win-x86-runtime/` | .NET 10 Desktop Runtime x86 | 20 MB |

  Every output folder holds `RigolDPRemoteControl.exe` and the `Help` folder. There is no Linux build: the user interface
  is WPF, which only exists on Windows.
- GitHub Actions (`.github/workflows/build.yml`) runs the same four publish profiles on every push and pull request and offers the
  zips as build artifacts. A new release: set `<Version>` in the csproj, commit, then `git tag v1.01` and `git push origin v1.01` –
  the workflow attaches the zips and `SHA256SUMS.txt` to that release.

No instrument at hand? Add a supply with the address `sim` (or `sim-dp831`, `sim-dp711`, `sim-dp932`, `sim-dp2031`, …) to use the built-in simulator.

## Run-time folders (next to the exe)

| Folder | Contents |
|---|---|
| `settings/settings.json` | addresses, channel names, delays, colours, minimized view, groups, trend/window/theme settings |
| `settings/presets/*.dppreset` | saved presets: complete configuration incl. set-points and OVP/OCP of the instruments |
| `trend/LiveTrend.csv` | default trend – overwritten each time trending or the app restarts |
| `trend/*.csv` | trends saved with *Save as…* (open them with *History…*) |
| `Help/Help.html` | user help + datasheet summary of all supported series (F1) |

## Code layout

| Path | Purpose |
|---|---|
| `Scpi/ScpiTcpTransport.cs` | SCPI over TCP: line based, serialized I/O, time-outs, resync on error |
| `Scpi/ScpiSerialTransport.cs` | SCPI over RS232 (`COMn`, port field = baud rate), 8N1, no handshake |
| `Scpi/SimulatedDp800Transport.cs` | in-process instrument simulator (`sim…` host names) |
| `Devices/Dp800Device.cs` | Rigol DP command set (measure, mode, output, set-points, OVP/OCP, identity) |
| `Devices/ModelCatalog.cs` | model ratings / interfaces of the DP700, DP800, DP900 and DP2000 series |
| `Settings/Preset.cs` | preset file format (JSON) |
| `ViewModels/DeviceViewModel.cs` | per-supply background sampling task, auto-reconnect, preset apply on connect |
| `ViewModels/ChannelViewModel.cs` | channel state, set-point editing, delayed switching |
| `ViewModels/MainViewModel.cs` | devices, groups, presets, trend recording, settings persistence |
| `Trend/TrendView.xaml(.cs)` | ScottPlot trend (combined / separate V-A-W, follow/scroll, smoothing, scaling, export) used for live and history |
| `Trend/Decimator.cs` | min/max decimation + moving average of the visible range (keeps long trends fast) |
| `Trend/TrendFile.cs` | CSV trend file reader/writer and background recorder |
| `Themes/` | dark/light palettes (+ WPF Fluent theme via `Application.ThemeMode`) |
| `Assets/` | application icon and logo (taken from the Rigol documentation in `/info`) |

Sampling runs on one background task per supply. The UI pulls the latest values at 10 Hz and the trend
redraws decimated data, so neither the network nor the plot can block the user interface.
