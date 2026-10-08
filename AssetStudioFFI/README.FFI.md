# Haruki AssetStudio Native FFI

This NativeAOT library is intended to be consumed from C-compatible hosts such as Rust, C, C++, Swift, or Python FFI bindings. The public data path is typed structs only: callers pass typed request structs and receive typed response structs with ABI/schema metadata and machine-readable status/error codes.

## Architecture

The native adapter calls `AssetStudioCore` directly. The CLI is now a separate entry-point adapter that also calls the same core project, so FFI consumers no longer depend on the CLI assembly or CLI runner.

## Lifecycle

Use the context API for library integrations:

1. `haruki_assetstudio_capabilities_v1(&response)` to check typed ABI/schema versions and supported FFI feature flags.
2. `haruki_assetstudio_abi_layout_v1(&response)` to verify native struct sizes against the caller's compiled bindings.
3. `haruki_assetstudio_limits_v1(&response)` to get hard limits through a typed response.
4. `haruki_assetstudio_context_open_v1(&request, &response)` to load and index a bundle or asset directory without returning the full object list.
5. `haruki_assetstudio_context_list_objects_size_v1(...)` and `haruki_assetstudio_context_list_objects_into_v1(...)` to page through objects into a caller-owned object table buffer.
6. `haruki_assetstudio_context_read_objects_size_v1(...)` to get exact metadata/string/payload buffer sizes for a typed batch read.
7. `haruki_assetstudio_context_read_objects_into_v1(...)`, the direct into calls, or the direct retry calls to read payloads into caller-owned or safe native-owned buffers.
8. `haruki_assetstudio_context_close_v1(&request, &response)` to release the active context.
9. Release any library-owned typed buffers. For the size/into and direct read calls, the caller owns the output buffers. For handle-based batch reads (`haruki_assetstudio_context_read_objects_handle_v1`), release `result_handle` once with `haruki_assetstudio_result_free`; for the compatibility list/lookup calls (`haruki_assetstudio_context_list_objects_v1`, `haruki_assetstudio_context_lookup_objects_v1`), release returned table buffers with `haruki_assetstudio_free_buffer`. `haruki_assetstudio_free_string` is retained only as an ABI compatibility helper for callers that still resolve the old symbol.

Core logger, progress, runtime options, and ImageSharp timing sinks are execution-context local, so `limits_v1.legacy_static_engine` is `false`. Multiple active contexts are supported up to `limits_v1.max_active_contexts`, and open/list/lookup/read/close calls use per-context lifetime guards instead of one cross-context operation gate. `capabilities_v1.supports_concurrent_operations` is `true`, with narrow internal locks retained only for dependencies that require them, such as the NativeAOT ImageSharp guard. Per-context lifetime guards are enabled (`supports_context_lifetime_guards=true`): calls retain the context while running, and `close` returns `context_busy` / `HARUKI_ASSETSTUDIO_CONTEXT_ERROR_CONTEXT_BUSY` if another call is still using that context. The Native layer does not redirect process-wide `Console.Out`/`Console.Error` during normal calls; `capabilities_v1.native_console_capture` is `false`.

## Version Model

The `_v1` suffix identifies an exported symbol's generation and should only change when that entry point's C ABI changes. Typed responses also report normalized runtime contract versions: `HARUKI_ASSETSTUDIO_ABI_VERSION`, `HARUKI_ASSETSTUDIO_SCHEMA_VERSION`, `HARUKI_ASSETSTUDIO_LAYOUT_VERSION`, and per-feature ABI versions for context, limits, object table, and direct retry reads. SDKs should load the suffixed symbols they were built for, then compare these response versions and the `abi_layout_v1` struct sizes before using hot-path calls.

The typed ABI is defensive, but it is still a C ABI. Null pointers, negative lengths, oversized UTF-8 lengths, oversized object table page limits, oversized batch counts, invalid `struct_size`, missing contexts, unsupported object kinds, and insufficient caller buffers are converted to status/error codes. `haruki_assetstudio_limits_v1` exposes the current hard limits. Dangling pointers, forged addresses, or pointers to memory shorter than the declared length are undefined behavior at the process boundary and cannot be reliably recovered by the callee. Rust bindings should keep request buffers alive for the entire call, pass exact byte lengths, initialize every struct with zeroed memory plus `struct_size`, and treat all returned pointers as borrowed unless the specific function documents caller ownership.

Recommended SDK flow:

```text
capabilities_v1 -> abi_layout_v1 -> limits_v1 -> open_v1 -> typed list size_v1 -> typed list into_v1 -> typed read by-index direct retry_v1 -> result_free if needed -> close_v1
```

For every typed request, set `struct_size` to `sizeof(request_type)`, set `flags` to `0` unless a future capability documents a flag, and set all `reserved` fields to `0`. Typed responses fill their own `struct_size` so callers can validate the ABI layout they compiled against.

`haruki_assetstudio_abi_layout_v1` reports the native `sizeof(...)` for each public typed struct used by the current SDK path. SDK bindings should compare those values with their own struct sizes during startup and fail fast if a layout differs. `haruki_assetstudio_limits_v1` returns hard limits through a typed struct, so hot-path hosts do not need a secondary metadata format to discover page, UTF-8, batch, payload, cache, and legacy engine limits.

For high-throughput object enumeration, prefer `haruki_assetstudio_context_list_objects_size_v1` plus `haruki_assetstudio_context_list_objects_into_v1` when `capabilities_v1.supports_caller_provided_object_table_buffers` is `true`. It writes the typed object table and UTF-8 string pool into caller-owned memory, avoiding large intermediate response blobs and avoiding one native alloc/free per page. `haruki_assetstudio_context_list_objects_v1` remains the compatibility typed path when a caller wants the library to allocate the table buffer. If `limit <= 0`, typed table calls return at most `limits_v1.max_object_table_page_limit` objects; if `limit > limits_v1.max_object_table_page_limit`, they return `HARUKI_ASSETSTUDIO_INVALID_REQUEST`. Asset type filtering is handled by a Core-side type index while preserving object order.

For targeted object discovery, prefer `haruki_assetstudio_context_lookup_objects_size_v1` plus `haruki_assetstudio_context_lookup_objects_into_v1` when `capabilities_v1.supports_caller_provided_object_lookup_buffers` is `true`. It reuses the typed object table layout and can lookup by path id, name, container, or type before the caller pays to move a wider object list across the ABI boundary. `haruki_assetstudio_context_lookup_objects_v1` remains the compatibility typed path when a caller wants the library to allocate the table buffer. Exact path id/name/container/type lookup is indexed; set `HARUKI_ASSETSTUDIO_OBJECT_LOOKUP_CONTAINS` only when substring matching is needed.

For high-throughput object reads after a typed list/lookup, prefer `haruki_assetstudio_context_read_objects_by_index_direct_into_v1` when direct read and by-index read are available and the caller already has reusable buffers. The object `index` returned by list/lookup is stable for the lifetime of the context, so this path avoids path-id dictionary lookup, keeps all read output in caller-owned buffers, and skips the per-context pending batch cache. Each typed object row includes default `estimated_payload_capacity` plus kind-specific `raw_payload_capacity`, `image_payload_capacity`, and `text_payload_capacity`; SDK callers can allocate or grow their reusable payload buffer from the best matching hint and call the direct into entry point without a size prepass. If it returns `HARUKI_ASSETSTUDIO_BUFFER_TOO_SMALL`, grow to `response.required_payload_len` and retry. Direct into/retry calls stream payload bytes in a single read pass, so after a `BUFFER_TOO_SMALL` result the contents of the caller buffers are unspecified; only the `required_*` sizes are meaningful.

For a safer SDK default, use `haruki_assetstudio_context_read_objects_by_index_direct_retry_v1` or path-id `haruki_assetstudio_context_read_objects_direct_retry_v1` when `capabilities_v1.supports_direct_object_read_retry` is true. The retry entry points accept the same caller-owned buffers as the direct into entry points. If they are sufficient, `result_handle` is `0` and returned pointers borrow caller memory. If either buffer is too small or null, Native allocates exact-size replacement buffers, sets `ownership_flags` and `result_handle`, and returns the normal object read status instead of `BUFFER_TOO_SMALL`; release that handle once with `haruki_assetstudio_result_free`. This avoids a manual Rust resize loop while keeping FFI exceptions contained as fixed status/error codes.

When buffer sizes are unknown and the caller wants strict caller-owned memory only, use the two-step size/into path instead: `haruki_assetstudio_context_read_objects_by_index_size_v1` plus `haruki_assetstudio_context_read_objects_by_index_into_v1`, or path-id `haruki_assetstudio_context_read_objects_size_v1` plus `haruki_assetstudio_context_read_objects_into_v1`. All these paths write per-item typed error messages into the same UTF-8 string pool. If some items fail and some succeed, `response.status` remains `0` while `response.error_code` is `HARUKI_ASSETSTUDIO_OBJECT_READ_ERROR_PARTIAL_FAILURE`; inspect each item status/error code before using its payload range. If all items fail with the same reason, the batch returns that concrete error code instead of a generic internal error. When `limits_v1.max_cached_object_read_batch_payload_bytes` is non-zero, the two-step size path keeps the most recent matching size result inside the context so the following `into` call can avoid re-reading the same objects. The cache is capped by `max_cached_object_read_batch_payload_bytes`; set `HARUKI_ASSET_STUDIO_NATIVE_MAX_CACHED_READ_PAYLOAD_BYTES=0` to disable it or to another byte value to tune memory use.

`haruki_assetstudio_context_read_objects_v1` and `haruki_assetstudio_context_read_objects_handle_v1` remain compatibility typed paths when a caller prefers library-owned buffers. They now use the same Core payload writer path as the caller-owned read APIs. The handle variant additionally returns one `result_handle` that owns the metadata buffer and payload buffer together.

Direct into/retry reads can write Core output into caller/native memory through the writer path. Internally, Core tags each payload with an `AssetStudioPayloadStreamingTier` (`AssetStudioCore/AssetStudioTypes.cs`). The tier is not part of the C ABI: `capabilities_v1` only exposes version numbers and integer feature flags, none of which describe the tier, so callers cannot query it. The current kind-to-tier mapping is:

- Source streaming (no full managed payload buffer): `raw`, `audio_raw`, `video_raw`.
- Direct write from arrays already resident on parsed objects: `movie_ogv`, `font`, `text_bytes`.
- Generated streaming (output is streamed to the caller/native buffer instead of first becoming a full managed `byte[]`): `shader_text`, `typetree_json`, `mesh_obj`, `image_raw_rgba`, `image_array_bundle_raw_rgba`. `shader_text` is mixed: uncompressed shaders without subprogram blobs write the original script bytes directly after the header, while compressed/subprogram shaders still generate converted text. Texture array bundles use a counting pass before the write pass so entry lengths can be emitted without retaining each raw RGBA layer as a managed array.
- Temp-file streaming (output is streamed from temporary files into the caller/native buffer): `animator_bundle_fbx`.
- Managed payload (full managed array before the final write): only the fallback branch of the direct writer. With the current kind/asset matching that branch is reached only by unsupported combinations, which fail with `unsupported_kind`.

FBX/animator export still uses a temporary directory internally, but the FFI bundle pack streams those files to the payload writer. FFI image reads always return raw RGBA IR; final image encoding is handled by the Rust caller.

The library-owned batch read paths use the indexed object sizes to reserve an initial native payload capacity before reading the batch. This is a performance hint that reduces reallocations; callers should still trust only `response.payload_len` for the valid byte range.

With object table ABI v1, `haruki_assetstudio_asset_object` includes:

- `estimated_payload_capacity`: recommended caller-owned payload buffer capacity for the default direct read path.
- `raw_payload_capacity`, `image_payload_capacity`, `text_payload_capacity`: recommended capacity for those payload families when non-zero.
- `payload_capacity_flags`: bit 0 means estimated, bit 1 means exact for the default auto/raw-style payload, bits 2/3/4 indicate raw/image/text hints are present.

Capacity hints are not a correctness contract. Direct read responses remain authoritative: use `payload_len` for valid bytes, and grow/retry on `HARUKI_ASSETSTUDIO_BUFFER_TOO_SMALL`.

Texture decoding depends on the platform native `Texture2DDecoderNative` library; Oodle bundle decompression uses `ooz` and `animator_bundle_fbx` reads use `AssetStudioFBXNative`. The FFI never loads `fmod`: audio reads (`audio_raw`) return the clip's stored bytes, and only the CLI's export path converts audio through FMOD. NativeAOT publish copies everything under `AssetStudioCore/Libraries/<RID>/` next to `HarukiAssetStudioFFI` (MSBuild target `CopyHarukiNativeAotRuntimeLibraries`; not every RID ships every library). `NativeExports` registers a DllImport resolver for the texture decoder, FBX wrapper and `AssetStudio` assemblies, so it covers `Texture2DDecoderNative`, `AssetStudioFBXNative` and `ooz`. It looks them up in `HARUKI_ASSET_STUDIO_NATIVE_LIBRARY_PATH` first, then beside the FFI library, under `runtimes/<RID>/native`, in the app base directory and in the current directory. `fmod` is imported from `AssetStudioUtility`, which has no resolver registered; that is harmless because no FFI entry point reaches the FMOD converter, so the shipped `fmod` file can be omitted. External packagers should keep the resolver-covered files beside the FFI library or set `HARUKI_ASSET_STUDIO_NATIVE_LIBRARY_PATH` to a dependency file, dependency directory, or path-list. `capabilities_v1.supports_native_dependency_resolver` exposes the resolver contract for SDK diagnostics.

## Status And Errors

Every typed response starts with `struct_size`, ABI/schema fields, `status`, and a numeric `error_code`. The function return value mirrors `status` for fast checks; callers should still inspect per-item status/error fields for list/read batch calls that can partially succeed.

Stable status families are:

- `null_pointer`
- `invalid_request`
- `context_not_found`
- `context_limit`
- `context_busy`
- `asset_not_found`
- `unsupported_kind`
- `internal_error`

## Request Shapes

Open request:

```c
const char *input_path = "/path/to/bundle-or-directory";
const char *unity_version = "2022.3.62f1";
const char *asset_types = "Texture2D,TextAsset";
haruki_assetstudio_context_open_request request = {
  .struct_size = sizeof(haruki_assetstudio_context_open_request),
  .input_path_utf8 = (const uint8_t *)input_path,
  .input_path_utf8_len = (int32_t)strlen(input_path),
  .unity_version_utf8 = (const uint8_t *)unity_version,
  .unity_version_utf8_len = (int32_t)strlen(unity_version),
  .asset_types_csv_utf8 = (const uint8_t *)asset_types,
  .asset_types_csv_utf8_len = (int32_t)strlen(asset_types),
  .load_all_assets = 0,
  .flags = 0,
  .reserved = 0
};
haruki_assetstudio_context_open_response response = {0};
int rc = haruki_assetstudio_context_open_v1(&request, &response);
```

Typed list size/into request:

```c
haruki_assetstudio_object_list_request request = {
  .struct_size = sizeof(haruki_assetstudio_object_list_request),
  .context_id = context_id,
  .offset = 0,
  .limit = 4096,
  .asset_types_csv_utf8 = (const uint8_t *)"TextAsset,MonoBehaviour",
  .asset_types_csv_utf8_len = 23,
  .flags = 0,
  .reserved = 0
};
haruki_assetstudio_object_table size = {0};
int size_rc = haruki_assetstudio_context_list_objects_size_v1(&request, &size);
uint8_t *buffer = malloc((size_t)size.buffer_len);
haruki_assetstudio_object_list_into_request_v1 into = {
  .struct_size = sizeof(haruki_assetstudio_object_list_into_request_v1),
  .context_id = context_id,
  .offset = request.offset,
  .limit = request.limit,
  .asset_types_csv_utf8 = request.asset_types_csv_utf8,
  .asset_types_csv_utf8_len = request.asset_types_csv_utf8_len,
  .flags = 0,
  .reserved = 0,
  .buffer = buffer,
  .buffer_len = size.buffer_len
};
haruki_assetstudio_object_table table = {0};
int rc = haruki_assetstudio_context_list_objects_into_v1(&into, &table);
/* table.objects points into caller-owned buffer; string offsets are relative to table.string_data. */
free(buffer);
```

Typed lookup request:

```c
haruki_assetstudio_object_lookup_request lookup = {
  .struct_size = sizeof(haruki_assetstudio_object_lookup_request),
  .context_id = context_id,
  .lookup_kind = HARUKI_ASSETSTUDIO_OBJECT_LOOKUP_PATH_ID,
  .path_id = path_id,
  .query_utf8 = NULL,
  .query_utf8_len = 0,
  .asset_types_csv_utf8 = NULL,
  .asset_types_csv_utf8_len = 0,
  .offset = 0,
  .limit = 1,
  .flags = 0,
  .reserved = 0
};
haruki_assetstudio_object_table lookup_table = {0};
int lookup_rc = haruki_assetstudio_context_lookup_objects_v1(&lookup, &lookup_table);
haruki_assetstudio_free_buffer(lookup_table.buffer);
```

Typed list size/into memory rules (caller-owned buffers):

- `size_v1` fills `buffer_len` with the required contiguous table bytes and `string_data_len` with the UTF-8 string pool bytes.
- `into_v1` writes both `table.objects` and `table.string_data` into the caller-owned `request.buffer`.
- Do not call `haruki_assetstudio_free_buffer` for list size/into output buffers.
- If the provided buffer is too small, `into_v1` returns `HARUKI_ASSETSTUDIO_BUFFER_TOO_SMALL`/`8` and leaves `objects`/`string_data` unset.
- `table.objects[i]` is valid while `table.buffer` is alive.
- Every string field is `(offset, len)` into `table.string_data`; offsets are byte offsets, and strings are UTF-8 without null terminators.
- Empty strings have `len == 0`; callers should ignore the offset in that case.
- `table.next_offset == -1` means the page is complete. Otherwise pass `next_offset` into the next request.
- On failure, `table.status` and `table.error_code` mirror the stable native return code, and ABI/schema fields are still filled when `table` itself is writable.

Typed list compatibility path (`haruki_assetstudio_context_list_objects_v1`) memory rules (library-owned buffer):

- `table.buffer` owns both `table.objects` and `table.string_data`.
- Release `table.buffer` exactly once with `haruki_assetstudio_free_buffer`; do not free `objects` or `string_data` separately.

Typed batch read requests use arrays of `haruki_assetstudio_object_read_item_request`. Prefer the by-index direct retry call after listing; use the path-id direct retry call when the caller does not have a table index.

Typed batch read size/into memory rules (caller-owned buffers):

- Set `request.struct_size` to `sizeof(haruki_assetstudio_object_read_batch_request_v1)` for `size_v1`.
- Set every `reserved` field to `0`.
- Allocate `items_buffer` with at least `size_response.required_items_buffer_len` bytes and `payload` with at least `size_response.required_payload_len` bytes.
- Set `into_request.struct_size` to `sizeof(haruki_assetstudio_object_read_batch_into_request_v1)` for `into_v1`.
- `into_response.items` and `into_response.string_data` point inside the caller-owned `items_buffer`; `into_response.payload` points to the caller-owned payload buffer.
- Every item string field is `(offset, len)` into `into_response.string_data`, including `error_message_offset/error_message_len` for failed items.
- If a provided buffer is too small, `into_v1` returns `HARUKI_ASSETSTUDIO_BUFFER_TOO_SMALL`/`8` and fills `required_items_buffer_len`, `required_string_data_len`, and `required_payload_len` without writing item or payload data.
- Do not call `haruki_assetstudio_result_free` or `haruki_assetstudio_free_buffer` for size/into output buffers.

Typed batch read handle path (`haruki_assetstudio_context_read_objects_handle_v1`) memory rules (library-owned buffers):

- `response.result_handle` owns `response.items_buffer` and `response.payload`.
- Release `response.result_handle` exactly once with `haruki_assetstudio_result_free`.
- Do not pass `response.items_buffer` or `response.payload` to `haruki_assetstudio_free_buffer` when `result_handle != 0`.
- `response.items` and `response.string_data` point inside `response.items_buffer`.
- A second `haruki_assetstudio_result_free` for the same handle returns `HARUKI_ASSETSTUDIO_CONTEXT_NOT_FOUND`/`4`.
- Closing a context automatically releases any still-owned result handles for that context; freeing such a handle after close also returns `4`.

## Rust SDK Crate

The Rust wrapper lives in `AssetStudioFFI/rust/haruki-assetstudio`. It loads the native library with `libloading`, validates typed ABI struct sizes through `haruki_assetstudio_abi_layout_v1`, exposes typed capabilities, manages context close through RAII, lists and looks up objects through caller-owned table buffers, and reads objects by either path id or stable list index through direct retry v1. `ObjectReadResult` owns copied payload bytes plus per-item metadata (`payload_kind`, `suggested_extension`, offsets, lengths, and error fields), and `payload_for(item)` returns the item slice with bounds checks.

```bash
cargo run --manifest-path AssetStudioFFI/rust/haruki-assetstudio/Cargo.toml \
  --example smoke -- \
  /path/to/HarukiAssetStudioFFI.dylib \
  /path/to/resources.assets
```

## Smoke Test

After publishing the NativeAOT library, run the Rust smoke example against a real Unity asset file:

```bash
cargo run --manifest-path AssetStudioFFI/rust/haruki-assetstudio/Cargo.toml \
  --example smoke -- \
  /path/to/HarukiAssetStudioFFI.dylib \
  /path/to/resources.assets
```

It exercises capabilities, ABI layout validation, open, paged list, lookup, direct retry reads by path id and by index, and context close.

The `bench` example reads every object of each input through the by-index direct retry path and prints per-bundle timing plus an FNV-1a hash over item metadata and payload bytes, so two library builds can be compared for byte-exact output:

```bash
cargo run --release --manifest-path AssetStudioFFI/rust/haruki-assetstudio/Cargo.toml \
  --example bench -- \
  /path/to/HarukiAssetStudioFFI.dylib \
  [--unity-version V] [--rounds N] [--kind K] [--threads T] \
  /path/to/bundle ...
```
