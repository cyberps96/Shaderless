# upstream/ — the parts of SCSKiller that Shaderless uses

Shaderless is built on SCSKiller's core library. This folder holds **only** what the Linux build needs, taken from
[SCSKiller v1.2.5](https://github.com/BlueHeisenberg/SCSKiller) (commit `b10c1e3`):

| Here | From SCSKiller | Used for |
|---|---|---|
| `SCSKiller.Core/` | `src/SCSKiller.Core/` | engine readers, planner, recording format, anti-cheat detection |
| `proxy/warm11.cpp` | `proxy/warm11.cpp` | D3D11 shader warming, linked into `scskiller_warm.exe` |
| `Directory.Build.props` | `Directory.Build.props` | SCSKiller.Core's assembly metadata |
| `LICENSE`, `LICENSE-EXCEPTION.txt` | same | SCSKiller's licence (GPL-3.0-or-later + exception) |

The Windows app, its tests, tools and the Windows recorder are left out. The recorder's Linux/Proton port is
`src/SCSKiller.Linux.Core/Native/recorder.cpp`.

## Linux changes to SCSKiller.Core

`SCSKiller.Core/` is SCSKiller v1.2.5 plus a small Linux patch, kept in full in [`linux.patch`](linux.patch)
(apply to an upstream checkout with `patch -p1 < linux.patch`):

| File | Change |
|---|---|
| `SCSKiller.Core.csproj` | `net10.0-windows` → `net10.0` |
| `App/Codecs.cs` | native Oodle only on Windows |
| `Games/GameFiles.cs` | `/` path separators as well as `\` |
| `Northlight/NorthlightReader.cs` | archive paths built with `Path.Combine` |
| `RedEngine/RedEngineReader.cs` | archive paths built with `Path.Combine` |
| `Planning/RootSig.cs` | no System32 `d3d12.dll` on Linux: managed root-signature serializer fallback (`WrapDxbc`) |

Copyright (C) 2026 The SCSKiller authors. Changes Copyright (C) 2026 the Shaderless contributors.
