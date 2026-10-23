# upstream/ — the parts of SCSKiller that Shaderless uses

Shaderless is built on SCSKiller's core library. This folder holds **only** what the Linux build needs, taken from
[SCSKiller v1.2.5](https://github.com/BlueHeisenberg/SCSKiller) (commit `b10c1e3`):

| Here | From SCSKiller | Used for |
|---|---|---|
| `SCSKiller.Core/` | `src/SCSKiller.Core/` | engine readers, planner, recording format, anti-cheat detection |
| `proxy/warm11.cpp` | `proxy/warm11.cpp` | D3D11 shader warming, linked into `shaderless_warm.exe` |
| `Directory.Build.props` | `Directory.Build.props` | SCSKiller.Core's assembly metadata |
| `LICENSE`, `LICENSE-EXCEPTION.txt` | same | SCSKiller's licence (GPL-3.0-or-later + exception) |

The Windows app, its tests, tools and the Windows recorder are left out. The recorder's Linux/Proton port is
`src/SCSKiller.Linux.Core/Native/recorder.cpp`.

## Linux changes to SCSKiller.Core

`SCSKiller.Core/` is SCSKiller v1.2.5 plus small Linux platform guards (and `proxy/warm11.cpp` semantic fix):

| File | Change |
|---|---|
| `SCSKiller.Core.csproj` | `net10.0-windows` → `net10.0` |
| `App/Codecs.cs` | native Oodle only on Windows |
| `App/KeyFiles.cs` | managed `FileInfo` fallback on Linux instead of `kernel32.dll` (`CreateFileW` / `GetFileInformationByHandleEx`) |
| `App/ProcessMemory.cs` | `Trim()` no-op on Linux (`kernel32.dll` `SetProcessWorkingSetSize`) |
| `App/Recordings.cs` | cross-platform file lock on Linux instead of Windows named `Mutex` |
| `Games/GameFiles.cs` | `/` path separators as well as `\`; managed `File.ResolveLinkTarget` fallback on Linux instead of `kernel32.dll` |
| `Northlight/NorthlightReader.cs` | archive paths built with `Path.Combine` |
| `RedEngine/RedEngineReader.cs` | archive paths built with `Path.Combine` |
| `Planning/Planner.cs` | enable `D3D11Cache` when `caps.PerStageCache` is set (Mesa / AMD DXVK) |
| `Planning/RootSig.cs` | no System32 `d3d12.dll` on Linux: managed root-signature serializer fallback (`WrapDxbc`) |
| `proxy/warm11.cpp` | alias `SV_` user semantics (`sv == 0`) to `SC_` in generated partner VS for Unity GPU-instancing shaders |

Copyright (C) 2026 The SCSKiller authors. Changes Copyright (C) 2026 the Shaderless contributors.
