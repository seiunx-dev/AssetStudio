# AGENTS.md

Contributor and agent notes for this repository. This file is the single source of truth; `CLAUDE.md` only points here.

## What this is

A fork of [aelurum/AssetStudioMod](https://github.com/aelurum/AssetStudioMod) (itself a fork of Perfare's AssetStudio), a Unity asset reader and exporter written in C#. The default branch is `sekai-modified`. On top of upstream it adds:

- `AssetStudioCore`: the loading and export engine (sessions, runtime options, exporter, parallel exporter), split out of the CLI so that the CLI and the FFI library share one implementation.
- `AssetStudioFFI`: a NativeAOT shared library, `HarukiAssetStudioFFI`, that exposes a typed C ABI over `AssetStudioCore`. A Rust wrapper crate lives in `AssetStudioFFI/rust/haruki-assetstudio`.
- CLI options `--filter-exclude-mode` and `--strip-path-prefix`.
- Smaller engine changes in `AssetStudio` and `AssetStudioUtility` (texture decoding paths, SMOL-V decoration decoding, material shader keywords and render state, optional model texture conversion).

The GUI (`AssetStudioGUI`) is unchanged from upstream and does not use `AssetStudioCore`.

## Layout

| Path | Contents |
| --- | --- |
| `AssetStudio/` | Core Unity parsing (classes, bundles, compression). Upstream code plus fork patches. |
| `AssetStudio.PInvoke/` | Native library loading helpers. |
| `AssetStudioUtility/` | Converters: textures, sprites, audio (FMOD), shaders, meshes, Live2D, SMOL-V. |
| `AssetStudioFBXWrapper/`, `AssetStudioFBXNative/` | Managed wrapper and C++ (vcxproj) source for FBX export. |
| `Texture2DDecoderWrapper/`, `Texture2DDecoderNative/` | Texture decoder wrapper (net472 only) and C++ source. Newer targets use the `Kyaru.Texture2DDecoder` packages. |
| `AssetStudioCore/` | Fork-only shared engine. `Libraries/<RID>/` holds the prebuilt native libraries (`fmod`, `ooz`, `Texture2DDecoderNative`, `AssetStudioFBXNative`; not every RID has every file). |
| `AssetStudioCLI/` | `AssetStudioModCLI` entry point: option parsing (`Options/CLIOptions.cs`), console logging, and a runner that calls `AssetStudioCore`. |
| `AssetStudioFFI/` | `NativeExports.cs` (all exported entry points), `haruki_assetstudio_native.h` (C header), `README.FFI.md` (ABI contract), `rust/haruki-assetstudio/` (Rust crate with `smoke` and `bench` examples). |
| `AssetStudioGUI/` | Upstream WinForms GUI (Windows only). |

All C# projects carry version `0.19.0.0`, the upstream version. Target frameworks:

- `AssetStudioCore`: net472, net8.0, net9.0, net10.0.
- `AssetStudioCLI`: net472, net8.0, net9.0.
- `AssetStudioFFI`: net10.0 only (`PublishAot`, `NativeLib=Shared`, `InvariantGlobalization`).
- `AssetStudioGUI`: net472, net8.0-windows, net9.0-windows.

## Build and test

There are no C# test projects. Verify changes by building and by running the Rust examples against real Unity assets.

```bash
# NativeAOT FFI library: HarukiAssetStudioFFI plus the RID's native libraries land in the -o directory
dotnet publish AssetStudioFFI/AssetStudioFFI.csproj -c Release -r osx-arm64 -o ../ffi-out

# CLI for one runtime
dotnet build AssetStudioCLI/AssetStudioCLI.csproj -c Release -f net9.0 -r osx-arm64

# Rust wrapper: unit tests and examples
cargo test --manifest-path AssetStudioFFI/rust/haruki-assetstudio/Cargo.toml
cargo run --manifest-path AssetStudioFFI/rust/haruki-assetstudio/Cargo.toml \
  --example smoke -- ../ffi-out/HarukiAssetStudioFFI.dylib /path/to/resources.assets
```

Replace `osx-arm64` with the target RID (`linux-x64`, `linux-arm64`, `osx-x64`, ...). The `bench` example and the full ABI contract are documented in `AssetStudioFFI/README.FFI.md`.

Building the CLI without `-r` runs the upstream `CopyExtraFilesPortable` target, which copies Windows `AssetStudioFBXNative.dll` and `Texture2DDecoderNative.dll` from the C++ projects' `bin/` output under `$(SolutionDir)`. Outside a full Windows solution build this fails with MSB3030, so pass a RID.

## CI and releases

`.github/workflows/build.yml` is inherited from upstream. It only triggers on pushes and pull requests to `master`, a branch this fork does not have, and it builds net5/net6 GUI targets that the projects no longer declare. It has never run here. The fork publishes no releases; there is no automated build or test gate. Build locally before pushing.

## Configuration (FFI environment variables)

| Variable | Effect |
| --- | --- |
| `HARUKI_ASSET_STUDIO_NATIVE_LIBRARY_PATH` | File, directory, or path-list where the DllImport resolver looks for `Texture2DDecoderNative`, `AssetStudioFBXNative` and `ooz` before the default locations (beside the FFI library, `runtimes/<RID>/native`, app base directory, current directory). `fmod` is imported from `AssetStudioUtility`, which has no resolver registered, so it is not covered and relies on the platform's default library search. |
| `HARUKI_ASSET_STUDIO_NATIVE_MAX_CACHED_READ_PAYLOAD_BYTES` | Cap for the per-context cache that lets a read `size` call be followed by `into` without re-reading. Default 512 MiB; `0` disables it. |
| `HARUKI_ASSET_STUDIO_NATIVE_TRACE`, `HARUKI_ASSET_STUDIO_NATIVE_DIAGNOSTICS` | Either one (`1`, `true`, `yes`, `debug`, `trace`) turns on the diagnostics log. |
| `HARUKI_ASSET_STUDIO_NATIVE_LOG_DIR` | Directory for that log (`native-<pid>.log`). Default: `<temp>/haruki-assetstudio-native`. |
| `HARUKI_ASSET_STUDIO_IMAGE_GUARD` | Forces the process-wide ImageSharp guard on. It is always on when dynamic code is unsupported (NativeAOT). |

## Conventions

- Commit titles are mixed: bracketed prefixes (`[Feat]`, `[Chore]`, `[Refactor]`, `[Perf]`, `[CLI]`) and plain imperative sentences. Prefer the bracketed form. The FFI/Core cleanup landed through PR #1 into `sekai-modified`.
- Remotes: `origin` is this fork; add `upstream` as `https://github.com/aelurum/AssetStudioMod` to pull upstream changes. Branch `AssetStudioMod` on the fork is upstream's branch plus the fork's earlier CLI commits; `cli-sekai-modified` is an older line of the FFI work and also holds an unmerged CLI option, `--sekai-keep-single-container-filename`, that is not on the default branch.
- Keep upstream-inherited files (GUI, `CHANGELOG.md`, most of `README.md`) close to upstream so merges stay easy. Put fork-specific documentation in this file, `AssetStudioFFI/README.FFI.md`, or the fork section of `README.md`.
- The FFI library uses invariant globalization: format and parse with `CultureInfo.InvariantCulture`.

## Gotchas

- `CLIOptions` lives in `AssetStudioCLI/Options/` but is declared in namespace `AssetStudioCore.Options`. `AssetStudioCore` grants `InternalsVisibleTo` to `AssetStudioModCLI`.
- Project references in `AssetStudioCore`, `AssetStudioUtility` and `AssetStudioCLI` (and in `AssetStudio` and `AssetStudioFBXWrapper`) are duplicated per `PublishAot` value. The AOT variants pin a target framework through `SetTargetFramework` (net10.0 for Core, Utility, AssetStudio and the FBX wrapper; net9.0 for the CLI) and strip `PublishAot;PublishTrimmed;NativeLib;SelfContained`. `AssetStudioFFI` always uses the pinned, stripped form (net10.0) with no duplication. Follow that pattern when adding a reference.
- The C ABI is mirrored in three places: `AssetStudioFFI/NativeExports.cs`, `AssetStudioFFI/haruki_assetstudio_native.h`, and `AssetStudioFFI/rust/haruki-assetstudio/src/lib.rs`. Change them together. Callers check struct sizes through `haruki_assetstudio_abi_layout_v1`, and a `_v1` suffix changes only when that entry point's C ABI changes (see `README.FFI.md`).
- FFI image reads only return raw RGBA; encoding to PNG and other formats is the caller's job.
- The header comment in `haruki_assetstudio_native.h` still mentions payload-kind "capability arrays". The capabilities struct has no such fields; payload streaming tiers are internal to `AssetStudioCore` (`AssetStudioPayloadStreamingTier`).
- Prebuilt native libraries now live in `AssetStudioCore/Libraries/`, not `AssetStudioCLI/Libraries/`. Both the CLI and the FFI publish copy from there.
