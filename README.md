# AntLslStreamer — LSL plugin for ANT Neuro eego amplifiers (EDI2 SDK)

Streams EEG from an ANT Neuro eego amplifier to [Lab Streaming Layer](https://labstreaminglayer.org),
using the ANT **EDI2** SDK (v2.0.2.1355, in `SDK/`). Any LSL inlet (LabRecorder, MNE-LSL, pylsl,
BCI2000, OpenViBE, ...) can then read the data.

Tested with an **EE-225 (eego sports, 64 referential + 24 bipolar channels)** over USB, and with the
EDI2 file simulator.

## What it publishes

| Stream | Type | Format | Content |
|---|---|---|---|
| `eego <model> <serial>` | `EEG` | float32, nominal rate = sampling rate | One channel per active amplifier channel, in **microvolts** (`--volts` for volts) |
| `eego <model> <serial> Markers` | `Markers` | string, irregular | Trigger codes from the amplifier's trigger input (e.g. `"128"`) |

- `source_id` = `ANT-eego-<serial>` (markers: `ANT-eego-<serial>-markers`), so inlets reconnect automatically.
- Stream metadata (`desc`) has `channels/channel/{label,unit,type,amplifier_index}` and
  `acquisition/{manufacturer,model,serial_number,input_ranges_volts,...}`.
- Channel types: `EEG` (referential), `BIP` (bipolar), `AUX` (auxiliary).
- Timestamps: each chunk is stamped with `lsl_local_clock()` at arrival, and LSL back-computes
  earlier samples from the nominal rate. For best timing, enable LSL dejitter on the inlet side
  (LabRecorder/XDF importers do this by default).
- If the amplifier is unplugged, the app waits and reconnects. The outlet is kept, so inlets keep their
  connection.

## Requirements

The `dist/` folder has the app, the EDI2 SDK libraries and liblsl, but it is **not fully
standalone**. To run it on another PC, copy `dist/` and install these first:

| Requirement | Why | Download |
|---|---|---|
| Windows 10/11, **64-bit** | The EDI2 SDK and liblsl are x64 only | – |
| **.NET 8 Desktop Runtime (x64)** | The app targets .NET 8 and does not bundle the runtime. Without it, Windows asks you to install .NET at startup | [.NET 8 downloads](https://dotnet.microsoft.com/download/dotnet/8.0) → *.NET Desktop Runtime 8.0.x*, Windows x64 |
| **Microsoft Visual C++ 2015–2022 Redistributable (x64)** | Needed by `lsl.dll` and ANT's `Edi.Usb.Native.dll`. Often already installed, but not on a clean Windows | [Latest supported VC++ Redistributable](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist) → `vc_redist.x64.exe` |
| **eego amplifier USB driver** | EDI2 reads the amplifier through libusb/WinUSB and skips devices whose driver reports a problem | Comes with ANT's eego software. Check in Device Manager → *Medical Imaging Solutions* → **eego amplifier** (no warning sign) |

To check a PC: `dotnet --list-runtimes` should list `Microsoft.WindowsDesktop.App 8.0.x`, and
`AntLslStreamer.exe list` should show the amplifier.

To build from source you also need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

## Usage

Run from the `dist/` folder (or `src/AntLslStreamer/bin/Release/...`):

```powershell
.\AntLslStreamer.exe list                      # show amplifier, rates, ranges, channels
.\AntLslStreamer.exe                           # stream all channels @ 500 Hz (Ctrl+C to stop)
.\AntLslStreamer.exe --cap CA-208              # stream with the electrode labels of a waveguard CA-208
.\AntLslStreamer.exe -c NA-265 -r 1000 --referential-only
.\AntLslStreamer.exe inlet -d 10               # built-in test inlet: resolve + read for 10 s
```

Options (`AntLslStreamer help` lists all of them):

| Option | Meaning |
|---|---|
| `-r, --rate <Hz>` | Sampling rate (default 500). EE-225: 250 … 32768 |
| `--ref-range <V>` / `--bip-range <V>` | Input ranges (default: largest; EE-225: ref 1/0.75/0.15, bip 4/1.5/0.7/0.35) |
| `--channels 0-63,64` | Amplifier channel indices to stream |
| `--referential-only` | Only the referential (EEG) channels |
| `-c, --cap <cap>` | Connected EEG cap: `CA-208` or `NA-265` (also accepts `ca208`, `208`, ...). Labels the 64 referential channels |
| `--labels <file>` | Channel labels, one per line (or comma separated), in stream order. Takes precedence over `--cap`. Default `EEG1..`, `BIP1..` |
| `--serial <serial>` | Choose an amplifier when several are connected |
| `-n, --name`, `--source-id` | Override LSL name / source id |
| `--no-markers` | Do not create the trigger stream |
| `--volts` | Stream volts instead of µV |
| `--sim <file.cnt> [--sim-type EE_225]` | Use the EDI2 file simulator instead of USB (e.g. `sim\trigger_test.cnt`) |
| `-d, --duration <s>` | Stop after N seconds |

### EEG caps

The amplifier does not report electrode names. With `--cap`, the referential channels get the cap's
electrode labels, and the stream metadata gets a `cap` element (`name`, `manufacturer`, `labelscheme`).
An electrode labelled `EOG` gets channel type `EOG`. Labels are matched by amplifier channel index
(index 0 = datasheet channel 1), so `--channels` subsets stay correctly labelled. Bipolar channels
stay `BIP1..24`.

**CA-208**: taken from the CBG `lsl-eego` plugin (`electrodeMap_208`) and confirmed correct by the lab.

| Amp channel | 0–11 | 12–23 | 24–35 | 36–47 | 48–63 |
|---|---|---|---|---|---|
| Label | Fp1 Fpz Fp2 F7 F3 Fz F4 F8 FC5 FC1 FC2 FC6 | M1 T7 C3 Cz C4 T8 M2 CP5 CP1 CP2 CP6 P7 | P3 Pz P4 P8 POz O1 O2 EOG AF7 AF3 AF4 AF8 | F5 F1 F2 F6 FC3 FCz FC4 C5 C1 C2 C6 CP3 | CP4 P5 P1 P2 P6 PO5 PO3 PO4 PO6 FT7 FT8 TP7 TP8 PO7 PO8 Oz |

**NA-265**: from ANT's NA-265 datasheet (UDO-SM-0987, DRN-15891 Rev. 5, 2026-01-16,
"Pinning Specifications"; the PDF is not included in this repository). Reference electrodes are at Cz and CPz, ground at AFz and Fpz. These are
also written to the stream metadata as `cap/reference` and `cap/ground`.

| Amp channel | 0–15 | 16–31 | 32–47 | 48–63 |
|---|---|---|---|---|
| Label | Fp1 Fp2 F9 F7 F3 Fz F4 F8 F10 FC5 FC1 FC2 FC6 T9 T7 C3 | C4 T8 T10 CP5 CP1 CP2 CP6 P9 P7 P3 Pz P4 P8 P10 O1 O2 | AF7 AF3 AF4 AF8 F5 F1 F2 F6 FC3 FCz FC4 C5 C1 C2 C6 CP3 | CP4 P5 P1 P2 P6 PO5 PO3 PO4 PO6 FT7 FT8 TP7 TP8 PO7 PO8 POz |

To add another cap, add an entry in `src/AntLslStreamer/Caps.cs`, or use `--labels <file>`.

EDI2 writes its own log to `%LOCALAPPDATA%\AntLslStreamer\logs`. Set `ANTLSL_DEBUG=1` to log every trigger.

## Build

### 1. Install the tools

- Windows 10/11 x64.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (x64). Check with `dotnet --list-sdks`,
  which should list an `8.0.x` version. The SDK includes the runtime, so nothing else is needed to build.
- Git, to clone the repository.

### 2. Get the code

```powershell
git clone https://github.com/fcbg-platforms/LSL-ANT-Gen3.git
cd LSL-ANT-Gen3
```

Use a short path (e.g. `C:\git\LSL-ANT-Gen3`). ANT notes that long paths can make NuGet restore fail.

### 3. Add the ANT EDI2 SDK

The SDK is **not in this repository**: `SDK/` is git-ignored because it is proprietary and too large.
Copy the SDK folder you received from ANT (version 2.0.2.1355) into the repository root as `SDK/`.
The build only needs the NuGet packages, which must end up here:

```
LSL-ANT-Gen3\
  SDK\
    DDE-OP-0590ver2.0.2.1355 EDI Binaries NuPkg\
      ES.CtkImpl_Release.2.0.2.1355.nupkg
      ES.CtkInterface_Release.2.0.2.1355.nupkg
      ES.DeviceDataModel_Release.2.0.2.1355.nupkg
      ES.EdiImpl_Release.2.0.2.1355.nupkg
      ES.EdiInterface_Release.2.0.2.1355.nupkg
      ...
  src\
  nuget.config
```

`nuget.config` points NuGet at that folder. Everything else (other NuGet packages) is downloaded
from nuget.org on the first build. liblsl (`lsl.dll`, v1.16, x64) is already in
`src\AntLslStreamer\native`. Replace it with a newer [liblsl release](https://github.com/sccn/liblsl/releases) if you want.

### 4. Build

To build a ready-to-run folder in `dist\`, run:

```powershell
dotnet publish src\AntLslStreamer -c Release -o dist -p:Version=1.0.0
```

`-p:Version` sets the app version, which also appears in the LSL stream metadata
(`acquisition/application`). Without it, the version is `1.0.0`. `dist\` holds everything the app
needs except the runtimes listed in [Requirements](#requirements).

During development you can run directly from the source:

```powershell
dotnet run --project src\AntLslStreamer -c Release -- list
```

Arguments after `--` are passed to the app.

### 5. Check the build

Without an amplifier, using the file simulator and the test inlet:

```powershell
Start-Process .\dist\AntLslStreamer.exe -ArgumentList '--sim','sim\trigger_test.cnt','-d','20'
.\dist\AntLslStreamer.exe inlet -d 10
```

The inlet should report about 500 Hz and print trigger markers. With the amplifier connected,
`.\dist\AntLslStreamer.exe list` should show it.

### 6. Package for a release (optional)

```powershell
Compress-Archive -Path dist\* -DestinationPath AntLslStreamer-v1.0.0-win-x64.zip -Force
```

The zip contains ANT's SDK libraries. Check ANT's SDK license before sharing it publicly.

### Troubleshooting

| Problem | Fix |
|---|---|
| `Unable to find package ES.CtkImpl_Release` (NU1101) | The SDK is missing or in the wrong place; see step 3 |
| NuGet restore fails with odd path errors | Clone into a shorter path |
| Warning `MSB3246 ... PE image does not have metadata` | Harmless: an ANT package ships native DLLs where NuGet expects .NET ones |
| A new ANT SDK version is not picked up | Update `EdiNugetVersion` in `AntLslStreamer.csproj`, then run `dotnet nuget locals all --clear` |
| `lsl.dll not found` at startup | Run from `dist\` (or the `bin\` output). `lsl.dll` must be next to the exe |

## Code layout

- `Streamer.cs`: EDI2 device discovery and reconnection, channel, rate and range configuration, acquisition loop, trigger de-duplication.
- `LslOutlets.cs`: EEG and marker outlets, stream metadata.
- `Lsl.cs`: minimal liblsl P/Invoke bindings.
- `InletTest.cs`: test inlet (`inlet` command).
- `Options.cs`: command-line parsing.

## Known limitations / not yet verified

- **Hardware triggers were not tested** (no trigger source was connected); they were only verified with the
  simulator. Marker timestamps come from the marker time relative to the frame start in amplifier time,
  with the arrival time as a fallback. Check them against a known trigger source before relying on them
  for precise timing.
- Impedance mode is not exposed. Check impedances with the eego software before streaming.
- Only one amplifier is streamed. Cascaded (multi-amplifier) setups are not configured.
