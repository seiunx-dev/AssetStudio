#ifndef HARUKI_ASSETSTUDIO_NATIVE_H
#define HARUKI_ASSETSTUDIO_NATIVE_H

#include <stdint.h>

#ifdef _WIN32
#  define HARUKI_ASSETSTUDIO_API __declspec(dllimport)
#else
#  define HARUKI_ASSETSTUDIO_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define HARUKI_ASSETSTUDIO_ABI_VERSION 1
#define HARUKI_ASSETSTUDIO_SCHEMA_VERSION 1
#define HARUKI_ASSETSTUDIO_LAYOUT_VERSION 1
#define HARUKI_ASSETSTUDIO_CONTEXT_ABI_VERSION 1
#define HARUKI_ASSETSTUDIO_LIMITS_ABI_VERSION 1
#define HARUKI_ASSETSTUDIO_OBJECT_TABLE_ABI_VERSION 1
#define HARUKI_ASSETSTUDIO_OBJECT_TABLE_INTO_ABI_VERSION 1
#define HARUKI_ASSETSTUDIO_OBJECT_READ_BATCH_ABI_VERSION 1
#define HARUKI_ASSETSTUDIO_OBJECT_READ_BATCH_INTO_ABI_VERSION 1
#define HARUKI_ASSETSTUDIO_OBJECT_READ_BATCH_DIRECT_RETRY_ABI_VERSION 1

/*
 * Stable return codes used by the NativeAOT FFI.
 *
 * The public data path is typed structs only. Caller-provided buffers remain
 * caller-owned; library-owned list buffers are released with
 * haruki_assetstudio_free_buffer; typed batch read v1/v1 buffers may be owned
 * by a result handle and must be released with haruki_assetstudio_result_free.
 * haruki_assetstudio_free_string is retained only as an ABI compatibility
 * helper for callers that still resolve the symbol.
 *
 * haruki_assetstudio_capabilities_v1 reports per-entry-point ABI versions and
 * integer supports_* feature flags only. haruki_assetstudio_limits_v1 reports
 * size and count limits (max_active_contexts, max_concurrent_operations, ...)
 * and legacy_static_engine=0. Per-context lifetime guards reject close/read
 * races with a retryable context-busy status. Open/list/lookup/read/close
 * calls can run across different contexts; narrow dependency locks may still
 * be used internally. Callers should use the flow:
 * open(include_assets=false), paged list, batch read, close/free.
 * native_console_capture=0 (in both responses) means the Native layer does not
 * redirect process-wide stdout/stderr during normal calls.
 * Native dependencies are resolved from HARUKI_ASSET_STUDIO_NATIVE_LIBRARY_PATH
 * (file, directory or path-list) first, then beside this library:
 * Texture2DDecoderNative for image reads, ooz for Oodle-compressed bundles and
 * AssetStudioFBXNative for animator_bundle_fbx reads. fmod is not needed:
 * audio reads return the clip's stored bytes (audio_raw), not decoded audio.
 * No capability field describes how a payload kind is produced; that
 * streaming tier is internal (see README.FFI.md). Image reads return raw RGBA
 * only; encoding is the caller's job.
 * Typed v1/v1 requests require struct_size=sizeof(request), flags=0 unless a
 * documented capability says otherwise, and reserved=0.
 */
enum haruki_assetstudio_status {
    HARUKI_ASSETSTUDIO_OK = 0,
    HARUKI_ASSETSTUDIO_NULL_POINTER = 1,
    HARUKI_ASSETSTUDIO_INVALID_REQUEST = 2,
    HARUKI_ASSETSTUDIO_CONTEXT_NOT_FOUND = 4,
    HARUKI_ASSETSTUDIO_CONTEXT_LIMIT = 5,
    HARUKI_ASSETSTUDIO_ASSET_NOT_FOUND = 6,
    HARUKI_ASSETSTUDIO_UNSUPPORTED_KIND = 7,
    HARUKI_ASSETSTUDIO_BUFFER_TOO_SMALL = 8,
    HARUKI_ASSETSTUDIO_PARTIAL_FAILURE = 9,
    HARUKI_ASSETSTUDIO_CONTEXT_BUSY = 10,
    HARUKI_ASSETSTUDIO_INTERNAL_ERROR = 100
};

enum haruki_assetstudio_object_table_error {
    HARUKI_ASSETSTUDIO_OBJECT_TABLE_ERROR_NONE = 0,
    HARUKI_ASSETSTUDIO_OBJECT_TABLE_ERROR_NULL_POINTER = 1,
    HARUKI_ASSETSTUDIO_OBJECT_TABLE_ERROR_INVALID_REQUEST = 2,
    HARUKI_ASSETSTUDIO_OBJECT_TABLE_ERROR_CONTEXT_NOT_FOUND = 4,
    HARUKI_ASSETSTUDIO_OBJECT_TABLE_ERROR_CONTEXT_BUSY = 5,
    HARUKI_ASSETSTUDIO_OBJECT_TABLE_ERROR_BUFFER_TOO_SMALL = 8,
    HARUKI_ASSETSTUDIO_OBJECT_TABLE_ERROR_INTERNAL_ERROR = 100
};

enum haruki_assetstudio_object_lookup_kind {
    HARUKI_ASSETSTUDIO_OBJECT_LOOKUP_PATH_ID = 1,
    HARUKI_ASSETSTUDIO_OBJECT_LOOKUP_NAME = 2,
    HARUKI_ASSETSTUDIO_OBJECT_LOOKUP_CONTAINER = 3,
    HARUKI_ASSETSTUDIO_OBJECT_LOOKUP_TYPE = 4
};

enum haruki_assetstudio_object_lookup_flags {
    HARUKI_ASSETSTUDIO_OBJECT_LOOKUP_CONTAINS = 1
};

enum haruki_assetstudio_object_read_error {
    HARUKI_ASSETSTUDIO_OBJECT_READ_ERROR_NONE = 0,
    HARUKI_ASSETSTUDIO_OBJECT_READ_ERROR_NULL_POINTER = 1,
    HARUKI_ASSETSTUDIO_OBJECT_READ_ERROR_INVALID_REQUEST = 2,
    HARUKI_ASSETSTUDIO_OBJECT_READ_ERROR_CONTEXT_NOT_FOUND = 4,
    HARUKI_ASSETSTUDIO_OBJECT_READ_ERROR_CONTEXT_BUSY = 5,
    HARUKI_ASSETSTUDIO_OBJECT_READ_ERROR_ASSET_NOT_FOUND = 6,
    HARUKI_ASSETSTUDIO_OBJECT_READ_ERROR_UNSUPPORTED_KIND = 7,
    HARUKI_ASSETSTUDIO_OBJECT_READ_ERROR_BUFFER_TOO_SMALL = 8,
    HARUKI_ASSETSTUDIO_OBJECT_READ_ERROR_PARTIAL_FAILURE = 9,
    HARUKI_ASSETSTUDIO_OBJECT_READ_ERROR_INTERNAL_ERROR = 100
};

enum haruki_assetstudio_context_error {
    HARUKI_ASSETSTUDIO_CONTEXT_ERROR_NONE = 0,
    HARUKI_ASSETSTUDIO_CONTEXT_ERROR_NULL_POINTER = 1,
    HARUKI_ASSETSTUDIO_CONTEXT_ERROR_INVALID_REQUEST = 2,
    HARUKI_ASSETSTUDIO_CONTEXT_ERROR_CONTEXT_NOT_FOUND = 4,
    HARUKI_ASSETSTUDIO_CONTEXT_ERROR_CONTEXT_LIMIT = 5,
    HARUKI_ASSETSTUDIO_CONTEXT_ERROR_CONTEXT_BUSY = 10,
    HARUKI_ASSETSTUDIO_CONTEXT_ERROR_INTERNAL_ERROR = 100
};

typedef struct haruki_assetstudio_context_open_request {
    int32_t struct_size;
    const uint8_t *input_path_utf8;
    int32_t input_path_utf8_len;
    const uint8_t *unity_version_utf8;
    int32_t unity_version_utf8_len;
    const uint8_t *asset_types_csv_utf8;
    int32_t asset_types_csv_utf8_len;
    const uint8_t *output_dir_utf8;
    int32_t output_dir_utf8_len;
    int32_t load_all_assets;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_context_open_request;

typedef struct haruki_assetstudio_context_open_response {
    int32_t struct_size;
    int32_t abi_version;
    int32_t schema_version;
    int32_t context_abi_version;
    int32_t status;
    int32_t error_code;
    int64_t context_id;
    int32_t assets_file_count;
    int32_t exportable_asset_count;
    int32_t object_index_count;
    int32_t has_more_assets;
    uint8_t *unity_version_utf8;
    int32_t unity_version_utf8_len;
    uint8_t *buffer;
    int64_t buffer_len;
    int64_t duration_ms;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_context_open_response;

typedef struct haruki_assetstudio_context_close_request {
    int32_t struct_size;
    int64_t context_id;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_context_close_request;

typedef struct haruki_assetstudio_context_close_response {
    int32_t struct_size;
    int32_t abi_version;
    int32_t schema_version;
    int32_t context_abi_version;
    int32_t status;
    int32_t error_code;
    int64_t context_id;
    int64_t duration_ms;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_context_close_response;

typedef struct haruki_assetstudio_limits_response {
    int32_t struct_size;
    int32_t abi_version;
    int32_t schema_version;
    int32_t limits_abi_version;
    int32_t status;
    int32_t error_code;
    int32_t max_native_utf8_bytes;
    int32_t max_object_read_batch_count;
    int32_t max_object_table_page_limit;
    int64_t max_object_read_batch_payload_bytes;
    int64_t max_cached_object_read_batch_payload_bytes;
    int32_t max_active_contexts;
    int32_t max_concurrent_operations;
    int32_t supports_multiple_contexts;
    int32_t supports_concurrent_operations;
    int32_t legacy_static_engine;
    int32_t native_console_capture;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_limits_response;

typedef struct haruki_assetstudio_capabilities_response {
    int32_t struct_size;
    int32_t abi_version;
    int32_t schema_version;
    int32_t status;
    int32_t error_code;
    int32_t core_api_version_major;
    int32_t core_api_version_minor;
    int32_t context_abi_version;
    int32_t object_table_abi_version;
    int32_t object_table_into_abi_version;
    int32_t object_lookup_abi_version;
    int32_t object_lookup_into_abi_version;
    int32_t object_read_abi_version;
    int32_t object_read_batch_abi_version;
    int32_t object_read_batch_handle_abi_version;
    int32_t object_read_batch_into_abi_version;
    int32_t object_read_batch_by_index_abi_version;
    int32_t object_read_batch_direct_into_abi_version;
    int32_t object_read_batch_direct_retry_abi_version;
    int32_t supports_typed_object_table;
    int32_t supports_caller_provided_object_table_buffers;
    int32_t supports_typed_object_lookup;
    int32_t supports_caller_provided_object_lookup_buffers;
    int32_t supports_typed_object_read;
    int32_t supports_typed_object_read_batch;
    int32_t supports_result_handle;
    int32_t supports_direct_object_read_retry;
    int32_t supports_typed_context;
    int32_t supports_native_dependency_resolver;
    int32_t supports_abi_layout;
    int32_t supports_multiple_contexts;
    int32_t supports_concurrent_operations;
    int32_t supports_context_lifetime_guards;
    int32_t native_console_capture;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_capabilities_response;

typedef struct haruki_assetstudio_abi_layout_response {
    int32_t struct_size;
    int32_t abi_version;
    int32_t schema_version;
    int32_t status;
    int32_t error_code;
    int32_t layout_version;
    int32_t context_open_request;
    int32_t context_open_response;
    int32_t context_close_request;
    int32_t context_close_response;
    int32_t limits_response;
    int32_t capabilities_response;
    int32_t object_list_request;
    int32_t object_list_into_request_v1;
    int32_t object_table;
    int32_t asset_object;
    int32_t object_read_item_request;
    int32_t object_read_batch_into_request_v1;
    int32_t object_read_item_response_v1;
    int32_t object_read_batch_retry_response_v1;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_abi_layout_response;

typedef struct haruki_assetstudio_object_list_request {
    int32_t struct_size;
    int64_t context_id;
    int32_t offset;
    int32_t limit;
    const uint8_t *asset_types_csv_utf8;
    int32_t asset_types_csv_utf8_len;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_object_list_request;

typedef struct haruki_assetstudio_object_list_into_request_v1 {
    int32_t struct_size;
    int64_t context_id;
    int32_t offset;
    int32_t limit;
    const uint8_t *asset_types_csv_utf8;
    int32_t asset_types_csv_utf8_len;
    int32_t flags;
    int32_t reserved;
    uint8_t *buffer;
    int64_t buffer_len;
} haruki_assetstudio_object_list_into_request_v1;

typedef struct haruki_assetstudio_object_lookup_request {
    int32_t struct_size;
    int64_t context_id;
    int32_t lookup_kind;
    int64_t path_id;
    const uint8_t *query_utf8;
    int32_t query_utf8_len;
    const uint8_t *asset_types_csv_utf8;
    int32_t asset_types_csv_utf8_len;
    int32_t offset;
    int32_t limit;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_object_lookup_request;

typedef struct haruki_assetstudio_object_lookup_into_request_v1 {
    int32_t struct_size;
    int64_t context_id;
    int32_t lookup_kind;
    int64_t path_id;
    const uint8_t *query_utf8;
    int32_t query_utf8_len;
    const uint8_t *asset_types_csv_utf8;
    int32_t asset_types_csv_utf8_len;
    int32_t offset;
    int32_t limit;
    int32_t flags;
    int32_t reserved;
    uint8_t *buffer;
    int64_t buffer_len;
} haruki_assetstudio_object_lookup_into_request_v1;

typedef struct haruki_assetstudio_asset_object {
    int32_t index;
    int32_t type_id;
    int64_t path_id;
    int64_t size;
    /* ABI v1. Suggested caller-owned payload capacities for direct reads.
     * estimated_payload_capacity is the default auto/read-kind hint.
     * raw/image/text capacities are kind-specific hints when non-zero.
     * payload_capacity_flags: bit 0 = estimated, bit 1 = exact for default
     * payload, bit 2 = has raw hint, bit 3 = has image hint, bit 4 = has text hint.
     * If direct read returns buffer-too-small, grow to response.required_payload_len.
     */
    int64_t estimated_payload_capacity;
    int64_t raw_payload_capacity;
    int64_t image_payload_capacity;
    int64_t text_payload_capacity;
    int32_t payload_capacity_flags;
    int32_t reserved;
    int32_t name_offset;
    int32_t name_len;
    int32_t container_offset;
    int32_t container_len;
    int32_t type_offset;
    int32_t type_len;
    int32_t unique_id_offset;
    int32_t unique_id_len;
    int32_t source_file_offset;
    int32_t source_file_len;
} haruki_assetstudio_asset_object;

typedef struct haruki_assetstudio_object_table {
    int32_t struct_size;
    int32_t abi_version;
    int32_t schema_version;
    int32_t object_table_abi_version;
    int32_t status;
    int32_t error_code;
    int64_t context_id;
    int32_t offset;
    int32_t limit;
    int32_t next_offset;
    int32_t has_more;
    int32_t total_count;
    int32_t returned_count;
    haruki_assetstudio_asset_object *objects;
    uint8_t *string_data;
    int32_t string_data_len;
    uint8_t *buffer;
    int64_t buffer_len;
    int64_t duration_ms;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_object_table;

typedef struct haruki_assetstudio_object_read_request {
    int64_t context_id;
    int64_t path_id;
    const uint8_t *kind_utf8;
    int32_t kind_utf8_len;
    const uint8_t *image_format_utf8;
    int32_t image_format_utf8_len;
} haruki_assetstudio_object_read_request;

typedef struct haruki_assetstudio_object_read_item_request {
    int64_t path_id;
    const uint8_t *kind_utf8;
    int32_t kind_utf8_len;
    const uint8_t *image_format_utf8;
    int32_t image_format_utf8_len;
} haruki_assetstudio_object_read_item_request;

typedef struct haruki_assetstudio_object_read_item_by_index_request_v1 {
    int32_t object_index;
    const uint8_t *kind_utf8;
    int32_t kind_utf8_len;
    const uint8_t *image_format_utf8;
    int32_t image_format_utf8_len;
} haruki_assetstudio_object_read_item_by_index_request_v1;

typedef struct haruki_assetstudio_object_read_batch_request {
    int64_t context_id;
    const haruki_assetstudio_object_read_item_request *items;
    int32_t count;
    int32_t flags;
} haruki_assetstudio_object_read_batch_request;

typedef struct haruki_assetstudio_object_read_batch_request_v1 {
    int32_t struct_size;
    int64_t context_id;
    const haruki_assetstudio_object_read_item_request *items;
    int32_t count;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_object_read_batch_request_v1;

typedef struct haruki_assetstudio_object_read_batch_into_request_v1 {
    int32_t struct_size;
    int64_t context_id;
    const haruki_assetstudio_object_read_item_request *items;
    int32_t count;
    int32_t flags;
    uint8_t *items_buffer;
    int64_t items_buffer_len;
    uint8_t *payload;
    int64_t payload_len;
    int32_t reserved;
} haruki_assetstudio_object_read_batch_into_request_v1;

typedef struct haruki_assetstudio_object_read_batch_by_index_request_v1 {
    int32_t struct_size;
    int64_t context_id;
    const haruki_assetstudio_object_read_item_by_index_request_v1 *items;
    int32_t count;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_object_read_batch_by_index_request_v1;

typedef struct haruki_assetstudio_object_read_batch_by_index_into_request_v1 {
    int32_t struct_size;
    int64_t context_id;
    const haruki_assetstudio_object_read_item_by_index_request_v1 *items;
    int32_t count;
    int32_t flags;
    int32_t reserved;
    uint8_t *items_buffer;
    int64_t items_buffer_len;
    uint8_t *payload;
    int64_t payload_len;
} haruki_assetstudio_object_read_batch_by_index_into_request_v1;

typedef struct haruki_assetstudio_object_read_response {
    int32_t abi_version;
    int32_t schema_version;
    int32_t object_read_abi_version;
    int32_t status;
    int32_t error_code;
    int64_t context_id;
    int64_t path_id;
    int32_t type_id;
    int64_t size;
    uint8_t *payload_kind;
    int32_t payload_kind_len;
    uint8_t *suggested_extension;
    int32_t suggested_extension_len;
    uint8_t *payload;
    int64_t payload_len;
    uint8_t *buffer;
    int64_t buffer_len;
    int64_t duration_ms;
} haruki_assetstudio_object_read_response;

typedef struct haruki_assetstudio_object_read_item_response {
    int32_t index;
    int32_t status;
    int32_t error_code;
    int64_t path_id;
    int32_t type_id;
    int64_t size;
    int64_t payload_offset;
    int64_t payload_len;
    int32_t payload_kind_offset;
    int32_t payload_kind_len;
    int32_t suggested_extension_offset;
    int32_t suggested_extension_len;
} haruki_assetstudio_object_read_item_response;

typedef struct haruki_assetstudio_object_read_item_response_v1 {
    int32_t index;
    int32_t status;
    int32_t error_code;
    int64_t path_id;
    int32_t type_id;
    int64_t size;
    int64_t payload_offset;
    int64_t payload_len;
    int32_t payload_kind_offset;
    int32_t payload_kind_len;
    int32_t suggested_extension_offset;
    int32_t suggested_extension_len;
    int32_t error_message_offset;
    int32_t error_message_len;
} haruki_assetstudio_object_read_item_response_v1;

typedef struct haruki_assetstudio_object_read_batch_response {
    int32_t abi_version;
    int32_t schema_version;
    int32_t object_read_batch_abi_version;
    int32_t status;
    int32_t error_code;
    int64_t context_id;
    int32_t requested_count;
    int32_t returned_count;
    int32_t failed_count;
    haruki_assetstudio_object_read_item_response *items;
    uint8_t *string_data;
    int32_t string_data_len;
    uint8_t *items_buffer;
    int64_t items_buffer_len;
    uint8_t *payload;
    int64_t payload_len;
    int64_t duration_ms;
} haruki_assetstudio_object_read_batch_response;

typedef struct haruki_assetstudio_object_read_batch_handle_response_v1 {
    int32_t abi_version;
    int32_t schema_version;
    int32_t object_read_batch_abi_version;
    int32_t status;
    int32_t error_code;
    int64_t context_id;
    int32_t requested_count;
    int32_t returned_count;
    int32_t failed_count;
    haruki_assetstudio_object_read_item_response *items;
    uint8_t *string_data;
    int32_t string_data_len;
    uint8_t *items_buffer;
    int64_t items_buffer_len;
    uint8_t *payload;
    int64_t payload_len;
    int64_t duration_ms;
    int32_t object_read_batch_handle_abi_version;
    int64_t result_handle;
} haruki_assetstudio_object_read_batch_handle_response_v1;

typedef struct haruki_assetstudio_object_read_batch_size_response_v1 {
    int32_t struct_size;
    int32_t abi_version;
    int32_t schema_version;
    int32_t object_read_batch_abi_version;
    int32_t object_read_batch_into_abi_version;
    int32_t status;
    int32_t error_code;
    int64_t context_id;
    int32_t requested_count;
    int32_t returned_count;
    int32_t failed_count;
    int64_t required_items_buffer_len;
    int32_t required_string_data_len;
    int64_t required_payload_len;
    int64_t items_buffer_len;
    int32_t string_data_len;
    int64_t payload_len;
    int64_t duration_ms;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_object_read_batch_size_response_v1;

typedef struct haruki_assetstudio_object_read_batch_into_response_v1 {
    int32_t struct_size;
    int32_t abi_version;
    int32_t schema_version;
    int32_t object_read_batch_abi_version;
    int32_t object_read_batch_into_abi_version;
    int32_t status;
    int32_t error_code;
    int64_t context_id;
    int32_t requested_count;
    int32_t returned_count;
    int32_t failed_count;
    haruki_assetstudio_object_read_item_response_v1 *items;
    uint8_t *string_data;
    int32_t string_data_len;
    uint8_t *items_buffer;
    int64_t items_buffer_len;
    uint8_t *payload;
    int64_t payload_len;
    int64_t required_items_buffer_len;
    int32_t required_string_data_len;
    int64_t required_payload_len;
    int64_t duration_ms;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_object_read_batch_into_response_v1;

typedef struct haruki_assetstudio_object_read_batch_retry_response_v1 {
    int32_t struct_size;
    int32_t abi_version;
    int32_t schema_version;
    int32_t object_read_batch_abi_version;
    int32_t object_read_batch_into_abi_version;
    int32_t object_read_batch_direct_retry_abi_version;
    int32_t status;
    int32_t error_code;
    int64_t context_id;
    int32_t requested_count;
    int32_t returned_count;
    int32_t failed_count;
    haruki_assetstudio_object_read_item_response_v1 *items;
    uint8_t *string_data;
    int32_t string_data_len;
    uint8_t *items_buffer;
    int64_t items_buffer_len;
    uint8_t *payload;
    int64_t payload_len;
    int64_t required_items_buffer_len;
    int32_t required_string_data_len;
    int64_t required_payload_len;
    int64_t duration_ms;
    int64_t result_handle;
    /* bit 0 = items_buffer is native-owned, bit 1 = payload is native-owned.
     * If result_handle != 0, release native-owned buffers with result_free.
     * Caller-owned buffers are never freed by result_free.
     */
    int32_t ownership_flags;
    int32_t flags;
    int32_t reserved;
} haruki_assetstudio_object_read_batch_retry_response_v1;

/*
 * Object table v1 memory layout:
 *
 *   table.buffer owns table.objects and table.string_data.
 *   table.objects points to returned_count contiguous asset objects.
 *   table.string_data is a UTF-8 byte pool without null terminators.
 *   String fields are byte offset/length pairs relative to table.string_data.
 *   Empty strings have length 0; ignore their offset.
 *   Release table.buffer once with haruki_assetstudio_free_buffer.
 */

HARUKI_ASSETSTUDIO_API int haruki_assetstudio_capabilities_v1(
    haruki_assetstudio_capabilities_response *response);
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_abi_layout_v1(
    haruki_assetstudio_abi_layout_response *response);
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_limits_v1(
    haruki_assetstudio_limits_response *response);

HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_open_v1(
    const haruki_assetstudio_context_open_request *request,
    haruki_assetstudio_context_open_response *response);
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_list_objects_v1(
    const haruki_assetstudio_object_list_request *request,
    haruki_assetstudio_object_table *response);
/*
 * Object list v1 is the preferred SDK path for caller-owned object table buffers.
 *
 * First call haruki_assetstudio_context_list_objects_size_v1. The response uses
 * buffer_len as the required contiguous table byte count and string_data_len as
 * the UTF-8 string pool byte count; response.buffer/objects/string_data remain
 * null. Then allocate buffer_len bytes and call
 * haruki_assetstudio_context_list_objects_into_v1 with struct_size set to
 * sizeof(haruki_assetstudio_object_list_into_request_v1).
 *
 * On success, response.buffer equals request.buffer. The caller owns that memory;
 * do not pass it to haruki_assetstudio_free_buffer. If the provided buffer is too
 * small, status/error_code are 8 and buffer_len contains the required size.
 */
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_list_objects_size_v1(
    const haruki_assetstudio_object_list_request *request,
    haruki_assetstudio_object_table *response);
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_list_objects_into_v1(
    const haruki_assetstudio_object_list_into_request_v1 *request,
    haruki_assetstudio_object_table *response);
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_lookup_objects_v1(
    const haruki_assetstudio_object_lookup_request *request,
    haruki_assetstudio_object_table *response);
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_lookup_objects_size_v1(
    const haruki_assetstudio_object_lookup_request *request,
    haruki_assetstudio_object_table *response);
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_lookup_objects_into_v1(
    const haruki_assetstudio_object_lookup_into_request_v1 *request,
    haruki_assetstudio_object_table *response);
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_close_v1(
    const haruki_assetstudio_context_close_request *request,
    haruki_assetstudio_context_close_response *response);

HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_read_object_v1(
    const haruki_assetstudio_object_read_request *request,
    haruki_assetstudio_object_read_response *response);

HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_read_objects_v1(
    const haruki_assetstudio_object_read_batch_request *request,
    haruki_assetstudio_object_read_batch_response *response);

/*
 * Batch read v1 returns the same items/string/payload pointers as v1, but
 * ownership is represented by response.result_handle. If result_handle != 0,
 * release all returned buffers with haruki_assetstudio_result_free(result_handle)
 * exactly once; do not pass items_buffer or payload to haruki_assetstudio_free_buffer.
 * Closing a context releases any still-owned result handles for that context.
 */
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_read_objects_handle_v1(
    const haruki_assetstudio_object_read_batch_request *request,
    haruki_assetstudio_object_read_batch_handle_response_v1 *response);

/*
 * Batch read v1 is the preferred SDK path for caller-owned buffers.
 *
 * First call haruki_assetstudio_context_read_objects_size_v1 with struct_size
 * set to sizeof(haruki_assetstudio_object_read_batch_request_v1). Set reserved
 * fields to 0. Allocate items_buffer using response.required_items_buffer_len
 * and payload using response.required_payload_len. Then call
 * haruki_assetstudio_context_read_objects_into_v1 with struct_size set to
 * sizeof(haruki_assetstudio_object_read_batch_into_request_v1).
 *
 * response.items points inside items_buffer. response.string_data also points
 * inside items_buffer. Each v1 item string field is an offset/length pair
 * relative to response.string_data, including error_message_offset/len for
 * failed items. response.payload points to the caller-provided payload buffer.
 * The caller owns both buffers and must not pass them to result_free.
 * If a provided buffer is too small, into_v1 returns
 * HARUKI_ASSETSTUDIO_BUFFER_TOO_SMALL/8 and fills the required_* lengths.
 */
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_read_objects_size_v1(
    const haruki_assetstudio_object_read_batch_request_v1 *request,
    haruki_assetstudio_object_read_batch_size_response_v1 *response);

HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_read_objects_into_v1(
    const haruki_assetstudio_object_read_batch_into_request_v1 *request,
    haruki_assetstudio_object_read_batch_into_response_v1 *response);

HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_read_objects_by_index_size_v1(
    const haruki_assetstudio_object_read_batch_by_index_request_v1 *request,
    haruki_assetstudio_object_read_batch_size_response_v1 *response);
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_read_objects_by_index_into_v1(
    const haruki_assetstudio_object_read_batch_by_index_into_request_v1 *request,
    haruki_assetstudio_object_read_batch_into_response_v1 *response);

/*
 * Direct read v1 is a one-call caller-owned-buffer path. It uses the same
 * request/response structs as v1/v1 into calls, but does not require a prior
 * size call and does not use the per-context pending batch cache. If buffers
 * are too small, it returns HARUKI_ASSETSTUDIO_BUFFER_TOO_SMALL/8 and fills
 * required_* lengths so callers can resize and retry.
 */
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_read_objects_direct_into_v1(
    const haruki_assetstudio_object_read_batch_into_request_v1 *request,
    haruki_assetstudio_object_read_batch_into_response_v1 *response);
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_read_objects_by_index_direct_into_v1(
    const haruki_assetstudio_object_read_batch_by_index_into_request_v1 *request,
    haruki_assetstudio_object_read_batch_into_response_v1 *response);

/*
 * Direct retry v1 is a safe SDK helper over the v1 hot path. The caller may
 * pass reusable buffers just like v1. If they are large enough, response points
 * into those caller buffers and result_handle is 0. If either buffer is too
 * small or null, Native allocates exact-size replacement buffers, fills the
 * response, sets ownership_flags/result_handle, and returns the normal read
 * status instead of BUFFER_TOO_SMALL. Release result_handle once when non-zero.
 */
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_read_objects_direct_retry_v1(
    const haruki_assetstudio_object_read_batch_into_request_v1 *request,
    haruki_assetstudio_object_read_batch_retry_response_v1 *response);
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_context_read_objects_by_index_direct_retry_v1(
    const haruki_assetstudio_object_read_batch_by_index_into_request_v1 *request,
    haruki_assetstudio_object_read_batch_retry_response_v1 *response);

HARUKI_ASSETSTUDIO_API void haruki_assetstudio_free_string(char *value);
HARUKI_ASSETSTUDIO_API void haruki_assetstudio_free_buffer(uint8_t *value);
HARUKI_ASSETSTUDIO_API int haruki_assetstudio_result_free(int64_t result_handle);

#ifdef __cplusplus
}
#endif

#endif
