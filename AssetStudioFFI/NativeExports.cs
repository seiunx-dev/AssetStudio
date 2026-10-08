using AssetStudioCore;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Texture2DDecoder;

namespace AssetStudioFFI;

public static unsafe class NativeExports
{
    private const int FfiAbiVersion = 1;
    private const int FfiSchemaVersion = 1;
    internal const int FfiAbiVersionForEnvelope = FfiAbiVersion;
    internal const int FfiSchemaVersionForEnvelope = FfiSchemaVersion;
    private static readonly NativeDiagnostics Diagnostics = NativeDiagnostics.CreateFromEnvironment();
    private static readonly string WorkerId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
    private static readonly long MaxCachedObjectReadBatchPayloadBytes = ReadLongEnvironment(
        "HARUKI_ASSET_STUDIO_NATIVE_MAX_CACHED_READ_PAYLOAD_BYTES",
        512L * 1024L * 1024L);
    private const uint PayloadBundleMagic = 0x42504148; // HAPB
    private const ushort PayloadBundleVersion = 2;
    private const ushort PayloadBundleHeaderLength = 20;
    private const int FfiLayoutVersion = 1;
    private const int FfiObjectTableAbiVersion = 1;
    private const int FfiObjectTableIntoAbiVersion = 1;
    private const int FfiObjectReadAbiVersion = 1;
    private const int FfiObjectReadBatchAbiVersion = 1;
    private const int FfiObjectReadBatchHandleAbiVersion = 1;
    private const int FfiObjectReadBatchIntoAbiVersion = 1;
    private const int FfiObjectReadBatchByIndexAbiVersion = 1;
    private const int FfiObjectReadBatchDirectIntoAbiVersion = 1;
    private const int FfiObjectReadBatchDirectRetryAbiVersion = 1;
    private const int FfiObjectLookupAbiVersion = 1;
    private const int FfiObjectLookupIntoAbiVersion = 1;
    private const int FfiContextAbiVersion = 1;
    private const int FfiLimitsAbiVersion = 1;
    private const int MaxNativeUtf8ByteLength = 1024 * 1024;
    private const int MaxNativeObjectReadBatchCount = 65536;
    private const int MaxNativeObjectTablePageLimit = 65536;
    private const int MaxNativeActiveContexts = 4;
    private static readonly int MaxNativeConcurrentOperations = Math.Max(1, Environment.ProcessorCount);
    private const long MaxNativeObjectReadBatchPayloadBytes = int.MaxValue;
    private static long NextContextId;
    private static long NextResultHandle;
    private static readonly object SessionsSync = new();
    private static readonly Dictionary<long, ActiveNativeContext> Sessions = new();
    private static readonly Dictionary<long, NativeResultArena> ResultArenas = new();

    static NativeExports()
    {
        SixLabors.ImageSharp.Configuration.Default.MaxDegreeOfParallelism = 1;
        NativeLibrary.SetDllImportResolver(typeof(TextureDecoder).Assembly, ResolveAssetStudioNativeLibrary);
        // The resolver is per-assembly. The FBX wrapper (AssetStudioFBXNative) and
        // the AssetStudio core (ooz) declare their own DllImports; default
        // probing does not search this shared library's directory, so without the
        // resolver those loads fail even when the .so ships right next to us.
        // fmod (AssetStudioUtility) is deliberately not covered: no FFI read path
        // decodes audio, audio reads return the stored bytes.
        NativeLibrary.SetDllImportResolver(typeof(AssetStudio.Fbx).Assembly, ResolveAssetStudioNativeLibrary);
        NativeLibrary.SetDllImportResolver(typeof(AssetStudio.AssetsManager).Assembly, ResolveAssetStudioNativeLibrary);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Diagnostics.Event("process", "unhandled_exception", args.ExceptionObject?.ToString());
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Diagnostics.Event("process", "unobserved_task_exception", args.Exception.ToString());
        KeepAotReflectionDependencies();
        Diagnostics.Event(
            "process",
            "native_exports_initialized",
            $"image_guard={AssetStudio.ImageSharpNativeAotGuard.Enabled}");
    }

    /// <summary>
    /// Roots type-tree parsed asset types (and their enums) for the ILC trimmer in one
    /// place. Called from the static constructor, which every export reaches, so the
    /// attributes no longer need to be repeated on individual entry points.
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.Texture2D))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.Texture2DArray))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.GLTextureSettings))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.StreamingInfo))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.ResourceReader))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.Sprite))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.TextAsset))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.MonoBehaviour))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.AudioClip))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.VideoClip))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.MovieTexture))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.Font))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.Shader))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.Mesh))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.Animator))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(AssetStudio.AnimationClip))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields, typeof(AssetStudio.TextureFormat))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields, typeof(AssetStudio.GraphicsFormat))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields, typeof(AssetStudio.ClassIDType))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields, typeof(AssetStudio.FMODSoundType))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields, typeof(AssetStudio.AudioCompressionFormat))]
    private static void KeepAotReflectionDependencies()
    {
    }

    // Shared fail path: stamps Status/ErrorCode/DurationMs on the typed response and
    // returns the status so callers can `return Fail(...)` in one line.
    private static int Fail(NativeContextOpenResponse* response, Stopwatch stopwatch, int status, NativeContextErrorCode errorCode)
    {
        response->Status = status;
        response->ErrorCode = errorCode;
        response->DurationMs = stopwatch.ElapsedMilliseconds;
        return status;
    }

    private static int Fail(NativeContextCloseResponse* response, Stopwatch stopwatch, int status, NativeContextErrorCode errorCode)
    {
        response->Status = status;
        response->ErrorCode = errorCode;
        response->DurationMs = stopwatch.ElapsedMilliseconds;
        return status;
    }

    private static int Fail(NativeObjectTable* response, Stopwatch stopwatch, int status, NativeObjectTableErrorCode errorCode)
    {
        response->Status = status;
        response->ErrorCode = errorCode;
        response->DurationMs = stopwatch.ElapsedMilliseconds;
        return status;
    }

    private static int Fail(NativeObjectReadResponse* response, Stopwatch stopwatch, int status, NativeObjectReadErrorCode errorCode)
    {
        response->Status = status;
        response->ErrorCode = errorCode;
        response->DurationMs = stopwatch.ElapsedMilliseconds;
        return status;
    }

    private static int Fail(NativeObjectReadBatchResponse* response, Stopwatch stopwatch, int status, NativeObjectReadErrorCode errorCode)
    {
        response->Status = status;
        response->ErrorCode = errorCode;
        response->DurationMs = stopwatch.ElapsedMilliseconds;
        return status;
    }

    private static int Fail(NativeObjectReadBatchResponseV1* response, Stopwatch stopwatch, int status, NativeObjectReadErrorCode errorCode)
    {
        response->Status = status;
        response->ErrorCode = errorCode;
        response->DurationMs = stopwatch.ElapsedMilliseconds;
        return status;
    }

    private static int Fail(NativeObjectReadBatchSizeResponseV1* response, Stopwatch stopwatch, int status, NativeObjectReadErrorCode errorCode)
    {
        response->Status = status;
        response->ErrorCode = errorCode;
        response->DurationMs = stopwatch.ElapsedMilliseconds;
        return status;
    }

    private static int Fail(NativeObjectReadBatchIntoResponseV1* response, Stopwatch stopwatch, int status, NativeObjectReadErrorCode errorCode)
    {
        response->Status = status;
        response->ErrorCode = errorCode;
        response->DurationMs = stopwatch.ElapsedMilliseconds;
        return status;
    }

    private static int Fail(NativeObjectReadBatchRetryResponseV1* response, Stopwatch stopwatch, int status, NativeObjectReadErrorCode errorCode)
    {
        response->Status = status;
        response->ErrorCode = errorCode;
        response->DurationMs = stopwatch.ElapsedMilliseconds;
        return status;
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_capabilities_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int CapabilitiesV1(NativeCapabilitiesResponse* response)
    {
        if (response == null)
        {
            return 1;
        }

        *response = default;
        response->StructSize = sizeof(NativeCapabilitiesResponse);
        response->AbiVersion = FfiAbiVersion;
        response->SchemaVersion = FfiSchemaVersion;
        response->Status = 0;
        response->ErrorCode = NativeContextErrorCode.None;
        response->CoreApiVersionMajor = 1;
        response->CoreApiVersionMinor = 0;
        response->ContextAbiVersion = FfiContextAbiVersion;
        response->ObjectTableAbiVersion = FfiObjectTableAbiVersion;
        response->ObjectTableIntoAbiVersion = FfiObjectTableIntoAbiVersion;
        response->ObjectLookupAbiVersion = FfiObjectLookupAbiVersion;
        response->ObjectLookupIntoAbiVersion = FfiObjectLookupIntoAbiVersion;
        response->ObjectReadAbiVersion = FfiObjectReadAbiVersion;
        response->ObjectReadBatchAbiVersion = FfiObjectReadBatchAbiVersion;
        response->ObjectReadBatchHandleAbiVersion = FfiObjectReadBatchHandleAbiVersion;
        response->ObjectReadBatchIntoAbiVersion = FfiObjectReadBatchIntoAbiVersion;
        response->ObjectReadBatchByIndexAbiVersion = FfiObjectReadBatchByIndexAbiVersion;
        response->ObjectReadBatchDirectIntoAbiVersion = FfiObjectReadBatchDirectIntoAbiVersion;
        response->ObjectReadBatchDirectRetryAbiVersion = FfiObjectReadBatchDirectRetryAbiVersion;
        response->SupportsTypedObjectTable = 1;
        response->SupportsCallerProvidedObjectTableBuffers = 1;
        response->SupportsTypedObjectLookup = 1;
        response->SupportsCallerProvidedObjectLookupBuffers = 1;
        response->SupportsTypedObjectRead = 1;
        response->SupportsTypedObjectReadBatch = 1;
        response->SupportsResultHandle = 1;
        response->SupportsDirectObjectReadRetry = 1;
        response->SupportsTypedContext = 1;
        response->SupportsNativeDependencyResolver = 1;
        response->SupportsAbiLayout = 1;
        response->SupportsMultipleContexts = 1;
        response->SupportsConcurrentOperations = 1;
        response->SupportsContextLifetimeGuards = 1;
        response->NativeConsoleCapture = 0;
        response->Flags = 0;
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_abi_layout_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int AbiLayoutV1(NativeAbiLayoutResponse* response)
    {
        if (response == null)
        {
            return 1;
        }

        *response = default;
        response->StructSize = sizeof(NativeAbiLayoutResponse);
        response->AbiVersion = FfiAbiVersion;
        response->SchemaVersion = FfiSchemaVersion;
        response->Status = 0;
        response->ErrorCode = NativeContextErrorCode.None;
        response->LayoutVersion = FfiLayoutVersion;
        response->ContextOpenRequest = sizeof(NativeContextOpenRequest);
        response->ContextOpenResponse = sizeof(NativeContextOpenResponse);
        response->ContextCloseRequest = sizeof(NativeContextCloseRequest);
        response->ContextCloseResponse = sizeof(NativeContextCloseResponse);
        response->LimitsResponse = sizeof(NativeLimitsResponse);
        response->CapabilitiesResponse = sizeof(NativeCapabilitiesResponse);
        response->ObjectListRequest = sizeof(NativeObjectListRequest);
        response->ObjectListIntoRequestV1 = sizeof(NativeObjectListIntoRequest);
        response->ObjectTable = sizeof(NativeObjectTable);
        response->AssetObject = sizeof(NativeAssetObject);
        response->ObjectReadItemRequest = sizeof(NativeObjectReadItemRequest);
        response->ObjectReadBatchIntoRequestV1 = sizeof(NativeObjectReadBatchIntoRequestV1);
        response->ObjectReadItemResponseV1 = sizeof(NativeObjectReadItemResponseV1);
        response->ObjectReadBatchRetryResponseV1 = sizeof(NativeObjectReadBatchRetryResponseV1);
        response->Flags = 0;
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_limits_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int LimitsV1(NativeLimitsResponse* response)
    {
        if (response == null)
        {
            return 1;
        }

        *response = default;
        response->StructSize = sizeof(NativeLimitsResponse);
        response->AbiVersion = FfiAbiVersion;
        response->SchemaVersion = FfiSchemaVersion;
        response->LimitsAbiVersion = FfiLimitsAbiVersion;
        response->Status = 0;
        response->ErrorCode = NativeContextErrorCode.None;
        response->MaxNativeUtf8Bytes = MaxNativeUtf8ByteLength;
        response->MaxObjectReadBatchCount = MaxNativeObjectReadBatchCount;
        response->MaxObjectTablePageLimit = MaxNativeObjectTablePageLimit;
        response->MaxObjectReadBatchPayloadBytes = MaxNativeObjectReadBatchPayloadBytes;
        response->MaxCachedObjectReadBatchPayloadBytes = MaxCachedObjectReadBatchPayloadBytes;
        response->MaxActiveContexts = MaxNativeActiveContexts;
        response->MaxConcurrentOperations = MaxNativeConcurrentOperations;
        response->SupportsMultipleContexts = 1;
        response->SupportsConcurrentOperations = 1;
        response->LegacyStaticEngine = 0;
        response->NativeConsoleCapture = 0;
        response->Flags = 0;
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_open_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextOpenV1(NativeContextOpenRequest* request, NativeContextOpenResponse* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeContextOpenResponse(response);
        try
        {
            if (request == null)
            {
                return Fail(response, stopwatch, 1, NativeContextErrorCode.NullPointer);
            }

            if (request->StructSize < sizeof(NativeContextOpenRequest))
            {
                return Fail(response, stopwatch, 2, NativeContextErrorCode.InvalidRequest);
            }

            var inputPath = ReadNativeUtf8(request->InputPathUtf8, request->InputPathUtf8Len, defaultValue: "");
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                return Fail(response, stopwatch, 2, NativeContextErrorCode.InvalidRequest);
            }
            var unityVersion = ReadNativeUtf8(request->UnityVersionUtf8, request->UnityVersionUtf8Len, defaultValue: "");
            var outputDir = ReadNativeUtf8(request->OutputDirUtf8, request->OutputDirUtf8Len, defaultValue: "");
            var assetTypes = ParseNativeAssetTypes(request->AssetTypesCsvUtf8, request->AssetTypesCsvUtf8Len);

            var operationId = Diagnostics.Begin("context_open_v1", inputPath);
            if (ActiveSessionCount() >= MaxNativeActiveContexts)
            {
                return Fail(response, stopwatch, 5, NativeContextErrorCode.ContextLimit);
            }

            var inspectOptions = new AssetStudioInspectOptions
            {
                InputPath = inputPath,
                AssetTypes = assetTypes,
                UnityVersion = string.IsNullOrWhiteSpace(unityVersion) ? null : unityVersion,
                LoadAllAssets = request->LoadAllAssets != 0,
                IncludeAssets = false,
                OutputDir = string.IsNullOrWhiteSpace(outputDir) ? null : outputDir,
            };
            var session = AssetStudioSession.Open(inspectOptions);
            var contextId = Interlocked.Increment(ref NextContextId);
            var result = session.InspectResult;
            var totalAssetCount = session.CountObjects(assetTypes);
            if (!TryAddSession(new ActiveNativeContext(contextId, operationId, inputPath, stopwatch, assetTypes, session)))
            {
                session.Dispose();
                return Fail(response, stopwatch, 5, NativeContextErrorCode.ContextLimit);
            }

            response->Status = 0;
            response->ErrorCode = NativeContextErrorCode.None;
            response->ContextId = contextId;
            response->AssetsFileCount = result.AssetsFileCount;
            response->ExportableAssetCount = totalAssetCount;
            response->ObjectIndexCount = session.ObjectIndexCount;
            response->HasMoreAssets = totalAssetCount > 0 ? 1 : 0;
            response->DurationMs = stopwatch.ElapsedMilliseconds;
            response->Buffer = WriteObjectReadStringsToNative(
                result.UnityVersion,
                null,
                out response->UnityVersionUtf8,
                out response->UnityVersionUtf8Len,
                out _,
                out _,
                out response->BufferLen);
            Diagnostics.End(operationId, "context_open_v1", stopwatch.ElapsedMilliseconds, $"assets={totalAssetCount} object_index={session.ObjectIndexCount}");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Diagnostics.Exception("context_open_v1", ex);
            ResetProcessLocalState();
            return Fail(response, stopwatch, 2, NativeContextErrorCode.InvalidRequest);
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_open_v1", ex);
            ResetProcessLocalState();
            return Fail(response, stopwatch, 100, NativeContextErrorCode.InternalError);
        }
    }

    private static void InitializeNativeContextOpenResponse(NativeContextOpenResponse* response)
    {
        *response = default;
        response->StructSize = sizeof(NativeContextOpenResponse);
        response->AbiVersion = FfiAbiVersion;
        response->SchemaVersion = FfiSchemaVersion;
        response->ContextAbiVersion = FfiContextAbiVersion;
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_close_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextCloseV1(NativeContextCloseRequest* request, NativeContextCloseResponse* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeContextCloseResponse(response);
        try
        {
            if (request == null)
            {
                return Fail(response, stopwatch, 1, NativeContextErrorCode.NullPointer);
            }

            if (request->StructSize < sizeof(NativeContextCloseRequest))
            {
                return Fail(response, stopwatch, 2, NativeContextErrorCode.InvalidRequest);
            }

            response->ContextId = request->ContextId;
            if (!TryRemoveSession(request->ContextId, out var context))
            {
                return Fail(response, stopwatch, 4, NativeContextErrorCode.ContextNotFound);
            }

            if (!context.TryBeginClose())
            {
                lock (SessionsSync)
                {
                    Sessions[request->ContextId] = context;
                }
                return Fail(response, stopwatch, 10, NativeContextErrorCode.ContextBusy);
            }

            var operationId = context.OperationId;
            try
            {
                context.ClearPendingReadBatch();
                context.Session.Dispose();
            }
            finally
            {
                ReleaseResultArenasForContext(request->ContextId);
            }
            Diagnostics.Event(operationId, "context_closed_v1", $"duration_ms={stopwatch.ElapsedMilliseconds}");
            return Fail(response, stopwatch, 0, NativeContextErrorCode.None);
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_close_v1", ex);
            return Fail(response, stopwatch, 100, NativeContextErrorCode.InternalError);
        }
    }

    private static void InitializeNativeContextCloseResponse(NativeContextCloseResponse* response)
    {
        *response = default;
        response->StructSize = sizeof(NativeContextCloseResponse);
        response->AbiVersion = FfiAbiVersion;
        response->SchemaVersion = FfiSchemaVersion;
        response->ContextAbiVersion = FfiContextAbiVersion;
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_list_objects_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextListObjectsV1(NativeObjectListRequest* request, NativeObjectTable* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectTableResponse(response);
        try
        {
            if (request == null)
            {
                return Fail(response, stopwatch, 1, NativeObjectTableErrorCode.NullPointer);
            }

            if (request->StructSize < sizeof(NativeObjectListRequest))
            {
                return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
            }

            var acquireResult = TryAcquireSession(request->ContextId, out var context);
            if (acquireResult == NativeContextAcquireResult.NotFound || context == null)
            {
                return Fail(response, stopwatch, 4, NativeObjectTableErrorCode.ContextNotFound);
            }
            if (acquireResult == NativeContextAcquireResult.Busy)
            {
                return Fail(response, stopwatch, 5, NativeObjectTableErrorCode.ContextBusy);
            }

            try
            {
                var requestedAssetTypes = ParseNativeAssetTypes(request->AssetTypesCsvUtf8, request->AssetTypesCsvUtf8Len);
                requestedAssetTypes ??= context.RequestedAssetTypes;
                var offset = Math.Max(0, request->Offset);
                var totalCount = context.Session.CountObjects(requestedAssetTypes);
                if (!TryNormalizeObjectTableLimit(request->Limit, totalCount, out var limit))
                {
                    return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
                }
                var page = context.Session.ListObjects(new AssetStudioObjectListOptions
                {
                    Offset = offset,
                    Limit = limit,
                    AssetTypes = requestedAssetTypes,
                });
                var buffer = WriteObjectTableToNative(page, out var stringDataOffset, out var stringDataLength, out var bufferLength);
                var nextOffset = offset + page.Length;
                var hasMore = nextOffset < totalCount;

                response->Status = 0;
                response->ErrorCode = NativeObjectTableErrorCode.None;
                response->ContextId = request->ContextId;
                response->Offset = offset;
                response->Limit = limit;
                response->NextOffset = hasMore ? nextOffset : -1;
                response->HasMore = hasMore ? 1 : 0;
                response->TotalCount = totalCount;
                response->ReturnedCount = page.Length;
                response->Objects = (NativeAssetObject*)buffer;
                response->StringData = buffer == null ? null : buffer + stringDataOffset;
                response->StringDataLen = stringDataLength;
                response->Buffer = buffer;
                response->BufferLen = bufferLength;
                response->DurationMs = stopwatch.ElapsedMilliseconds;
                Diagnostics.Event(
                    context.OperationId,
                    "context_list_objects_v1",
                    $"offset={offset} limit={limit} returned={page.Length}/{totalCount}");
                return 0;
            }
            finally
            {
                context.Release();
            }
        }
        catch (ArgumentException ex)
        {
            Diagnostics.Exception("context_list_objects_v1", ex);
            return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_list_objects_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectTableErrorCode.InternalError);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_list_objects_size_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextListObjectsSizeV1(NativeObjectListRequest* request, NativeObjectTable* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectTableResponse(response);
        try
        {
            var status = BuildListObjectTable(request, stopwatch, out var table, response);
            if (status != 0 || table == null)
            {
                return status;
            }

            response->Status = 0;
            response->ErrorCode = NativeObjectTableErrorCode.None;
            PopulateNativeObjectTableMetadata(response, table, stopwatch);
            response->StringDataLen = EstimateObjectTableStringBytes(table.Page);
            response->BufferLen = RequiredObjectTableBufferLength(table.Page, response->StringDataLen);
            Diagnostics.Event(
                table.Context.OperationId,
                "context_list_objects_size_v1",
                $"offset={table.Offset} limit={table.Limit} returned={table.Page.Length}/{table.TotalCount} buffer_len={response->BufferLen}");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Diagnostics.Exception("context_list_objects_size_v1", ex);
            return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_list_objects_size_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectTableErrorCode.InternalError);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_list_objects_into_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextListObjectsIntoV1(NativeObjectListIntoRequest* request, NativeObjectTable* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectTableResponse(response);
        try
        {
            if (request == null)
            {
                return Fail(response, stopwatch, 1, NativeObjectTableErrorCode.NullPointer);
            }
            if (request->StructSize < sizeof(NativeObjectListIntoRequest))
            {
                return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
            }

            var listRequest = new NativeObjectListRequest
            {
                StructSize = sizeof(NativeObjectListRequest),
                ContextId = request->ContextId,
                Offset = request->Offset,
                Limit = request->Limit,
                AssetTypesCsvUtf8 = request->AssetTypesCsvUtf8,
                AssetTypesCsvUtf8Len = request->AssetTypesCsvUtf8Len,
                Flags = request->Flags,
                Reserved = request->Reserved,
            };
            var status = BuildListObjectTable(&listRequest, stopwatch, out var table, response);
            if (status != 0 || table == null)
            {
                return status;
            }

            var stringDataLength = EstimateObjectTableStringBytes(table.Page);
            var requiredBufferLength = RequiredObjectTableBufferLength(table.Page, stringDataLength);
            PopulateNativeObjectTableMetadata(response, table, stopwatch);
            response->StringDataLen = stringDataLength;
            response->BufferLen = requiredBufferLength;

            if (requiredBufferLength > 0 && (request->Buffer == null || request->BufferLen < requiredBufferLength))
            {
                return Fail(response, stopwatch, 8, NativeObjectTableErrorCode.BufferTooSmall);
            }

            WriteObjectTableInto(
                table.Page,
                request->Buffer,
                request->BufferLen,
                stringDataLength,
                out response->Objects,
                out response->StringData,
                out response->StringDataLen,
                out response->BufferLen);
            response->Buffer = request->Buffer;
            response->Status = 0;
            response->ErrorCode = NativeObjectTableErrorCode.None;
            response->DurationMs = stopwatch.ElapsedMilliseconds;
            Diagnostics.Event(
                table.Context.OperationId,
                "context_list_objects_into_v1",
                $"offset={table.Offset} limit={table.Limit} returned={table.Page.Length}/{table.TotalCount} buffer_len={response->BufferLen}");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Diagnostics.Exception("context_list_objects_into_v1", ex);
            return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_list_objects_into_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectTableErrorCode.InternalError);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_lookup_objects_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextLookupObjectsV1(NativeObjectLookupRequest* request, NativeObjectTable* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectTableResponse(response);
        try
        {
            if (request == null)
            {
                return Fail(response, stopwatch, 1, NativeObjectTableErrorCode.NullPointer);
            }

            if (request->StructSize < sizeof(NativeObjectLookupRequest))
            {
                return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
            }

            var status = BuildLookupObjectTable(request, stopwatch, out var table, response, out var lookupKind);
            if (status != 0 || table == null)
            {
                return status;
            }

            var page = table.Page;
            var buffer = WriteObjectTableToNative(page, out var stringDataOffset, out var stringDataLength, out var bufferLength);

            response->Status = 0;
            response->ErrorCode = NativeObjectTableErrorCode.None;
            response->ContextId = table.ContextId;
            response->Offset = table.Offset;
            response->Limit = table.Limit;
            response->NextOffset = table.NextOffset;
            response->HasMore = table.HasMore ? 1 : 0;
            response->TotalCount = table.TotalCount;
            response->ReturnedCount = page.Length;
            response->Objects = (NativeAssetObject*)buffer;
            response->StringData = buffer == null ? null : buffer + stringDataOffset;
            response->StringDataLen = stringDataLength;
            response->Buffer = buffer;
            response->BufferLen = bufferLength;
            response->DurationMs = stopwatch.ElapsedMilliseconds;
            Diagnostics.Event(
                table.Context.OperationId,
                "context_lookup_objects_v1",
                $"kind={lookupKind} offset={table.Offset} limit={table.Limit} returned={page.Length}/{table.TotalCount}");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Diagnostics.Exception("context_lookup_objects_v1", ex);
            return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_lookup_objects_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectTableErrorCode.InternalError);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_lookup_objects_size_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextLookupObjectsSizeV1(NativeObjectLookupRequest* request, NativeObjectTable* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectTableResponse(response);
        try
        {
            var status = BuildLookupObjectTable(request, stopwatch, out var table, response, out var lookupKind);
            if (status != 0 || table == null)
            {
                return status;
            }

            response->Status = 0;
            response->ErrorCode = NativeObjectTableErrorCode.None;
            PopulateNativeObjectTableMetadata(response, table, stopwatch);
            response->StringDataLen = EstimateObjectTableStringBytes(table.Page);
            response->BufferLen = RequiredObjectTableBufferLength(table.Page, response->StringDataLen);
            Diagnostics.Event(
                table.Context.OperationId,
                "context_lookup_objects_size_v1",
                $"kind={lookupKind} offset={table.Offset} limit={table.Limit} returned={table.Page.Length}/{table.TotalCount} buffer_len={response->BufferLen}");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Diagnostics.Exception("context_lookup_objects_size_v1", ex);
            return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_lookup_objects_size_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectTableErrorCode.InternalError);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_lookup_objects_into_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextLookupObjectsIntoV1(NativeObjectLookupIntoRequest* request, NativeObjectTable* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectTableResponse(response);
        try
        {
            if (request == null)
            {
                return Fail(response, stopwatch, 1, NativeObjectTableErrorCode.NullPointer);
            }
            if (request->StructSize < sizeof(NativeObjectLookupIntoRequest))
            {
                return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
            }

            var lookupRequest = new NativeObjectLookupRequest
            {
                StructSize = sizeof(NativeObjectLookupRequest),
                ContextId = request->ContextId,
                LookupKind = request->LookupKind,
                PathId = request->PathId,
                QueryUtf8 = request->QueryUtf8,
                QueryUtf8Len = request->QueryUtf8Len,
                AssetTypesCsvUtf8 = request->AssetTypesCsvUtf8,
                AssetTypesCsvUtf8Len = request->AssetTypesCsvUtf8Len,
                Offset = request->Offset,
                Limit = request->Limit,
                Flags = request->Flags,
                Reserved = request->Reserved,
            };
            var status = BuildLookupObjectTable(&lookupRequest, stopwatch, out var table, response, out var lookupKind);
            if (status != 0 || table == null)
            {
                return status;
            }

            var stringDataLength = EstimateObjectTableStringBytes(table.Page);
            var requiredBufferLength = RequiredObjectTableBufferLength(table.Page, stringDataLength);
            PopulateNativeObjectTableMetadata(response, table, stopwatch);
            response->StringDataLen = stringDataLength;
            response->BufferLen = requiredBufferLength;

            if (requiredBufferLength > 0 && (request->Buffer == null || request->BufferLen < requiredBufferLength))
            {
                return Fail(response, stopwatch, 8, NativeObjectTableErrorCode.BufferTooSmall);
            }

            WriteObjectTableInto(
                table.Page,
                request->Buffer,
                request->BufferLen,
                stringDataLength,
                out response->Objects,
                out response->StringData,
                out response->StringDataLen,
                out response->BufferLen);
            response->Buffer = request->Buffer;
            response->Status = 0;
            response->ErrorCode = NativeObjectTableErrorCode.None;
            response->DurationMs = stopwatch.ElapsedMilliseconds;
            Diagnostics.Event(
                table.Context.OperationId,
                "context_lookup_objects_into_v1",
                $"kind={lookupKind} offset={table.Offset} limit={table.Limit} returned={table.Page.Length}/{table.TotalCount} buffer_len={response->BufferLen}");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Diagnostics.Exception("context_lookup_objects_into_v1", ex);
            return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_lookup_objects_into_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectTableErrorCode.InternalError);
        }
    }

    private static AssetStudioObjectLookupKind ToCoreLookupKind(NativeObjectLookupKind lookupKind)
    {
        return lookupKind switch
        {
            NativeObjectLookupKind.PathId => AssetStudioObjectLookupKind.PathId,
            NativeObjectLookupKind.Name => AssetStudioObjectLookupKind.Name,
            NativeObjectLookupKind.Container => AssetStudioObjectLookupKind.Container,
            NativeObjectLookupKind.Type => AssetStudioObjectLookupKind.Type,
            _ => throw new ArgumentOutOfRangeException(nameof(lookupKind), lookupKind, null),
        };
    }

    private static int BuildListObjectTable(
        NativeObjectListRequest* request,
        Stopwatch stopwatch,
        out NativeObjectTableBuildResult? table,
        NativeObjectTable* response)
    {
        table = null;
        if (request == null)
        {
            return Fail(response, stopwatch, 1, NativeObjectTableErrorCode.NullPointer);
        }

        if (request->StructSize < sizeof(NativeObjectListRequest))
        {
            return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
        }

        var acquireResult = TryAcquireSession(request->ContextId, out var context);
        if (acquireResult == NativeContextAcquireResult.NotFound || context == null)
        {
            return Fail(response, stopwatch, 4, NativeObjectTableErrorCode.ContextNotFound);
        }
        if (acquireResult == NativeContextAcquireResult.Busy)
        {
            return Fail(response, stopwatch, 5, NativeObjectTableErrorCode.ContextBusy);
        }

        try
        {
            var requestedAssetTypes = ParseNativeAssetTypes(request->AssetTypesCsvUtf8, request->AssetTypesCsvUtf8Len);
            requestedAssetTypes ??= context.RequestedAssetTypes;
            var offset = Math.Max(0, request->Offset);
            var totalCount = context.Session.CountObjects(requestedAssetTypes);
            if (!TryNormalizeObjectTableLimit(request->Limit, totalCount, out var limit))
            {
                return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
            }
            var page = context.Session.ListObjects(new AssetStudioObjectListOptions
            {
                Offset = offset,
                Limit = limit,
                AssetTypes = requestedAssetTypes,
            });
            var nextOffset = offset + page.Length;
            var hasMore = nextOffset < totalCount;
            table = new NativeObjectTableBuildResult(
                context,
                request->ContextId,
                offset,
                limit,
                hasMore ? nextOffset : -1,
                hasMore,
                totalCount,
                page);
            return 0;
        }
        finally
        {
            context.Release();
        }
    }

    private static int BuildLookupObjectTable(
        NativeObjectLookupRequest* request,
        Stopwatch stopwatch,
        out NativeObjectTableBuildResult? table,
        NativeObjectTable* response,
        out NativeObjectLookupKind lookupKind)
    {
        table = null;
        lookupKind = default;
        if (request == null)
        {
            return Fail(response, stopwatch, 1, NativeObjectTableErrorCode.NullPointer);
        }

        if (request->StructSize < sizeof(NativeObjectLookupRequest))
        {
            return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
        }

        var acquireResult = TryAcquireSession(request->ContextId, out var context);
        if (acquireResult == NativeContextAcquireResult.NotFound || context == null)
        {
            return Fail(response, stopwatch, 4, NativeObjectTableErrorCode.ContextNotFound);
        }
        if (acquireResult == NativeContextAcquireResult.Busy)
        {
            return Fail(response, stopwatch, 5, NativeObjectTableErrorCode.ContextBusy);
        }

        try
        {
            lookupKind = (NativeObjectLookupKind)request->LookupKind;
            if (!Enum.IsDefined(typeof(NativeObjectLookupKind), lookupKind))
            {
                return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
            }

            var query = ParseNativeUtf8(request->QueryUtf8, request->QueryUtf8Len, "query_utf8");
            if (lookupKind != NativeObjectLookupKind.PathId && string.IsNullOrEmpty(query))
            {
                return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
            }

            var requestedAssetTypes = ParseNativeAssetTypes(request->AssetTypesCsvUtf8, request->AssetTypesCsvUtf8Len);
            requestedAssetTypes ??= context.RequestedAssetTypes;
            var requestedLimit = request->Limit;
            if (!TryNormalizeObjectTableLimit(requestedLimit, int.MaxValue, out var limit))
            {
                return Fail(response, stopwatch, 2, NativeObjectTableErrorCode.InvalidRequest);
            }
            var lookup = context.Session.LookupObjects(new AssetStudioObjectLookupOptions
            {
                LookupKind = ToCoreLookupKind(lookupKind),
                PathId = request->PathId,
                Query = query,
                Offset = request->Offset,
                Limit = limit,
                Contains = (request->Flags & 1) != 0,
                AssetTypes = requestedAssetTypes,
            });
            var nextOffset = lookup.Offset + lookup.Assets.Length;
            var hasMore = nextOffset < lookup.TotalCount;
            table = new NativeObjectTableBuildResult(
                context,
                request->ContextId,
                lookup.Offset,
                lookup.Limit,
                hasMore ? nextOffset : -1,
                hasMore,
                lookup.TotalCount,
                lookup.Assets);
            return 0;
        }
        finally
        {
            context.Release();
        }
    }

    private static bool TryNormalizeObjectTableLimit(int requestedLimit, int totalCount, out int limit)
    {
        if (requestedLimit > MaxNativeObjectTablePageLimit)
        {
            limit = 0;
            return false;
        }

        limit = requestedLimit <= 0
            ? Math.Min(totalCount, MaxNativeObjectTablePageLimit)
            : requestedLimit;
        return true;
    }

    private static void PopulateNativeObjectTableMetadata(
        NativeObjectTable* response,
        NativeObjectTableBuildResult table,
        Stopwatch stopwatch)
    {
        response->ContextId = table.ContextId;
        response->Offset = table.Offset;
        response->Limit = table.Limit;
        response->NextOffset = table.NextOffset;
        response->HasMore = table.HasMore ? 1 : 0;
        response->TotalCount = table.TotalCount;
        response->ReturnedCount = table.Page.Length;
        response->DurationMs = stopwatch.ElapsedMilliseconds;
    }

    private static void InitializeNativeObjectTableResponse(NativeObjectTable* response)
    {
        *response = default;
        response->StructSize = sizeof(NativeObjectTable);
        response->AbiVersion = FfiAbiVersion;
        response->SchemaVersion = FfiSchemaVersion;
        response->ObjectTableAbiVersion = FfiObjectTableAbiVersion;
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_read_object_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextReadObjectV1(NativeObjectReadRequest* request, NativeObjectReadResponse* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectReadResponse(response);
        try
        {
            if (request == null)
            {
                return Fail(response, stopwatch, 1, NativeObjectReadErrorCode.NullPointer);
            }

            var acquireResult = TryAcquireSession(request->ContextId, out var context);
            if (acquireResult == NativeContextAcquireResult.NotFound || context == null)
            {
                response->ContextId = request->ContextId;
                response->PathId = request->PathId;
                return Fail(response, stopwatch, 4, NativeObjectReadErrorCode.ContextNotFound);
            }
            if (acquireResult == NativeContextAcquireResult.Busy)
            {
                response->ContextId = request->ContextId;
                response->PathId = request->PathId;
                return Fail(response, stopwatch, 5, NativeObjectReadErrorCode.ContextBusy);
            }

            try
            {
                var kind = ReadNativeUtf8(request->KindUtf8, request->KindUtf8Len, defaultValue: "auto");
                var imageFormat = ReadNativeUtf8(request->ImageFormatUtf8, request->ImageFormatUtf8Len, defaultValue: "raw_rgba");
                var result = context.Session.ReadObject(new AssetStudioObjectReadOptions
                {
                    PathId = request->PathId,
                    Kind = kind,
                    ImageFormat = imageFormat,
                });

                if (result.Payload.Length > 0)
                {
                    var payload = (byte*)NativeMemory.Alloc((nuint)result.Payload.Length);
                    Marshal.Copy(result.Payload, 0, (IntPtr)payload, result.Payload.Length);
                    response->Payload = payload;
                    response->PayloadLen = result.Payload.Length;
                }

                response->Buffer = WriteObjectReadStringsToNative(
                    result.PayloadKind,
                    result.SuggestedExtension,
                    out response->PayloadKind,
                    out response->PayloadKindLen,
                    out response->SuggestedExtension,
                    out response->SuggestedExtensionLen,
                    out response->BufferLen);
                response->Status = 0;
                response->ErrorCode = NativeObjectReadErrorCode.None;
                response->ContextId = request->ContextId;
                response->PathId = request->PathId;
                response->TypeId = result.Asset.TypeId;
                response->Size = result.Asset.Size;
                response->DurationMs = stopwatch.ElapsedMilliseconds;
                Diagnostics.Event(context.OperationId, "context_read_object_v1", $"path_id={request->PathId} kind={kind} payload_len={result.Payload.Length}");
                return 0;
            }
            finally
            {
                context.Release();
            }
        }
        catch (ArgumentException ex)
        {
            Diagnostics.Exception("context_read_object_v1", ex);
            ReleaseNativeObjectReadResponseResources(response);
            return Fail(response, stopwatch, 2, NativeObjectReadErrorCode.InvalidRequest);
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_read_object_v1", ex);
            ReleaseNativeObjectReadResponseResources(response);
            return Fail(response, stopwatch, ClassifyReadStatus(ex), ToNativeObjectReadErrorCode(ClassifyReadError(ex)));
        }
    }

    private static void InitializeNativeObjectReadResponse(NativeObjectReadResponse* response)
    {
        *response = default;
        response->AbiVersion = FfiAbiVersion;
        response->SchemaVersion = FfiSchemaVersion;
        response->ObjectReadAbiVersion = FfiObjectReadAbiVersion;
    }

    /// <summary>
    /// Frees native blocks already attached to a response on a failure path and
    /// zeroes the pointers: a non-zero status must never carry live allocations,
    /// because callers only free resources of successful responses.
    /// </summary>
    private static void ReleaseNativeObjectReadResponseResources(NativeObjectReadResponse* response)
    {
        if (response == null)
        {
            return;
        }
        if (response->Payload != null)
        {
            NativeMemory.Free(response->Payload);
            response->Payload = null;
        }
        response->PayloadLen = 0;
        if (response->Buffer != null)
        {
            NativeMemory.Free(response->Buffer);
            response->Buffer = null;
        }
        response->BufferLen = 0;
        response->PayloadKind = null;
        response->PayloadKindLen = 0;
        response->SuggestedExtension = null;
        response->SuggestedExtensionLen = 0;
    }

    /// <inheritdoc cref="ReleaseNativeObjectReadResponseResources"/>
    private static void ReleaseNativeObjectReadBatchResponseResources(NativeObjectReadBatchResponse* response)
    {
        if (response == null)
        {
            return;
        }
        if (response->Payload != null)
        {
            NativeMemory.Free(response->Payload);
            response->Payload = null;
        }
        response->PayloadLen = 0;
        if (response->ItemsBuffer != null)
        {
            NativeMemory.Free(response->ItemsBuffer);
            response->ItemsBuffer = null;
        }
        response->ItemsBufferLen = 0;
        response->Items = null;
        response->StringData = null;
        response->StringDataLen = 0;
        response->ReturnedCount = 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_read_objects_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextReadObjectsV1(NativeObjectReadBatchRequest* request, NativeObjectReadBatchResponse* response)
    {
        return ContextReadObjectsCore(request, response);
    }

    private static int ContextReadObjectsCore(NativeObjectReadBatchRequest* request, NativeObjectReadBatchResponse* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectReadBatchResponse(response);
        NativePayloadAppendStream? payload = null;
        try
        {
            if (request == null)
            {
                return Fail(response, stopwatch, 1, NativeObjectReadErrorCode.NullPointer);
            }
            response->ContextId = request->ContextId;
            response->RequestedCount = Math.Max(0, request->Count);

            if (request->Count < 0)
            {
                return Fail(response, stopwatch, 2, NativeObjectReadErrorCode.InvalidRequest);
            }
            if (request->Count > MaxNativeObjectReadBatchCount)
            {
                return Fail(response, stopwatch, 2, NativeObjectReadErrorCode.InvalidRequest);
            }
            if (request->Count > 0 && request->Items == null)
            {
                return Fail(response, stopwatch, 1, NativeObjectReadErrorCode.NullPointer);
            }
            var acquireResult = TryAcquireSession(request->ContextId, out var context);
            if (acquireResult == NativeContextAcquireResult.NotFound || context == null)
            {
                return Fail(response, stopwatch, 4, NativeObjectReadErrorCode.ContextNotFound);
            }
            if (acquireResult == NativeContextAcquireResult.Busy)
            {
                return Fail(response, stopwatch, 5, NativeObjectReadErrorCode.ContextBusy);
            }

            try
            {
                payload = new NativePayloadAppendStream();
                payload.Reserve(EstimateObjectReadBatchPayloadCapacity(context.Session, request));
                var result = BuildObjectReadBatchInto(context, request->Items, request->Count, payload);
                response->ItemsBuffer = WriteObjectReadBatchItemsToNative(result.Reads, out response->Items, out response->StringData, out response->StringDataLen, out response->ItemsBufferLen);
                response->Payload = payload.Detach();
                response->PayloadLen = payload.Length;
                response->Status = DetermineBatchStatus(result.Reads, result.FailedCount, request->Count);
                response->ErrorCode = DetermineBatchErrorCode(result.Reads, result.FailedCount, request->Count);
                response->ReturnedCount = result.Reads.Count;
                response->FailedCount = result.FailedCount;
                response->DurationMs = stopwatch.ElapsedMilliseconds;
                Diagnostics.Event(context.OperationId, "context_read_objects_v1", $"count={request->Count} failed={result.FailedCount} payload_len={result.PayloadLen}");
                return response->Status;
            }
            finally
            {
                context.Release();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_read_objects_v1", ex);
            ReleaseNativeObjectReadBatchResponseResources(response);
            return Fail(response, stopwatch, 100, NativeObjectReadErrorCode.InternalError);
        }
        finally
        {
            payload?.Dispose();
        }
    }

    private static void InitializeNativeObjectReadBatchResponse(NativeObjectReadBatchResponse* response)
    {
        *response = default;
        response->AbiVersion = FfiAbiVersion;
        response->SchemaVersion = FfiSchemaVersion;
        response->ObjectReadBatchAbiVersion = FfiObjectReadBatchAbiVersion;
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_read_objects_handle_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextReadObjectsHandleV1(NativeObjectReadBatchRequest* request, NativeObjectReadBatchResponseV1* response)
    {
        if (response == null)
        {
            return 1;
        }

        return ContextReadObjectsHandleCore(request, response);
    }

    private static int ContextReadObjectsHandleCore(NativeObjectReadBatchRequest* request, NativeObjectReadBatchResponseV1* response)
    {
        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectReadBatchResponseV1(response);
        NativePayloadAppendStream? payload = null;
        byte* itemsBuffer = null;
        var registered = false;
        try
        {
            if (request == null)
            {
                return Fail(response, stopwatch, 1, NativeObjectReadErrorCode.NullPointer);
            }
            response->ContextId = request->ContextId;
            response->RequestedCount = Math.Max(0, request->Count);

            if (request->Count < 0)
            {
                return Fail(response, stopwatch, 2, NativeObjectReadErrorCode.InvalidRequest);
            }
            if (request->Count > MaxNativeObjectReadBatchCount)
            {
                return Fail(response, stopwatch, 2, NativeObjectReadErrorCode.InvalidRequest);
            }
            if (request->Count > 0 && request->Items == null)
            {
                return Fail(response, stopwatch, 1, NativeObjectReadErrorCode.NullPointer);
            }
            var acquireResult = TryAcquireSession(request->ContextId, out var context);
            if (acquireResult == NativeContextAcquireResult.NotFound || context == null)
            {
                return Fail(response, stopwatch, 4, NativeObjectReadErrorCode.ContextNotFound);
            }
            if (acquireResult == NativeContextAcquireResult.Busy)
            {
                return Fail(response, stopwatch, 5, NativeObjectReadErrorCode.ContextBusy);
            }

            try
            {
                payload = new NativePayloadAppendStream();
                payload.Reserve(EstimateObjectReadBatchPayloadCapacity(context.Session, request));
                var result = BuildObjectReadBatchInto(context, request->Items, request->Count, payload);
                var reads = result.Reads;
                var failedCount = result.FailedCount;

                itemsBuffer = WriteObjectReadBatchItemsToNative(reads, out response->Items, out response->StringData, out response->StringDataLen, out response->ItemsBufferLen);
                response->ItemsBuffer = itemsBuffer;
                response->Payload = payload.Detach();
                response->PayloadLen = payload.Length;
                response->Status = DetermineBatchStatus(reads, failedCount, request->Count);
                response->ErrorCode = DetermineBatchErrorCode(reads, failedCount, request->Count);
                response->ReturnedCount = reads.Count;
                response->FailedCount = failedCount;
                response->DurationMs = stopwatch.ElapsedMilliseconds;
                if (response->ItemsBuffer != null || response->Payload != null)
                {
                    response->ResultHandle = RegisterResultArena(request->ContextId, response->ItemsBuffer, response->Payload);
                    registered = true;
                }
                Diagnostics.Event(context.OperationId, "context_read_objects_v1", $"count={request->Count} failed={failedCount} payload_len={response->PayloadLen}");
                return response->Status;
            }
            finally
            {
                context.Release();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_read_objects_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectReadErrorCode.InternalError);
        }
        finally
        {
            if (!registered)
            {
                if (itemsBuffer != null)
                {
                    NativeMemory.Free(itemsBuffer);
                }
                if (response->Payload != null)
                {
                    NativeMemory.Free(response->Payload);
                }
                payload?.Dispose();
                response->Items = null;
                response->StringData = null;
                response->ItemsBuffer = null;
                response->Payload = null;
                response->ItemsBufferLen = 0;
                response->PayloadLen = 0;
            }
        }
    }

    private static void InitializeNativeObjectReadBatchResponseV1(NativeObjectReadBatchResponseV1* response)
    {
        *response = default;
        response->AbiVersion = FfiAbiVersion;
        response->SchemaVersion = FfiSchemaVersion;
        response->ObjectReadBatchAbiVersion = FfiObjectReadBatchAbiVersion;
        response->ObjectReadBatchHandleAbiVersion = FfiObjectReadBatchHandleAbiVersion;
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_read_objects_size_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextReadObjectsSizeV1(NativeObjectReadBatchRequestV1* request, NativeObjectReadBatchSizeResponseV1* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectReadBatchSizeResponseV1(response);
        try
        {
            var status = ValidateObjectReadBatchRequestV1(request, stopwatch, out var context, response);
            if (status != 0 || context == null)
            {
                return status;
            }

            try
            {
                var signature = BuildObjectReadBatchSignature(request->Items, request->Count);
                var result = BuildObjectReadBatch(context, request->Items, request->Count, capturePayloads: true);
                if (ShouldCacheObjectReadBatch(result))
                {
                    context.SetPendingReadBatch(signature, result);
                }
                else
                {
                    context.ClearPendingReadBatch();
                }
                response->Status = DetermineBatchStatus(result.Reads, result.FailedCount, request->Count);
                response->ErrorCode = DetermineBatchErrorCode(result.Reads, result.FailedCount, request->Count);
                response->ContextId = request->ContextId;
                response->RequestedCount = request->Count;
                response->ReturnedCount = result.Reads.Count;
                response->FailedCount = result.FailedCount;
                response->RequiredItemsBufferLen = result.ItemsBufferLen;
                response->RequiredStringDataLen = result.StringDataLen;
                response->RequiredPayloadLen = result.PayloadLen;
                response->ItemsBufferLen = result.ItemsBufferLen;
                response->StringDataLen = result.StringDataLen;
                response->PayloadLen = result.PayloadLen;
                response->DurationMs = stopwatch.ElapsedMilliseconds;
                Diagnostics.Event(context.OperationId, "context_read_objects_size_v1", $"count={request->Count} failed={result.FailedCount} payload_len={result.PayloadLen}");
                return response->Status;
            }
            finally
            {
                context.Release();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_read_objects_size_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectReadErrorCode.InternalError);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_read_objects_into_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextReadObjectsIntoV1(NativeObjectReadBatchIntoRequestV1* request, NativeObjectReadBatchIntoResponseV1* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectReadBatchIntoResponseV1(response);
        try
        {
            var status = ValidateObjectReadBatchIntoRequestV1(request, stopwatch, out var context, response);
            if (status != 0 || context == null)
            {
                return status;
            }

            try
            {
                var signature = BuildObjectReadBatchSignature(request->Items, request->Count);
                // Single-read protocol: reuse the payload bytes captured by size_v1 when
                // available, otherwise capture once here. The packed payload block is then
                // reproduced by memcpy — objects are never read (or decoded) a second time.
                var result = context.TryGetPendingReadBatch(signature, out var cachedResult) && cachedResult.HasCapturedPayloads
                    ? cachedResult
                    : BuildObjectReadBatch(context, request->Items, request->Count, capturePayloads: true);
                response->ContextId = request->ContextId;
                response->RequestedCount = request->Count;
                response->ReturnedCount = result.Reads.Count;
                response->FailedCount = result.FailedCount;
                response->RequiredItemsBufferLen = result.ItemsBufferLen;
                response->RequiredStringDataLen = result.StringDataLen;
                response->RequiredPayloadLen = result.PayloadLen;

                if ((result.ItemsBufferLen > 0 && (request->ItemsBuffer == null || request->ItemsBufferLen < result.ItemsBufferLen))
                    || (result.PayloadLen > 0 && (request->Payload == null || request->PayloadLen < result.PayloadLen)))
                {
                    response->Status = 8;
                    response->ErrorCode = NativeObjectReadErrorCode.BufferTooSmall;
                    response->ItemsBufferLen = request->ItemsBufferLen;
                    response->PayloadLen = request->PayloadLen;
                    response->DurationMs = stopwatch.ElapsedMilliseconds;
                    return 8;
                }

                WriteObjectReadBatchItemsV1Into(
                    result.Reads,
                    request->ItemsBuffer,
                    result.ItemsBufferLen,
                    out response->Items,
                    out response->StringData,
                    out response->StringDataLen);
                WriteObjectReadBatchPayloadInto(result.Reads, request->Payload, result.PayloadLen);

                response->ItemsBuffer = request->ItemsBuffer;
                response->ItemsBufferLen = result.ItemsBufferLen;
                response->Payload = request->Payload;
                response->PayloadLen = result.PayloadLen;
                response->Status = DetermineBatchStatus(result.Reads, result.FailedCount, request->Count);
                response->ErrorCode = DetermineBatchErrorCode(result.Reads, result.FailedCount, request->Count);
                response->DurationMs = stopwatch.ElapsedMilliseconds;
                context.ClearPendingReadBatch(signature);
                Diagnostics.Event(context.OperationId, "context_read_objects_into_v1", $"count={request->Count} failed={result.FailedCount} payload_len={result.PayloadLen}");
                return response->Status;
            }
            finally
            {
                context.Release();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_read_objects_into_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectReadErrorCode.InternalError);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_read_objects_by_index_size_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextReadObjectsByIndexSizeV1(NativeObjectReadBatchByIndexRequestV1* request, NativeObjectReadBatchSizeResponseV1* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectReadBatchSizeResponseV1(response);
        try
        {
            var status = ValidateObjectReadBatchByIndexRequestV1(request, stopwatch, out var context, response);
            if (status != 0 || context == null)
            {
                return status;
            }

            try
            {
                var signature = BuildObjectReadBatchByIndexSignature(request->Items, request->Count);
                // Capture payload bytes so the following into_v1 call can memcpy them
                // instead of re-reading (and re-decoding) every object. Mirrors the
                // path-id size_v1 behavior.
                var result = BuildObjectReadBatchByIndex(context, request->Items, request->Count, capturePayloads: true);
                if (ShouldCacheObjectReadBatch(result))
                {
                    context.SetPendingReadBatch(signature, result);
                }
                else
                {
                    context.ClearPendingReadBatch();
                }
                response->Status = DetermineBatchStatus(result.Reads, result.FailedCount, request->Count);
                response->ErrorCode = DetermineBatchErrorCode(result.Reads, result.FailedCount, request->Count);
                response->ContextId = request->ContextId;
                response->RequestedCount = request->Count;
                response->ReturnedCount = result.Reads.Count;
                response->FailedCount = result.FailedCount;
                response->RequiredItemsBufferLen = result.ItemsBufferLen;
                response->RequiredStringDataLen = result.StringDataLen;
                response->RequiredPayloadLen = result.PayloadLen;
                response->ItemsBufferLen = result.ItemsBufferLen;
                response->StringDataLen = result.StringDataLen;
                response->PayloadLen = result.PayloadLen;
                response->DurationMs = stopwatch.ElapsedMilliseconds;
                Diagnostics.Event(context.OperationId, "context_read_objects_by_index_size_v1", $"count={request->Count} failed={result.FailedCount} payload_len={result.PayloadLen}");
                return response->Status;
            }
            finally
            {
                context.Release();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_read_objects_by_index_size_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectReadErrorCode.InternalError);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_read_objects_by_index_into_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextReadObjectsByIndexIntoV1(NativeObjectReadBatchByIndexIntoRequestV1* request, NativeObjectReadBatchIntoResponseV1* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectReadBatchIntoResponseV1(response);
        try
        {
            var status = ValidateObjectReadBatchByIndexIntoRequestV1(request, stopwatch, out var context, response);
            if (status != 0 || context == null)
            {
                return status;
            }

            try
            {
                var signature = BuildObjectReadBatchByIndexSignature(request->Items, request->Count);
                // Single-read protocol: see ContextReadObjectsIntoV1.
                var result = context.TryGetPendingReadBatch(signature, out var cachedResult) && cachedResult.HasCapturedPayloads
                    ? cachedResult
                    : BuildObjectReadBatchByIndex(context, request->Items, request->Count, capturePayloads: true);
                response->ContextId = request->ContextId;
                response->RequestedCount = request->Count;
                response->ReturnedCount = result.Reads.Count;
                response->FailedCount = result.FailedCount;
                response->RequiredItemsBufferLen = result.ItemsBufferLen;
                response->RequiredStringDataLen = result.StringDataLen;
                response->RequiredPayloadLen = result.PayloadLen;

                if ((result.ItemsBufferLen > 0 && (request->ItemsBuffer == null || request->ItemsBufferLen < result.ItemsBufferLen))
                    || (result.PayloadLen > 0 && (request->Payload == null || request->PayloadLen < result.PayloadLen)))
                {
                    response->Status = 8;
                    response->ErrorCode = NativeObjectReadErrorCode.BufferTooSmall;
                    response->ItemsBufferLen = request->ItemsBufferLen;
                    response->PayloadLen = request->PayloadLen;
                    response->DurationMs = stopwatch.ElapsedMilliseconds;
                    return 8;
                }

                WriteObjectReadBatchItemsV1Into(
                    result.Reads,
                    request->ItemsBuffer,
                    result.ItemsBufferLen,
                    out response->Items,
                    out response->StringData,
                    out response->StringDataLen);
                WriteObjectReadBatchPayloadInto(result.Reads, request->Payload, result.PayloadLen);

                response->ItemsBuffer = request->ItemsBuffer;
                response->ItemsBufferLen = result.ItemsBufferLen;
                response->Payload = request->Payload;
                response->PayloadLen = result.PayloadLen;
                response->Status = DetermineBatchStatus(result.Reads, result.FailedCount, request->Count);
                response->ErrorCode = DetermineBatchErrorCode(result.Reads, result.FailedCount, request->Count);
                response->DurationMs = stopwatch.ElapsedMilliseconds;
                context.ClearPendingReadBatch(signature);
                Diagnostics.Event(context.OperationId, "context_read_objects_by_index_into_v1", $"count={request->Count} failed={result.FailedCount} payload_len={result.PayloadLen}");
                return response->Status;
            }
            finally
            {
                context.Release();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_read_objects_by_index_into_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectReadErrorCode.InternalError);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_read_objects_direct_into_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextReadObjectsDirectIntoV1(NativeObjectReadBatchIntoRequestV1* request, NativeObjectReadBatchIntoResponseV1* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectReadBatchIntoResponseV1(response);
        try
        {
            var status = ValidateObjectReadBatchIntoRequestV1(request, stopwatch, out var context, response);
            if (status != 0 || context == null)
            {
                return status;
            }

            try
            {
                // Single pass: payload bytes stream straight into the caller buffer while
                // they fit. On overflow the stream keeps counting so the exact required
                // sizes are still reported with BUFFER_TOO_SMALL; caller buffer contents
                // are unspecified in that case.
                using var payloadStream = new NativeOptimisticPayloadStream(request->Payload, request->PayloadLen, spillToNativeOnOverflow: false);
                var result = BuildObjectReadBatchInto(context, request->Items, request->Count, payloadStream);
                response->ContextId = request->ContextId;
                response->RequestedCount = request->Count;
                response->ReturnedCount = result.Reads.Count;
                response->FailedCount = result.FailedCount;
                response->RequiredItemsBufferLen = result.ItemsBufferLen;
                response->RequiredStringDataLen = result.StringDataLen;
                response->RequiredPayloadLen = result.PayloadLen;

                if ((result.ItemsBufferLen > 0 && (request->ItemsBuffer == null || request->ItemsBufferLen < result.ItemsBufferLen))
                    || payloadStream.Overflowed)
                {
                    response->Status = 8;
                    response->ErrorCode = NativeObjectReadErrorCode.BufferTooSmall;
                    response->ItemsBufferLen = request->ItemsBufferLen;
                    response->PayloadLen = request->PayloadLen;
                    response->DurationMs = stopwatch.ElapsedMilliseconds;
                    return 8;
                }

                WriteObjectReadBatchItemsV1Into(
                    result.Reads,
                    request->ItemsBuffer,
                    result.ItemsBufferLen,
                    out response->Items,
                    out response->StringData,
                    out response->StringDataLen);

                response->ItemsBuffer = request->ItemsBuffer;
                response->ItemsBufferLen = result.ItemsBufferLen;
                response->Payload = request->Payload;
                response->PayloadLen = result.PayloadLen;
                response->Status = DetermineBatchStatus(result.Reads, result.FailedCount, request->Count);
                response->ErrorCode = DetermineBatchErrorCode(result.Reads, result.FailedCount, request->Count);
                response->DurationMs = stopwatch.ElapsedMilliseconds;
                Diagnostics.Event(context.OperationId, "context_read_objects_direct_into_v1", $"count={request->Count} failed={result.FailedCount} payload_len={result.PayloadLen}");
                return response->Status;
            }
            finally
            {
                context.Release();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_read_objects_direct_into_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectReadErrorCode.InternalError);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_read_objects_by_index_direct_into_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextReadObjectsByIndexDirectIntoV1(NativeObjectReadBatchByIndexIntoRequestV1* request, NativeObjectReadBatchIntoResponseV1* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectReadBatchIntoResponseV1(response);
        try
        {
            var status = ValidateObjectReadBatchByIndexIntoRequestV1(request, stopwatch, out var context, response);
            if (status != 0 || context == null)
            {
                return status;
            }

            try
            {
                // Single pass: see ContextReadObjectsDirectIntoV1.
                using var payloadStream = new NativeOptimisticPayloadStream(request->Payload, request->PayloadLen, spillToNativeOnOverflow: false);
                var result = BuildObjectReadBatchByIndexInto(context, request->Items, request->Count, payloadStream);
                response->ContextId = request->ContextId;
                response->RequestedCount = request->Count;
                response->ReturnedCount = result.Reads.Count;
                response->FailedCount = result.FailedCount;
                response->RequiredItemsBufferLen = result.ItemsBufferLen;
                response->RequiredStringDataLen = result.StringDataLen;
                response->RequiredPayloadLen = result.PayloadLen;

                if ((result.ItemsBufferLen > 0 && (request->ItemsBuffer == null || request->ItemsBufferLen < result.ItemsBufferLen))
                    || payloadStream.Overflowed)
                {
                    response->Status = 8;
                    response->ErrorCode = NativeObjectReadErrorCode.BufferTooSmall;
                    response->ItemsBufferLen = request->ItemsBufferLen;
                    response->PayloadLen = request->PayloadLen;
                    response->DurationMs = stopwatch.ElapsedMilliseconds;
                    return 8;
                }

                WriteObjectReadBatchItemsV1Into(
                    result.Reads,
                    request->ItemsBuffer,
                    result.ItemsBufferLen,
                    out response->Items,
                    out response->StringData,
                    out response->StringDataLen);

                response->ItemsBuffer = request->ItemsBuffer;
                response->ItemsBufferLen = result.ItemsBufferLen;
                response->Payload = request->Payload;
                response->PayloadLen = result.PayloadLen;
                response->Status = DetermineBatchStatus(result.Reads, result.FailedCount, request->Count);
                response->ErrorCode = DetermineBatchErrorCode(result.Reads, result.FailedCount, request->Count);
                response->DurationMs = stopwatch.ElapsedMilliseconds;
                Diagnostics.Event(context.OperationId, "context_read_objects_by_index_direct_into_v1", $"count={request->Count} failed={result.FailedCount} payload_len={result.PayloadLen}");
                return response->Status;
            }
            finally
            {
                context.Release();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_read_objects_by_index_direct_into_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectReadErrorCode.InternalError);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_read_objects_direct_retry_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextReadObjectsDirectRetryV1(NativeObjectReadBatchIntoRequestV1* request, NativeObjectReadBatchRetryResponseV1* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectReadBatchRetryResponseV1(response);
        NativeObjectReadBatchIntoResponseV1 validationResponse = default;
        InitializeNativeObjectReadBatchIntoResponseV1(&validationResponse);
        try
        {
            var status = ValidateObjectReadBatchIntoRequestV1(request, stopwatch, out var context, &validationResponse);
            CopyRetryValidationResponse(response, &validationResponse);
            if (status != 0 || context == null)
            {
                return status;
            }

            try
            {
                // Single pass: payload bytes stream straight into the caller buffer and
                // spill into a native buffer on overflow, so objects are read (and
                // textures decoded) exactly once.
                using var payloadStream = new NativeOptimisticPayloadStream(request->Payload, request->PayloadLen, spillToNativeOnOverflow: true);
                var result = BuildObjectReadBatchInto(context, request->Items, request->Count, payloadStream);
                var rc = WriteObjectReadBatchRetryResultV1(
                    result,
                    request->ContextId,
                    request->Count,
                    request->ItemsBuffer,
                    request->ItemsBufferLen,
                    request->Payload,
                    payloadStream,
                    stopwatch,
                    response);
                Diagnostics.Event(context.OperationId, "context_read_objects_direct_retry_v1", $"count={request->Count} failed={result.FailedCount} payload_len={result.PayloadLen} handle={response->ResultHandle}");
                return rc;
            }
            finally
            {
                context.Release();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_read_objects_direct_retry_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectReadErrorCode.InternalError);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_context_read_objects_by_index_direct_retry_v1", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ContextReadObjectsByIndexDirectRetryV1(NativeObjectReadBatchByIndexIntoRequestV1* request, NativeObjectReadBatchRetryResponseV1* response)
    {
        if (response == null)
        {
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        InitializeNativeObjectReadBatchRetryResponseV1(response);
        NativeObjectReadBatchIntoResponseV1 validationResponse = default;
        InitializeNativeObjectReadBatchIntoResponseV1(&validationResponse);
        try
        {
            var status = ValidateObjectReadBatchByIndexIntoRequestV1(request, stopwatch, out var context, &validationResponse);
            CopyRetryValidationResponse(response, &validationResponse);
            if (status != 0 || context == null)
            {
                return status;
            }

            try
            {
                // Single pass: see ContextReadObjectsDirectRetryV1.
                using var payloadStream = new NativeOptimisticPayloadStream(request->Payload, request->PayloadLen, spillToNativeOnOverflow: true);
                var result = BuildObjectReadBatchByIndexInto(context, request->Items, request->Count, payloadStream);
                var rc = WriteObjectReadBatchRetryResultV1(
                    result,
                    request->ContextId,
                    request->Count,
                    request->ItemsBuffer,
                    request->ItemsBufferLen,
                    request->Payload,
                    payloadStream,
                    stopwatch,
                    response);
                Diagnostics.Event(context.OperationId, "context_read_objects_by_index_direct_retry_v1", $"count={request->Count} failed={result.FailedCount} payload_len={result.PayloadLen} handle={response->ResultHandle}");
                return rc;
            }
            finally
            {
                context.Release();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Exception("context_read_objects_by_index_direct_retry_v1", ex);
            return Fail(response, stopwatch, 100, NativeObjectReadErrorCode.InternalError);
        }
    }

    private static void InitializeNativeObjectReadBatchSizeResponseV1(NativeObjectReadBatchSizeResponseV1* response)
    {
        *response = default;
        response->StructSize = sizeof(NativeObjectReadBatchSizeResponseV1);
        response->AbiVersion = FfiAbiVersion;
        response->SchemaVersion = FfiSchemaVersion;
        response->ObjectReadBatchAbiVersion = FfiObjectReadBatchAbiVersion;
        response->ObjectReadBatchIntoAbiVersion = FfiObjectReadBatchIntoAbiVersion;
    }

    private static void InitializeNativeObjectReadBatchRetryResponseV1(NativeObjectReadBatchRetryResponseV1* response)
    {
        *response = default;
        response->StructSize = sizeof(NativeObjectReadBatchRetryResponseV1);
        response->AbiVersion = FfiAbiVersion;
        response->SchemaVersion = FfiSchemaVersion;
        response->ObjectReadBatchAbiVersion = FfiObjectReadBatchAbiVersion;
        response->ObjectReadBatchIntoAbiVersion = FfiObjectReadBatchIntoAbiVersion;
        response->ObjectReadBatchDirectRetryAbiVersion = FfiObjectReadBatchDirectRetryAbiVersion;
    }

    private static void CopyRetryValidationResponse(NativeObjectReadBatchRetryResponseV1* target, NativeObjectReadBatchIntoResponseV1* source)
    {
        target->Status = source->Status;
        target->ErrorCode = source->ErrorCode;
        target->ContextId = source->ContextId;
        target->RequestedCount = source->RequestedCount;
        target->ReturnedCount = source->ReturnedCount;
        target->FailedCount = source->FailedCount;
        target->DurationMs = source->DurationMs;
    }

    private static void InitializeNativeObjectReadBatchIntoResponseV1(NativeObjectReadBatchIntoResponseV1* response)
    {
        *response = default;
        response->StructSize = sizeof(NativeObjectReadBatchIntoResponseV1);
        response->AbiVersion = FfiAbiVersion;
        response->SchemaVersion = FfiSchemaVersion;
        response->ObjectReadBatchAbiVersion = FfiObjectReadBatchAbiVersion;
        response->ObjectReadBatchIntoAbiVersion = FfiObjectReadBatchIntoAbiVersion;
    }

    // Shared request validation for the typed batch read entry points. The request
    // structs differ only in item layout; the checks are identical per response type.

    private static int ValidateObjectReadBatchCore(
        NativeObjectReadBatchSizeResponseV1* response,
        Stopwatch stopwatch,
        long contextId,
        bool structSizeOk,
        int count,
        bool itemsNull,
        out ActiveNativeContext? context)
    {
        context = null;
        response->ContextId = contextId;
        response->RequestedCount = Math.Max(0, count);
        if (!structSizeOk || count < 0 || count > MaxNativeObjectReadBatchCount)
        {
            return Fail(response, stopwatch, 2, NativeObjectReadErrorCode.InvalidRequest);
        }
        if (count > 0 && itemsNull)
        {
            return Fail(response, stopwatch, 1, NativeObjectReadErrorCode.NullPointer);
        }
        var acquireResult = TryAcquireSession(contextId, out context);
        if (acquireResult == NativeContextAcquireResult.NotFound || context == null)
        {
            return Fail(response, stopwatch, 4, NativeObjectReadErrorCode.ContextNotFound);
        }
        if (acquireResult == NativeContextAcquireResult.Busy)
        {
            return Fail(response, stopwatch, 5, NativeObjectReadErrorCode.ContextBusy);
        }
        return 0;
    }

    private static int ValidateObjectReadBatchCore(
        NativeObjectReadBatchIntoResponseV1* response,
        Stopwatch stopwatch,
        long contextId,
        bool structSizeOk,
        int count,
        bool itemsNull,
        out ActiveNativeContext? context)
    {
        context = null;
        response->ContextId = contextId;
        response->RequestedCount = Math.Max(0, count);
        if (!structSizeOk || count < 0 || count > MaxNativeObjectReadBatchCount)
        {
            return Fail(response, stopwatch, 2, NativeObjectReadErrorCode.InvalidRequest);
        }
        if (count > 0 && itemsNull)
        {
            return Fail(response, stopwatch, 1, NativeObjectReadErrorCode.NullPointer);
        }
        var acquireResult = TryAcquireSession(contextId, out context);
        if (acquireResult == NativeContextAcquireResult.NotFound || context == null)
        {
            return Fail(response, stopwatch, 4, NativeObjectReadErrorCode.ContextNotFound);
        }
        if (acquireResult == NativeContextAcquireResult.Busy)
        {
            return Fail(response, stopwatch, 5, NativeObjectReadErrorCode.ContextBusy);
        }
        return 0;
    }

    private static int ValidateObjectReadBatchRequestV1(
        NativeObjectReadBatchRequestV1* request,
        Stopwatch stopwatch,
        out ActiveNativeContext? context,
        NativeObjectReadBatchSizeResponseV1* response)
    {
        if (request == null)
        {
            context = null;
            return Fail(response, stopwatch, 1, NativeObjectReadErrorCode.NullPointer);
        }

        return ValidateObjectReadBatchCore(
            response,
            stopwatch,
            request->ContextId,
            request->StructSize >= sizeof(NativeObjectReadBatchRequestV1),
            request->Count,
            request->Items == null,
            out context);
    }

    private static int ValidateObjectReadBatchIntoRequestV1(
        NativeObjectReadBatchIntoRequestV1* request,
        Stopwatch stopwatch,
        out ActiveNativeContext? context,
        NativeObjectReadBatchIntoResponseV1* response)
    {
        if (request == null)
        {
            context = null;
            return Fail(response, stopwatch, 1, NativeObjectReadErrorCode.NullPointer);
        }

        return ValidateObjectReadBatchCore(
            response,
            stopwatch,
            request->ContextId,
            request->StructSize >= sizeof(NativeObjectReadBatchIntoRequestV1),
            request->Count,
            request->Items == null,
            out context);
    }

    private static int ValidateObjectReadBatchByIndexRequestV1(
        NativeObjectReadBatchByIndexRequestV1* request,
        Stopwatch stopwatch,
        out ActiveNativeContext? context,
        NativeObjectReadBatchSizeResponseV1* response)
    {
        if (request == null)
        {
            context = null;
            return Fail(response, stopwatch, 1, NativeObjectReadErrorCode.NullPointer);
        }

        return ValidateObjectReadBatchCore(
            response,
            stopwatch,
            request->ContextId,
            request->StructSize >= sizeof(NativeObjectReadBatchByIndexRequestV1),
            request->Count,
            request->Items == null,
            out context);
    }

    private static int ValidateObjectReadBatchByIndexIntoRequestV1(
        NativeObjectReadBatchByIndexIntoRequestV1* request,
        Stopwatch stopwatch,
        out ActiveNativeContext? context,
        NativeObjectReadBatchIntoResponseV1* response)
    {
        if (request == null)
        {
            context = null;
            return Fail(response, stopwatch, 1, NativeObjectReadErrorCode.NullPointer);
        }

        return ValidateObjectReadBatchCore(
            response,
            stopwatch,
            request->ContextId,
            request->StructSize >= sizeof(NativeObjectReadBatchByIndexIntoRequestV1),
            request->Count,
            request->Items == null,
            out context);
    }

    private static int WriteObjectReadBatchRetryResultV1(
        NativeObjectReadBatchBuildResult result,
        long contextId,
        int requestedCount,
        byte* callerItemsBuffer,
        long callerItemsBufferLen,
        byte* callerPayload,
        NativeOptimisticPayloadStream payload,
        Stopwatch stopwatch,
        NativeObjectReadBatchRetryResponseV1* response)
    {
        const int NativeItemsOwnership = 1;
        const int NativePayloadOwnership = 2;

        response->ContextId = contextId;
        response->RequestedCount = requestedCount;
        response->ReturnedCount = result.Reads.Count;
        response->FailedCount = result.FailedCount;
        response->RequiredItemsBufferLen = result.ItemsBufferLen;
        response->RequiredStringDataLen = result.StringDataLen;
        response->RequiredPayloadLen = result.PayloadLen;

        // The payload was already written by the single read pass — either fully into
        // the caller buffer or spilled into a native buffer the stream owns.
        var useNativeItemsBuffer = result.ItemsBufferLen > 0 && (callerItemsBuffer == null || callerItemsBufferLen < result.ItemsBufferLen);
        var useNativePayload = payload.UsedNativeBuffer;
        byte* itemsBuffer = useNativeItemsBuffer
            ? (byte*)NativeMemory.AllocZeroed((nuint)result.ItemsBufferLen)
            : callerItemsBuffer;
        byte* nativePayload = null;
        var registered = false;

        try
        {
            WriteObjectReadBatchItemsV1Into(
                result.Reads,
                itemsBuffer,
                result.ItemsBufferLen,
                out response->Items,
                out response->StringData,
                out response->StringDataLen);
            nativePayload = useNativePayload ? payload.Detach() : null;

            response->ItemsBuffer = itemsBuffer;
            response->ItemsBufferLen = result.ItemsBufferLen;
            response->Payload = useNativePayload ? nativePayload : callerPayload;
            response->PayloadLen = result.PayloadLen;
            response->Status = DetermineBatchStatus(result.Reads, result.FailedCount, requestedCount);
            response->ErrorCode = DetermineBatchErrorCode(result.Reads, result.FailedCount, requestedCount);
            response->OwnershipFlags = (useNativeItemsBuffer ? NativeItemsOwnership : 0) | (useNativePayload ? NativePayloadOwnership : 0);
            response->DurationMs = stopwatch.ElapsedMilliseconds;
            if (response->OwnershipFlags != 0)
            {
                response->ResultHandle = RegisterResultArena(
                    contextId,
                    useNativeItemsBuffer ? itemsBuffer : null,
                    useNativePayload ? nativePayload : null);
                registered = true;
            }

            return response->Status;
        }
        finally
        {
            if (!registered)
            {
                if (useNativeItemsBuffer && itemsBuffer != null)
                {
                    NativeMemory.Free(itemsBuffer);
                }
                if (nativePayload != null)
                {
                    NativeMemory.Free(nativePayload);
                }
                if (useNativeItemsBuffer || nativePayload != null)
                {
                    // Never leave freed native pointers visible to the caller after an
                    // exception between response assembly and arena registration.
                    response->Items = null;
                    response->StringData = null;
                    response->StringDataLen = 0;
                    response->ItemsBuffer = null;
                    response->ItemsBufferLen = 0;
                    response->Payload = null;
                    response->PayloadLen = 0;
                    response->OwnershipFlags = 0;
                    response->ResultHandle = 0;
                }
            }
        }
    }

    // The four Build* entry points below share one parse + execute + finish pipeline.
    // Only the item layout (path-id vs stable index) and the payload strategy
    // (captured managed arrays vs streaming into a writer) differ.

    private sealed class ParsedObjectReadBatch
    {
        public ParsedObjectReadBatch(int count)
        {
            OptionIndexes = new List<int>(count);
            Options = new List<AssetStudioObjectReadOptions>(count);
            Reads = new List<NativeObjectReadItemBuildResult>(count);
        }

        // Maps batch result indexes (into Options) back to the caller's item order.
        public List<int> OptionIndexes { get; }
        public List<AssetStudioObjectReadOptions> Options { get; }
        // Seeded with per-item parse failures; FinishObjectReadBatch appends the rest.
        public List<NativeObjectReadItemBuildResult> Reads { get; }
    }

    private static ParsedObjectReadBatch ParseObjectReadBatchItems(
        NativeObjectReadItemRequest* items,
        int count,
        string diagnosticsScope)
    {
        var parsed = new ParsedObjectReadBatch(count);
        for (var i = 0; i < count; i++)
        {
            var item = items[i];
            try
            {
                var kind = ReadNativeUtf8(item.KindUtf8, item.KindUtf8Len, defaultValue: "auto");
                var imageFormat = ReadNativeUtf8(item.ImageFormatUtf8, item.ImageFormatUtf8Len, defaultValue: "raw_rgba");
                parsed.OptionIndexes.Add(i);
                parsed.Options.Add(new AssetStudioObjectReadOptions
                {
                    PathId = item.PathId,
                    Kind = kind,
                    ImageFormat = imageFormat,
                });
            }
            catch (ArgumentException ex)
            {
                Diagnostics.Exception(diagnosticsScope, ex);
                parsed.Reads.Add(NativeObjectReadItemBuildResult.Fail(
                    i,
                    item.PathId,
                    2,
                    NativeObjectReadErrorCode.InvalidRequest,
                    ex.Message));
            }
        }
        return parsed;
    }

    private static ParsedObjectReadBatch ParseObjectReadBatchItemsByIndex(
        NativeObjectReadItemByIndexRequestV1* items,
        int count,
        string diagnosticsScope)
    {
        var parsed = new ParsedObjectReadBatch(count);
        for (var i = 0; i < count; i++)
        {
            var item = items[i];
            try
            {
                if (item.ObjectIndex < 0)
                {
                    throw new ArgumentException("object_index cannot be negative");
                }
                var kind = ReadNativeUtf8(item.KindUtf8, item.KindUtf8Len, defaultValue: "auto");
                var imageFormat = ReadNativeUtf8(item.ImageFormatUtf8, item.ImageFormatUtf8Len, defaultValue: "raw_rgba");
                parsed.OptionIndexes.Add(i);
                parsed.Options.Add(new AssetStudioObjectReadOptions
                {
                    ObjectIndex = item.ObjectIndex,
                    Kind = kind,
                    ImageFormat = imageFormat,
                });
            }
            catch (ArgumentException ex)
            {
                Diagnostics.Exception(diagnosticsScope, ex);
                parsed.Reads.Add(NativeObjectReadItemBuildResult.Fail(
                    i,
                    0,
                    2,
                    NativeObjectReadErrorCode.InvalidRequest,
                    ex.Message));
            }
        }
        return parsed;
    }

    private static NativeObjectReadBatchBuildResult FinishObjectReadBatch(
        AssetStudioObjectReadBatchResult batch,
        ParsedObjectReadBatch parsed,
        int count,
        bool includePayloads)
    {
        var hasCapturedPayloads = includePayloads;
        var nativeReads = parsed.Reads;
        nativeReads.AddRange(batch.Reads.Select(read =>
        {
            var originalIndex = read.Index >= 0 && read.Index < parsed.OptionIndexes.Count
                ? parsed.OptionIndexes[read.Index]
                : read.Index;
            return new NativeObjectReadItemBuildResult(
                index: originalIndex,
                status: read.Status,
                errorCode: ToNativeObjectReadErrorCode(read.ErrorKind),
                pathId: read.PathId,
                typeId: read.TypeId,
                size: read.Size,
                payloadKind: read.PayloadKind,
                suggestedExtension: read.SuggestedExtension,
                errorMessage: read.ErrorMessage,
                payload: includePayloads ? read.Payload : null,
                payloadOffset: read.PayloadOffset,
                payloadLen: read.PayloadLen);
        }));
        nativeReads.Sort(static (left, right) => left.Index.CompareTo(right.Index));

        var stringDataLen = EstimateObjectReadBatchStringBytesV1(nativeReads);
        var itemsBufferLen = AlignNativeObjectTableOffset(nativeReads.Count * sizeof(NativeObjectReadItemResponseV1)) + stringDataLen;
        return new NativeObjectReadBatchBuildResult(
            nativeReads,
            batch.FailedCount + (count - parsed.Options.Count),
            itemsBufferLen,
            stringDataLen,
            batch.PayloadLen,
            hasCapturedPayloads);
    }

    private static NativeObjectReadBatchBuildResult BuildObjectReadBatch(
        ActiveNativeContext context,
        NativeObjectReadItemRequest* items,
        int count,
        bool capturePayloads)
    {
        var parsed = ParseObjectReadBatchItems(items, count, "context_read_objects_batch");
        var batch = context.Session.ReadObjectsBatch(parsed.Options, capturePayloads);
        return FinishObjectReadBatch(batch, parsed, count, includePayloads: capturePayloads);
    }

    private static NativeObjectReadBatchBuildResult BuildObjectReadBatchByIndex(
        ActiveNativeContext context,
        NativeObjectReadItemByIndexRequestV1* items,
        int count,
        bool capturePayloads)
    {
        var parsed = ParseObjectReadBatchItemsByIndex(items, count, "context_read_objects_by_index_batch");
        var batch = context.Session.ReadObjectsBatch(parsed.Options, capturePayloads);
        return FinishObjectReadBatch(batch, parsed, count, includePayloads: capturePayloads);
    }

    private static NativeObjectReadBatchBuildResult BuildObjectReadBatchInto(
        ActiveNativeContext context,
        NativeObjectReadItemRequest* items,
        int count,
        Stream payloadStream)
    {
        var parsed = ParseObjectReadBatchItems(items, count, "context_read_objects_batch_into");
        var batch = context.Session.ReadObjectsBatchInto(parsed.Options, new AssetStudioStreamPayloadWriter(payloadStream));
        return FinishObjectReadBatch(batch, parsed, count, includePayloads: false);
    }

    private static NativeObjectReadBatchBuildResult BuildObjectReadBatchByIndexInto(
        ActiveNativeContext context,
        NativeObjectReadItemByIndexRequestV1* items,
        int count,
        Stream payloadStream)
    {
        var parsed = ParseObjectReadBatchItemsByIndex(items, count, "context_read_objects_by_index_batch_into");
        var batch = context.Session.ReadObjectsBatchInto(parsed.Options, new AssetStudioStreamPayloadWriter(payloadStream));
        return FinishObjectReadBatch(batch, parsed, count, includePayloads: false);
    }

    private static bool ShouldCacheObjectReadBatch(NativeObjectReadBatchBuildResult result)
    {
        return MaxCachedObjectReadBatchPayloadBytes > 0
            && result.PayloadLen <= MaxCachedObjectReadBatchPayloadBytes;
    }

    private static int DetermineBatchStatus(
        IReadOnlyList<NativeObjectReadItemBuildResult> reads,
        int failedCount,
        int requestedCount)
    {
        if (failedCount <= 0)
        {
            return 0;
        }

        if (failedCount < requestedCount)
        {
            return 0;
        }

        var errorCode = DetermineBatchErrorCode(reads, failedCount, requestedCount);
        return errorCode switch
        {
            NativeObjectReadErrorCode.InvalidRequest => 2,
            NativeObjectReadErrorCode.ContextNotFound => 4,
            NativeObjectReadErrorCode.AssetNotFound => 6,
            NativeObjectReadErrorCode.UnsupportedKind => 7,
            NativeObjectReadErrorCode.BufferTooSmall => 8,
            _ => 100,
        };
    }

    private static NativeObjectReadErrorCode DetermineBatchErrorCode(
        IReadOnlyList<NativeObjectReadItemBuildResult> reads,
        int failedCount,
        int requestedCount)
    {
        if (failedCount <= 0)
        {
            return NativeObjectReadErrorCode.None;
        }

        if (failedCount < requestedCount)
        {
            return NativeObjectReadErrorCode.PartialFailure;
        }

        NativeObjectReadErrorCode? commonErrorCode = null;
        foreach (var read in reads)
        {
            if (read.Status == 0 && read.ErrorCode == NativeObjectReadErrorCode.None)
            {
                continue;
            }

            if (read.ErrorCode == NativeObjectReadErrorCode.None)
            {
                return NativeObjectReadErrorCode.InternalError;
            }

            if (commonErrorCode == null)
            {
                commonErrorCode = read.ErrorCode;
                continue;
            }

            if (commonErrorCode.Value != read.ErrorCode)
            {
                return NativeObjectReadErrorCode.InternalError;
            }
        }

        return commonErrorCode ?? NativeObjectReadErrorCode.InternalError;
    }

    // FNV-1a accumulator shared by the path-id and by-index signature builders.
    private ref struct NativeBatchSignatureHash
    {
        private const ulong Prime = 1099511628211UL;
        private ulong hash;
        private readonly List<byte> bytes;

        public NativeBatchSignatureHash(int capacityHint)
        {
            hash = 14695981039346656037UL;
            bytes = new List<byte>(Math.Max(4, capacityHint));
        }

        public void AddByte(byte value)
        {
            hash ^= value;
            hash *= Prime;
            bytes.Add(value);
        }

        public void AddInt32(int value)
        {
            unchecked
            {
                AddByte((byte)value);
                AddByte((byte)(value >> 8));
                AddByte((byte)(value >> 16));
                AddByte((byte)(value >> 24));
            }
        }

        public void AddInt64(long value)
        {
            unchecked
            {
                AddInt32((int)value);
                AddInt32((int)(value >> 32));
            }
        }

        public unsafe void AddBytes(byte* value, int length)
        {
            AddInt32(length);
            if (value == null || length <= 0 || length > MaxNativeUtf8ByteLength)
            {
                return;
            }

            for (var i = 0; i < length; i++)
            {
                AddByte(value[i]);
            }
        }

        public NativeObjectReadBatchSignature Build()
        {
            return new NativeObjectReadBatchSignature(unchecked((long)hash), bytes.ToArray());
        }
    }

    private static NativeObjectReadBatchSignature BuildObjectReadBatchSignature(NativeObjectReadItemRequest* items, int count)
    {
        var signature = new NativeBatchSignatureHash(count * 32);
        signature.AddInt32(count);
        if (items == null || count <= 0)
        {
            return signature.Build();
        }

        for (var i = 0; i < count; i++)
        {
            var item = items[i];
            signature.AddInt64(item.PathId);
            signature.AddBytes(item.KindUtf8, item.KindUtf8Len);
            signature.AddBytes(item.ImageFormatUtf8, item.ImageFormatUtf8Len);
        }
        return signature.Build();
    }

    private static NativeObjectReadBatchSignature BuildObjectReadBatchByIndexSignature(NativeObjectReadItemByIndexRequestV1* items, int count)
    {
        var signature = new NativeBatchSignatureHash(count * 24);
        signature.AddInt32(count);
        if (items == null || count <= 0)
        {
            return signature.Build();
        }

        for (var i = 0; i < count; i++)
        {
            var item = items[i];
            signature.AddInt32(item.ObjectIndex);
            signature.AddBytes(item.KindUtf8, item.KindUtf8Len);
            signature.AddBytes(item.ImageFormatUtf8, item.ImageFormatUtf8Len);
        }
        return signature.Build();
    }

    private static void WriteObjectReadBatchItemsV1Into(
        IReadOnlyList<NativeObjectReadItemBuildResult> reads,
        byte* buffer,
        long bufferLen,
        out NativeObjectReadItemResponseV1* items,
        out byte* stringData,
        out int stringDataLen)
    {
        items = null;
        stringData = null;
        stringDataLen = EstimateObjectReadBatchStringBytesV1(reads);
        var stringDataOffset = AlignNativeObjectTableOffset(reads.Count * sizeof(NativeObjectReadItemResponseV1));
        var requiredLen = stringDataOffset + stringDataLen;
        if (requiredLen == 0)
        {
            return;
        }
        if (buffer == null || bufferLen < requiredLen)
        {
            throw new ArgumentException("object read batch v1 items buffer is smaller than required metadata size");
        }

        new Span<byte>(buffer, checked((int)requiredLen)).Clear();
        items = (NativeObjectReadItemResponseV1*)buffer;
        stringData = buffer + stringDataOffset;
        var stringCursor = 0;
        for (var index = 0; index < reads.Count; index++)
        {
            var read = reads[index];
            ref var native = ref items[index];
            native.Index = read.Index;
            native.Status = read.Status;
            native.ErrorCode = read.ErrorCode;
            native.PathId = read.PathId;
            native.TypeId = read.TypeId;
            native.Size = read.Size;
            native.PayloadOffset = read.PayloadOffset;
            native.PayloadLen = read.PayloadLen;
            WriteNativeString(read.PayloadKind, stringData, ref stringCursor, out native.PayloadKindOffset, out native.PayloadKindLen);
            WriteNativeString(read.SuggestedExtension, stringData, ref stringCursor, out native.SuggestedExtensionOffset, out native.SuggestedExtensionLen);
            WriteNativeString(read.ErrorMessage, stringData, ref stringCursor, out native.ErrorMessageOffset, out native.ErrorMessageLen);
        }
    }

    private static int EstimateObjectReadBatchStringBytesV1(IEnumerable<NativeObjectReadItemBuildResult> reads)
    {
        long total = 0;
        foreach (var read in reads)
        {
            total += NativeStringByteCount(read.PayloadKind);
            total += NativeStringByteCount(read.SuggestedExtension);
            total += NativeStringByteCount(read.ErrorMessage);
            if (total > int.MaxValue)
            {
                throw new InvalidOperationException("object read batch v1 string data is too large to address as one native buffer");
            }
        }
        return (int)total;
    }

    private static void WriteObjectReadBatchPayloadInto(IEnumerable<NativeObjectReadItemBuildResult> reads, byte* payload, long payloadLen)
    {
        if (payloadLen <= 0)
        {
            return;
        }
        if (payload == null)
        {
            throw new ArgumentException("object read batch v1 payload buffer is null but payload is non-empty");
        }

        // Copy per read at its own offset so the total payload block is not limited to
        // a single int-addressed span (individual captured arrays are always < 2 GiB).
        foreach (var read in reads)
        {
            if (read.Payload == null || read.Payload.Length == 0)
            {
                continue;
            }
            if (read.PayloadOffset < 0 || read.PayloadOffset > payloadLen - read.Payload.Length)
            {
                throw new InvalidOperationException("object read batch v1 payload region is out of bounds");
            }
            read.Payload.CopyTo(new Span<byte>(payload + read.PayloadOffset, read.Payload.Length));
        }
    }

    private static long RegisterResultArena(long contextId, byte* itemsBuffer, byte* payload)
    {
        var handle = Interlocked.Increment(ref NextResultHandle);
        lock (ResultArenas)
        {
            ResultArenas.Add(handle, new NativeResultArena(contextId, (IntPtr)itemsBuffer, (IntPtr)payload));
        }
        return handle;
    }

    private static long EstimateObjectReadBatchPayloadCapacity(AssetStudioSession session, NativeObjectReadBatchRequest* request)
    {
        if (request->Count <= 0 || request->Items == null)
        {
            return 0;
        }

        var pathIds = new long[request->Count];
        for (var i = 0; i < request->Count; i++)
        {
            pathIds[i] = request->Items[i].PathId;
        }
        return session.EstimateObjectPayloadCapacity(pathIds);
    }

    private static string ClassifyReadError(Exception exception)
    {
        if (exception is NotSupportedException)
        {
            return NativeErrorCodes.UnsupportedKind;
        }

        var message = exception.Message;
        if (message.Contains("was not found", StringComparison.OrdinalIgnoreCase))
        {
            return NativeErrorCodes.AssetNotFound;
        }
        if (message.Contains("unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return NativeErrorCodes.UnsupportedKind;
        }
        return NativeErrorCodes.InternalError;
    }

    private static int ClassifyReadStatus(Exception exception)
    {
        return ClassifyReadError(exception) switch
        {
            NativeErrorCodes.AssetNotFound => 6,
            NativeErrorCodes.UnsupportedKind => 7,
            NativeErrorCodes.InvalidRequest => 2,
            _ => 100,
        };
    }

    private static NativeObjectReadErrorCode ToNativeObjectReadErrorCode(string errorCode)
    {
        return errorCode switch
        {
            NativeErrorCodes.InvalidRequest => NativeObjectReadErrorCode.InvalidRequest,
            NativeErrorCodes.ContextNotFound => NativeObjectReadErrorCode.ContextNotFound,
            NativeErrorCodes.AssetNotFound => NativeObjectReadErrorCode.AssetNotFound,
            NativeErrorCodes.UnsupportedKind => NativeObjectReadErrorCode.UnsupportedKind,
            _ => NativeObjectReadErrorCode.InternalError,
        };
    }

    private static NativeObjectReadErrorCode ToNativeObjectReadErrorCode(AssetStudioObjectReadErrorKind errorKind)
    {
        return errorKind switch
        {
            AssetStudioObjectReadErrorKind.None => NativeObjectReadErrorCode.None,
            AssetStudioObjectReadErrorKind.InvalidRequest => NativeObjectReadErrorCode.InvalidRequest,
            AssetStudioObjectReadErrorKind.AssetNotFound => NativeObjectReadErrorCode.AssetNotFound,
            AssetStudioObjectReadErrorKind.UnsupportedKind => NativeObjectReadErrorCode.UnsupportedKind,
            _ => NativeObjectReadErrorCode.InternalError,
        };
    }

    private static string ReadNativeUtf8(byte* value, int byteLength, string defaultValue)
    {
        if (byteLength < 0)
        {
            throw new ArgumentException("native UTF-8 string length cannot be negative");
        }
        if (byteLength == 0)
        {
            return defaultValue;
        }
        if (byteLength > MaxNativeUtf8ByteLength)
        {
            throw new ArgumentException($"native UTF-8 string length cannot exceed {MaxNativeUtf8ByteLength} bytes");
        }
        if (value == null)
        {
            throw new ArgumentException("native UTF-8 string pointer is null but length is non-zero");
        }

        return Encoding.UTF8.GetString(value, byteLength);
    }

    private static byte* WriteObjectReadStringsToNative(
        string? payloadKind,
        string? suggestedExtension,
        out byte* payloadKindPtr,
        out int payloadKindLen,
        out byte* suggestedExtensionPtr,
        out int suggestedExtensionLen,
        out long bufferLen)
    {
        payloadKindLen = NativeStringByteCount(payloadKind);
        suggestedExtensionLen = NativeStringByteCount(suggestedExtension);
        bufferLen = payloadKindLen + suggestedExtensionLen;
        payloadKindPtr = null;
        suggestedExtensionPtr = null;
        if (bufferLen == 0)
        {
            return null;
        }
        if (bufferLen > int.MaxValue)
        {
            throw new InvalidOperationException("object read string data is too large to address as one native buffer");
        }

        var buffer = (byte*)NativeMemory.Alloc((nuint)bufferLen);
        var cursor = 0;
        if (payloadKindLen > 0)
        {
            payloadKindPtr = buffer + cursor;
            Encoding.UTF8.GetBytes(payloadKind!, new Span<byte>(payloadKindPtr, payloadKindLen));
            cursor += payloadKindLen;
        }
        if (suggestedExtensionLen > 0)
        {
            suggestedExtensionPtr = buffer + cursor;
            Encoding.UTF8.GetBytes(suggestedExtension!, new Span<byte>(suggestedExtensionPtr, suggestedExtensionLen));
        }
        return buffer;
    }

    private static byte* WriteObjectReadBatchItemsToNative(
        IReadOnlyCollection<NativeObjectReadItemBuildResult> reads,
        out NativeObjectReadItemResponse* items,
        out byte* stringData,
        out int stringDataLen,
        out long bufferLen)
    {
        var itemsOffset = 0;
        var stringDataOffset = AlignNativeObjectTableOffset(reads.Count * sizeof(NativeObjectReadItemResponse));
        stringDataLen = EstimateObjectReadBatchStringBytes(reads);
        bufferLen = stringDataOffset + stringDataLen;
        items = null;
        stringData = null;
        if (bufferLen == 0)
        {
            return null;
        }
        if (bufferLen > int.MaxValue)
        {
            throw new InvalidOperationException("object read batch metadata is too large to address as one native buffer");
        }

        var buffer = (byte*)NativeMemory.AllocZeroed((nuint)bufferLen);
        try
        {
            items = (NativeObjectReadItemResponse*)(buffer + itemsOffset);
            stringData = buffer + stringDataOffset;
            var stringCursor = 0;
            var index = 0;
            foreach (var read in reads)
            {
                ref var native = ref items[index++];
                native.Index = read.Index;
                native.Status = read.Status;
                native.ErrorCode = read.ErrorCode;
                native.PathId = read.PathId;
                native.TypeId = read.TypeId;
                native.Size = read.Size;
                native.PayloadOffset = read.PayloadOffset;
                native.PayloadLen = read.PayloadLen;
                WriteNativeString(read.PayloadKind, stringData, ref stringCursor, out native.PayloadKindOffset, out native.PayloadKindLen);
                WriteNativeString(read.SuggestedExtension, stringData, ref stringCursor, out native.SuggestedExtensionOffset, out native.SuggestedExtensionLen);
            }
            return buffer;
        }
        catch
        {
            NativeMemory.Free(buffer);
            throw;
        }
    }

    private static int EstimateObjectReadBatchStringBytes(IEnumerable<NativeObjectReadItemBuildResult> reads)
    {
        long total = 0;
        foreach (var read in reads)
        {
            total += NativeStringByteCount(read.PayloadKind);
            total += NativeStringByteCount(read.SuggestedExtension);
            if (total > int.MaxValue)
            {
                throw new InvalidOperationException("object read batch string data is too large to address as one native buffer");
            }
        }
        return (int)total;
    }

    private static IReadOnlyCollection<string>? ParseNativeAssetTypes(byte* value, int byteLength)
    {
        if (byteLength < 0)
        {
            throw new ArgumentException("asset_types_csv length cannot be negative");
        }
        if (byteLength == 0)
        {
            return null;
        }
        if (byteLength > MaxNativeUtf8ByteLength)
        {
            throw new ArgumentException($"asset_types_csv length cannot exceed {MaxNativeUtf8ByteLength} bytes");
        }
        if (value == null)
        {
            throw new ArgumentException("asset_types_csv pointer is null but length is non-zero");
        }

        var text = Encoding.UTF8.GetString(value, byteLength);
        var types = text
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .ToArray();
        return types.Length == 0 ? null : types;
    }

    private static string? ParseNativeUtf8(byte* value, int byteLength, string fieldName)
    {
        if (byteLength < 0)
        {
            throw new ArgumentException($"{fieldName} length cannot be negative");
        }
        if (byteLength == 0)
        {
            return null;
        }
        if (byteLength > MaxNativeUtf8ByteLength)
        {
            throw new ArgumentException($"{fieldName} length cannot exceed {MaxNativeUtf8ByteLength} bytes");
        }
        if (value == null)
        {
            throw new ArgumentException($"{fieldName} pointer is null but length is non-zero");
        }
        return Encoding.UTF8.GetString(value, byteLength);
    }

    private static byte* WriteObjectTableToNative(
        IReadOnlyCollection<AssetStudioAssetInfo> assets,
        out int stringDataOffset,
        out int stringDataLength,
        out long bufferLength)
    {
        stringDataLength = EstimateObjectTableStringBytes(assets);
        stringDataOffset = ObjectTableStringDataOffset(assets);
        bufferLength = RequiredObjectTableBufferLength(assets, stringDataLength);
        if (bufferLength == 0)
        {
            return null;
        }
        if (bufferLength > int.MaxValue)
        {
            throw new InvalidOperationException("object table is too large to address as one native buffer");
        }

        var buffer = (byte*)NativeMemory.AllocZeroed((nuint)bufferLength);
        try
        {
            WriteObjectTableInto(assets, buffer, bufferLength, stringDataLength, out _, out _, out _, out _);
            return buffer;
        }
        catch
        {
            NativeMemory.Free(buffer);
            throw;
        }
    }

    private static void WriteObjectTableInto(
        IReadOnlyCollection<AssetStudioAssetInfo> assets,
        byte* buffer,
        long bufferLength,
        int stringDataLength,
        out NativeAssetObject* objects,
        out byte* stringData,
        out int writtenStringDataLength,
        out long requiredBufferLength)
    {
        var stringDataOffset = ObjectTableStringDataOffset(assets);
        requiredBufferLength = (long)stringDataOffset + stringDataLength;
        writtenStringDataLength = stringDataLength;
        objects = null;
        stringData = null;
        if (requiredBufferLength == 0)
        {
            return;
        }
        if (requiredBufferLength > int.MaxValue)
        {
            throw new InvalidOperationException("object table is too large to address as one native buffer");
        }
        if (buffer == null || bufferLength < requiredBufferLength)
        {
            throw new ArgumentException("object table buffer is too small");
        }

        new Span<byte>(buffer, checked((int)requiredBufferLength)).Clear();
        objects = (NativeAssetObject*)buffer;
        stringData = buffer + stringDataOffset;
        var stringCursor = 0;
        var index = 0;
        foreach (var asset in assets)
        {
            ref var native = ref objects[index++];
            native.Index = asset.Index;
            native.TypeId = asset.TypeId;
            native.PathId = asset.PathId;
            native.Size = asset.Size;
            native.EstimatedPayloadCapacity = asset.EstimatedPayloadCapacity;
            native.RawPayloadCapacity = asset.RawPayloadCapacity;
            native.ImagePayloadCapacity = asset.ImagePayloadCapacity;
            native.TextPayloadCapacity = asset.TextPayloadCapacity;
            native.PayloadCapacityFlags = asset.PayloadCapacityFlags;
            WriteNativeString(asset.Name, stringData, ref stringCursor, out native.NameOffset, out native.NameLen);
            WriteNativeString(asset.Container, stringData, ref stringCursor, out native.ContainerOffset, out native.ContainerLen);
            WriteNativeString(asset.Type, stringData, ref stringCursor, out native.TypeOffset, out native.TypeLen);
            WriteNativeString(asset.UniqueId, stringData, ref stringCursor, out native.UniqueIdOffset, out native.UniqueIdLen);
            WriteNativeString(asset.SourceFile, stringData, ref stringCursor, out native.SourceFileOffset, out native.SourceFileLen);
        }
    }

    private static int ObjectTableStringDataOffset(IReadOnlyCollection<AssetStudioAssetInfo> assets)
    {
        return AlignNativeObjectTableOffset(assets.Count * sizeof(NativeAssetObject));
    }

    private static long RequiredObjectTableBufferLength(
        IReadOnlyCollection<AssetStudioAssetInfo> assets,
        int stringDataLength)
    {
        return (long)ObjectTableStringDataOffset(assets) + stringDataLength;
    }

    private static int EstimateObjectTableStringBytes(IEnumerable<AssetStudioAssetInfo> assets)
    {
        var total = 0L;
        foreach (var asset in assets)
        {
            total += NativeStringByteCount(asset.Name);
            total += NativeStringByteCount(asset.Container);
            total += NativeStringByteCount(asset.Type);
            total += NativeStringByteCount(asset.UniqueId);
            total += NativeStringByteCount(asset.SourceFile);
            if (total > int.MaxValue)
            {
                throw new InvalidOperationException("object table string data is too large to address as one native buffer");
            }
        }
        return (int)total;
    }

    private static int NativeStringByteCount(string? value)
    {
        return string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);
    }

    private static void WriteNativeString(string? value, byte* stringData, ref int stringCursor, out int offset, out int length)
    {
        if (string.IsNullOrEmpty(value))
        {
            offset = 0;
            length = 0;
            return;
        }

        offset = stringCursor;
        length = Encoding.UTF8.GetByteCount(value);
        Encoding.UTF8.GetBytes(value, new Span<byte>(stringData + stringCursor, length));
        stringCursor += length;
    }

    private static int AlignNativeObjectTableOffset(int value)
    {
        return (value + 7) & ~7;
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_free_string", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void FreeString(byte* value)
    {
        if (value != null)
        {
            NativeMemory.Free(value);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_free_buffer", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void FreeBuffer(byte* value)
    {
        if (value != null)
        {
            NativeMemory.Free(value);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "haruki_assetstudio_result_free", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ResultFree(long resultHandle)
    {
        if (resultHandle <= 0)
        {
            return 4;
        }

        NativeResultArena? arena;
        lock (ResultArenas)
        {
            if (!ResultArenas.Remove(resultHandle, out arena))
            {
                return 4;
            }
        }

        arena.Dispose();
        return 0;
    }

    private static NativePayloadBundle WritePayloadBundleToNative(IReadOnlyCollection<(string Name, byte[] Payload)> entries)
    {
        if (entries.Count == 0)
        {
            return default;
        }

        var capacity = EstimatePayloadBundleCapacity(entries);
        if (capacity <= 0)
        {
            throw new InvalidOperationException("payload bundle is too large to address as one native buffer");
        }

        var buffer = (byte*)NativeMemory.Alloc((nuint)capacity);
        try
        {
            var span = new Span<byte>(buffer, capacity);
            var offset = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, sizeof(uint)), PayloadBundleMagic);
            offset += sizeof(uint);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, sizeof(ushort)), PayloadBundleVersion);
            offset += sizeof(ushort);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, sizeof(ushort)), PayloadBundleHeaderLength);
            offset += sizeof(ushort);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), entries.Count);
            offset += sizeof(int);
            var payloadDataBytes = SumPayloadDataBytes(entries);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, sizeof(long)), payloadDataBytes);
            offset += sizeof(long);

            foreach (var (name, payload) in entries)
            {
                var nameByteCount = Encoding.UTF8.GetByteCount(name);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), nameByteCount);
                offset += sizeof(int);
                BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, sizeof(long)), payload.Length);
                offset += sizeof(long);
                var written = Encoding.UTF8.GetBytes(name, span.Slice(offset, nameByteCount));
                offset += written;
                payload.CopyTo(span.Slice(offset, payload.Length));
                offset += payload.Length;
            }

            return new NativePayloadBundle(
                buffer,
                capacity,
                PayloadBundleVersion,
                entries.Count,
                payloadDataBytes);
        }
        catch
        {
            NativeMemory.Free(buffer);
            throw;
        }
    }

    private static NativePayloadBundle WritePayloadBundleToNative(IReadOnlyList<AssetStudioObjectReadBatchItemResult> reads, byte* sourcePayload, long sourcePayloadLen)
    {
        var entryCount = 0;
        long payloadDataBytes = 0;
        long capacity = PayloadBundleHeaderLength;
        foreach (var read in reads)
        {
            if (read.Status != 0 || read.PayloadLen <= 0)
            {
                continue;
            }

            if (read.PayloadOffset < 0 || read.PayloadLen < 0 || read.PayloadOffset > sourcePayloadLen - read.PayloadLen)
            {
                throw new InvalidOperationException($"object read payload range is outside the native source buffer for path_id {read.PathId}");
            }

            var name = read.PathId.ToString(CultureInfo.InvariantCulture);
            capacity += sizeof(int) + sizeof(long) + Encoding.UTF8.GetByteCount(name) + read.PayloadLen;
            payloadDataBytes += read.PayloadLen;
            entryCount++;
            if (capacity > int.MaxValue)
            {
                throw new InvalidOperationException("payload bundle is too large to address as one native buffer");
            }
        }

        if (entryCount == 0)
        {
            return default;
        }
        if (sourcePayload == null)
        {
            throw new InvalidOperationException("native source payload buffer is null");
        }

        var buffer = (byte*)NativeMemory.Alloc((nuint)capacity);
        try
        {
            var span = new Span<byte>(buffer, (int)capacity);
            var offset = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, sizeof(uint)), PayloadBundleMagic);
            offset += sizeof(uint);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, sizeof(ushort)), PayloadBundleVersion);
            offset += sizeof(ushort);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, sizeof(ushort)), PayloadBundleHeaderLength);
            offset += sizeof(ushort);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), entryCount);
            offset += sizeof(int);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, sizeof(long)), payloadDataBytes);
            offset += sizeof(long);

            foreach (var read in reads)
            {
                if (read.Status != 0 || read.PayloadLen <= 0)
                {
                    continue;
                }

                var name = read.PathId.ToString(CultureInfo.InvariantCulture);
                var nameByteCount = Encoding.UTF8.GetByteCount(name);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), nameByteCount);
                offset += sizeof(int);
                BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, sizeof(long)), read.PayloadLen);
                offset += sizeof(long);
                var written = Encoding.UTF8.GetBytes(name, span.Slice(offset, nameByteCount));
                offset += written;
                new ReadOnlySpan<byte>(sourcePayload + read.PayloadOffset, (int)read.PayloadLen)
                    .CopyTo(span.Slice(offset, (int)read.PayloadLen));
                offset += (int)read.PayloadLen;
            }

            return new NativePayloadBundle(buffer, capacity, PayloadBundleVersion, entryCount, payloadDataBytes);
        }
        catch
        {
            NativeMemory.Free(buffer);
            throw;
        }
    }

    private static int EstimatePayloadBundleCapacity(IReadOnlyCollection<(string Name, byte[] Payload)> entries)
    {
        long capacity = PayloadBundleHeaderLength;
        foreach (var (name, payload) in entries)
        {
            capacity += sizeof(int) + sizeof(long) + Encoding.UTF8.GetByteCount(name) + payload.Length;
            if (capacity > int.MaxValue)
            {
                return 0;
            }
        }
        return (int)capacity;
    }

    private static long SumPayloadDataBytes(IReadOnlyCollection<(string Name, byte[] Payload)> entries)
    {
        long bytes = 0;
        foreach (var (_, payload) in entries)
        {
            bytes += payload.Length;
        }
        return bytes;
    }

    private readonly struct NativePayloadBundle
    {
        public NativePayloadBundle(byte* pointer, long length, int version, int entryCount, long dataBytes)
        {
            Pointer = pointer;
            Length = length;
            Version = version;
            EntryCount = entryCount;
            DataBytes = dataBytes;
        }

        public byte* Pointer { get; }
        public long Length { get; }
        public int Version { get; }
        public int EntryCount { get; }
        public long DataBytes { get; }
    }

    private static void ResetProcessLocalState()
    {
        AssetStudioSession.ResetProcessLocalState();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static bool TryAddSession(ActiveNativeContext context)
    {
        lock (SessionsSync)
        {
            if (Sessions.Count >= MaxNativeActiveContexts)
            {
                return false;
            }

            Sessions.Add(context.ContextId, context);
            return true;
        }
    }

    private static NativeContextAcquireResult TryAcquireSession(long contextId, [NotNullWhen(true)] out ActiveNativeContext? context)
    {
        lock (SessionsSync)
        {
            if (!Sessions.TryGetValue(contextId, out context))
            {
                context = null;
                return NativeContextAcquireResult.NotFound;
            }

            return context.TryAcquire()
                ? NativeContextAcquireResult.Acquired
                : NativeContextAcquireResult.Busy;
        }
    }

    private static bool TryRemoveSession(long contextId, [NotNullWhen(true)] out ActiveNativeContext? context)
    {
        lock (SessionsSync)
        {
            if (!Sessions.TryGetValue(contextId, out context))
            {
                return false;
            }

            Sessions.Remove(contextId);
            return true;
        }
    }

    private static int ActiveSessionCount()
    {
        lock (SessionsSync)
        {
            return Sessions.Count;
        }
    }

    private static long ReadLongEnvironment(string name, long defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed)
            : defaultValue;
    }

    private static void ReleaseResultArenasForContext(long contextId)
    {
        List<NativeResultArena>? arenas = null;
        lock (ResultArenas)
        {
            foreach (var pair in ResultArenas.Where(pair => pair.Value.ContextId == contextId).ToArray())
            {
                ResultArenas.Remove(pair.Key);
                (arenas ??= new List<NativeResultArena>()).Add(pair.Value);
            }
        }

        if (arenas == null)
        {
            return;
        }

        foreach (var arena in arenas)
        {
            arena.Dispose();
        }
    }

    /// <summary>
    /// Native libraries shipped next to this dylib. Only these are routed through
    /// the custom resolver; anything else falls back to default probing.
    /// </summary>
    private static readonly string[] ShippedNativeDependencyNames =
    {
        "Texture2DDecoderNative",
        "AssetStudioFBXNative",
        "ooz",
        "fmod",
    };

    private static IntPtr ResolveAssetStudioNativeLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!ShippedNativeDependencyNames.Contains(libraryName, StringComparer.Ordinal))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in NativeDependencyCandidates(libraryName))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> NativeDependencyCandidates(string libraryName)
    {
        var fileName = NativeDependencyFileName(libraryName);
        foreach (var path in ConfiguredNativeDependencyPaths())
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
                continue;
            }

            // The env var conventionally points at this FFI library itself; sibling
            // dependencies live in the same directory.
            if (File.Exists(path))
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    yield return Path.Combine(directory, fileName);
                    continue;
                }
            }

            yield return Path.Combine(path, fileName);
        }

        foreach (var directory in DefaultNativeDependencyDirectories())
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                yield return Path.Combine(directory, fileName);
            }
        }
    }

    private static IEnumerable<string> ConfiguredNativeDependencyPaths()
    {
        var configuredPath = Environment.GetEnvironmentVariable("HARUKI_ASSET_STUDIO_NATIVE_LIBRARY_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            foreach (var path in configuredPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> DefaultNativeDependencyDirectories()
    {
        var nativeLibraryDirectory = CurrentNativeLibraryDirectory();
        if (!string.IsNullOrWhiteSpace(nativeLibraryDirectory))
        {
            yield return nativeLibraryDirectory;
            yield return Path.Combine(nativeLibraryDirectory, "runtimes", CurrentRuntimeIdentifier(), "native");
        }

        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", CurrentRuntimeIdentifier(), "native");
        yield return Environment.CurrentDirectory;
        yield return Path.Combine(Environment.CurrentDirectory, "runtimes", CurrentRuntimeIdentifier(), "native");
    }

    private static string? CurrentNativeLibraryDirectory()
    {
        var dladdrDirectory = CurrentNativeLibraryDirectoryFromDladdr();
        if (!string.IsNullOrWhiteSpace(dladdrDirectory))
        {
            return dladdrDirectory;
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                var fileName = Path.GetFileName(module.FileName);
                if (fileName.StartsWith("HarukiAssetStudioFFI", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("HarukiAssetStudioNative", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(module.FileName);
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? CurrentNativeLibraryDirectoryFromDladdr()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return null;
        }

        try
        {
            var symbol = (IntPtr)(delegate* unmanaged[Cdecl]<NativeCapabilitiesResponse*, int>)&CapabilitiesV1;
            if (dladdr(symbol, out var info) == 0 || info.FileName == IntPtr.Zero)
            {
                return null;
            }

            var fileName = Marshal.PtrToStringUTF8(info.FileName);
            return string.IsNullOrWhiteSpace(fileName) ? null : Path.GetDirectoryName(fileName);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("libSystem.dylib", EntryPoint = "dladdr")]
    private static extern int dladdr(IntPtr address, out DlInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DlInfo
    {
        public readonly IntPtr FileName;
        public readonly IntPtr BaseAddress;
        public readonly IntPtr SymbolName;
        public readonly IntPtr SymbolAddress;
    }

    private static string CurrentRuntimeIdentifier()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "osx"
                : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }

    private static string NativeDependencyFileName(string libraryName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return libraryName + ".dll";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "lib" + libraryName + ".dylib";
        }
        return "lib" + libraryName + ".so";
    }
}

public enum NativeObjectTableErrorCode
{
    None = 0,
    NullPointer = 1,
    InvalidRequest = 2,
    ContextNotFound = 4,
    ContextBusy = 5,
    BufferTooSmall = 8,
    InternalError = 100,
}

public enum NativeObjectLookupKind
{
    PathId = 1,
    Name = 2,
    Container = 3,
    Type = 4,
}

public enum NativeObjectReadErrorCode
{
    None = 0,
    NullPointer = 1,
    InvalidRequest = 2,
    ContextNotFound = 4,
    ContextBusy = 5,
    AssetNotFound = 6,
    UnsupportedKind = 7,
    BufferTooSmall = 8,
    PartialFailure = 9,
    InternalError = 100,
}

public enum NativeContextErrorCode
{
    None = 0,
    NullPointer = 1,
    InvalidRequest = 2,
    ContextNotFound = 4,
    ContextLimit = 5,
    ContextBusy = 10,
    InternalError = 100,
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectListRequest
{
    public int StructSize;
    public long ContextId;
    public int Offset;
    public int Limit;
    public byte* AssetTypesCsvUtf8;
    public int AssetTypesCsvUtf8Len;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectListIntoRequest
{
    public int StructSize;
    public long ContextId;
    public int Offset;
    public int Limit;
    public byte* AssetTypesCsvUtf8;
    public int AssetTypesCsvUtf8Len;
    public int Flags;
    public int Reserved;
    public byte* Buffer;
    public long BufferLen;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectLookupRequest
{
    public int StructSize;
    public long ContextId;
    public int LookupKind;
    public long PathId;
    public byte* QueryUtf8;
    public int QueryUtf8Len;
    public byte* AssetTypesCsvUtf8;
    public int AssetTypesCsvUtf8Len;
    public int Offset;
    public int Limit;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectLookupIntoRequest
{
    public int StructSize;
    public long ContextId;
    public int LookupKind;
    public long PathId;
    public byte* QueryUtf8;
    public int QueryUtf8Len;
    public byte* AssetTypesCsvUtf8;
    public int AssetTypesCsvUtf8Len;
    public int Offset;
    public int Limit;
    public int Flags;
    public int Reserved;
    public byte* Buffer;
    public long BufferLen;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeContextOpenRequest
{
    public int StructSize;
    public byte* InputPathUtf8;
    public int InputPathUtf8Len;
    public byte* UnityVersionUtf8;
    public int UnityVersionUtf8Len;
    public byte* AssetTypesCsvUtf8;
    public int AssetTypesCsvUtf8Len;
    public byte* OutputDirUtf8;
    public int OutputDirUtf8Len;
    public int LoadAllAssets;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeContextOpenResponse
{
    public int StructSize;
    public int AbiVersion;
    public int SchemaVersion;
    public int ContextAbiVersion;
    public int Status;
    public NativeContextErrorCode ErrorCode;
    public long ContextId;
    public int AssetsFileCount;
    public int ExportableAssetCount;
    public int ObjectIndexCount;
    public int HasMoreAssets;
    public byte* UnityVersionUtf8;
    public int UnityVersionUtf8Len;
    public byte* Buffer;
    public long BufferLen;
    public long DurationMs;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeContextCloseRequest
{
    public int StructSize;
    public long ContextId;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeContextCloseResponse
{
    public int StructSize;
    public int AbiVersion;
    public int SchemaVersion;
    public int ContextAbiVersion;
    public int Status;
    public NativeContextErrorCode ErrorCode;
    public long ContextId;
    public long DurationMs;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeLimitsResponse
{
    public int StructSize;
    public int AbiVersion;
    public int SchemaVersion;
    public int LimitsAbiVersion;
    public int Status;
    public NativeContextErrorCode ErrorCode;
    public int MaxNativeUtf8Bytes;
    public int MaxObjectReadBatchCount;
    public int MaxObjectTablePageLimit;
    public long MaxObjectReadBatchPayloadBytes;
    public long MaxCachedObjectReadBatchPayloadBytes;
    public int MaxActiveContexts;
    public int MaxConcurrentOperations;
    public int SupportsMultipleContexts;
    public int SupportsConcurrentOperations;
    public int LegacyStaticEngine;
    public int NativeConsoleCapture;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeCapabilitiesResponse
{
    public int StructSize;
    public int AbiVersion;
    public int SchemaVersion;
    public int Status;
    public NativeContextErrorCode ErrorCode;
    public int CoreApiVersionMajor;
    public int CoreApiVersionMinor;
    public int ContextAbiVersion;
    public int ObjectTableAbiVersion;
    public int ObjectTableIntoAbiVersion;
    public int ObjectLookupAbiVersion;
    public int ObjectLookupIntoAbiVersion;
    public int ObjectReadAbiVersion;
    public int ObjectReadBatchAbiVersion;
    public int ObjectReadBatchHandleAbiVersion;
    public int ObjectReadBatchIntoAbiVersion;
    public int ObjectReadBatchByIndexAbiVersion;
    public int ObjectReadBatchDirectIntoAbiVersion;
    public int ObjectReadBatchDirectRetryAbiVersion;
    public int SupportsTypedObjectTable;
    public int SupportsCallerProvidedObjectTableBuffers;
    public int SupportsTypedObjectLookup;
    public int SupportsCallerProvidedObjectLookupBuffers;
    public int SupportsTypedObjectRead;
    public int SupportsTypedObjectReadBatch;
    public int SupportsResultHandle;
    public int SupportsDirectObjectReadRetry;
    public int SupportsTypedContext;
    public int SupportsNativeDependencyResolver;
    public int SupportsAbiLayout;
    public int SupportsMultipleContexts;
    public int SupportsConcurrentOperations;
    public int SupportsContextLifetimeGuards;
    public int NativeConsoleCapture;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeAbiLayoutResponse
{
    public int StructSize;
    public int AbiVersion;
    public int SchemaVersion;
    public int Status;
    public NativeContextErrorCode ErrorCode;
    public int LayoutVersion;
    public int ContextOpenRequest;
    public int ContextOpenResponse;
    public int ContextCloseRequest;
    public int ContextCloseResponse;
    public int LimitsResponse;
    public int CapabilitiesResponse;
    public int ObjectListRequest;
    public int ObjectListIntoRequestV1;
    public int ObjectTable;
    public int AssetObject;
    public int ObjectReadItemRequest;
    public int ObjectReadBatchIntoRequestV1;
    public int ObjectReadItemResponseV1;
    public int ObjectReadBatchRetryResponseV1;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectReadRequest
{
    public long ContextId;
    public long PathId;
    public byte* KindUtf8;
    public int KindUtf8Len;
    public byte* ImageFormatUtf8;
    public int ImageFormatUtf8Len;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectReadBatchRequest
{
    public long ContextId;
    public NativeObjectReadItemRequest* Items;
    public int Count;
    public int Flags;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectReadBatchRequestV1
{
    public int StructSize;
    public long ContextId;
    public NativeObjectReadItemRequest* Items;
    public int Count;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectReadBatchIntoRequestV1
{
    public int StructSize;
    public long ContextId;
    public NativeObjectReadItemRequest* Items;
    public int Count;
    public int Flags;
    public byte* ItemsBuffer;
    public long ItemsBufferLen;
    public byte* Payload;
    public long PayloadLen;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectReadItemByIndexRequestV1
{
    public int ObjectIndex;
    public byte* KindUtf8;
    public int KindUtf8Len;
    public byte* ImageFormatUtf8;
    public int ImageFormatUtf8Len;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectReadBatchByIndexRequestV1
{
    public int StructSize;
    public long ContextId;
    public NativeObjectReadItemByIndexRequestV1* Items;
    public int Count;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectReadBatchByIndexIntoRequestV1
{
    public int StructSize;
    public long ContextId;
    public NativeObjectReadItemByIndexRequestV1* Items;
    public int Count;
    public int Flags;
    public int Reserved;
    public byte* ItemsBuffer;
    public long ItemsBufferLen;
    public byte* Payload;
    public long PayloadLen;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectReadItemRequest
{
    public long PathId;
    public byte* KindUtf8;
    public int KindUtf8Len;
    public byte* ImageFormatUtf8;
    public int ImageFormatUtf8Len;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectReadBatchResponse
{
    public int AbiVersion;
    public int SchemaVersion;
    public int ObjectReadBatchAbiVersion;
    public int Status;
    public NativeObjectReadErrorCode ErrorCode;
    public long ContextId;
    public int RequestedCount;
    public int ReturnedCount;
    public int FailedCount;
    public NativeObjectReadItemResponse* Items;
    public byte* StringData;
    public int StringDataLen;
    public byte* ItemsBuffer;
    public long ItemsBufferLen;
    public byte* Payload;
    public long PayloadLen;
    public long DurationMs;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectReadBatchResponseV1
{
    public int AbiVersion;
    public int SchemaVersion;
    public int ObjectReadBatchAbiVersion;
    public int Status;
    public NativeObjectReadErrorCode ErrorCode;
    public long ContextId;
    public int RequestedCount;
    public int ReturnedCount;
    public int FailedCount;
    public NativeObjectReadItemResponse* Items;
    public byte* StringData;
    public int StringDataLen;
    public byte* ItemsBuffer;
    public long ItemsBufferLen;
    public byte* Payload;
    public long PayloadLen;
    public long DurationMs;
    public int ObjectReadBatchHandleAbiVersion;
    public long ResultHandle;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeObjectReadItemResponse
{
    public int Index;
    public int Status;
    public NativeObjectReadErrorCode ErrorCode;
    public long PathId;
    public int TypeId;
    public long Size;
    public long PayloadOffset;
    public long PayloadLen;
    public int PayloadKindOffset;
    public int PayloadKindLen;
    public int SuggestedExtensionOffset;
    public int SuggestedExtensionLen;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeObjectReadItemResponseV1
{
    public int Index;
    public int Status;
    public NativeObjectReadErrorCode ErrorCode;
    public long PathId;
    public int TypeId;
    public long Size;
    public long PayloadOffset;
    public long PayloadLen;
    public int PayloadKindOffset;
    public int PayloadKindLen;
    public int SuggestedExtensionOffset;
    public int SuggestedExtensionLen;
    public int ErrorMessageOffset;
    public int ErrorMessageLen;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeObjectReadBatchSizeResponseV1
{
    public int StructSize;
    public int AbiVersion;
    public int SchemaVersion;
    public int ObjectReadBatchAbiVersion;
    public int ObjectReadBatchIntoAbiVersion;
    public int Status;
    public NativeObjectReadErrorCode ErrorCode;
    public long ContextId;
    public int RequestedCount;
    public int ReturnedCount;
    public int FailedCount;
    public long RequiredItemsBufferLen;
    public int RequiredStringDataLen;
    public long RequiredPayloadLen;
    public long ItemsBufferLen;
    public int StringDataLen;
    public long PayloadLen;
    public long DurationMs;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectReadBatchIntoResponseV1
{
    public int StructSize;
    public int AbiVersion;
    public int SchemaVersion;
    public int ObjectReadBatchAbiVersion;
    public int ObjectReadBatchIntoAbiVersion;
    public int Status;
    public NativeObjectReadErrorCode ErrorCode;
    public long ContextId;
    public int RequestedCount;
    public int ReturnedCount;
    public int FailedCount;
    public NativeObjectReadItemResponseV1* Items;
    public byte* StringData;
    public int StringDataLen;
    public byte* ItemsBuffer;
    public long ItemsBufferLen;
    public byte* Payload;
    public long PayloadLen;
    public long RequiredItemsBufferLen;
    public int RequiredStringDataLen;
    public long RequiredPayloadLen;
    public long DurationMs;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectReadBatchRetryResponseV1
{
    public int StructSize;
    public int AbiVersion;
    public int SchemaVersion;
    public int ObjectReadBatchAbiVersion;
    public int ObjectReadBatchIntoAbiVersion;
    public int ObjectReadBatchDirectRetryAbiVersion;
    public int Status;
    public NativeObjectReadErrorCode ErrorCode;
    public long ContextId;
    public int RequestedCount;
    public int ReturnedCount;
    public int FailedCount;
    public NativeObjectReadItemResponseV1* Items;
    public byte* StringData;
    public int StringDataLen;
    public byte* ItemsBuffer;
    public long ItemsBufferLen;
    public byte* Payload;
    public long PayloadLen;
    public long RequiredItemsBufferLen;
    public int RequiredStringDataLen;
    public long RequiredPayloadLen;
    public long DurationMs;
    public long ResultHandle;
    public int OwnershipFlags;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectReadResponse
{
    public int AbiVersion;
    public int SchemaVersion;
    public int ObjectReadAbiVersion;
    public int Status;
    public NativeObjectReadErrorCode ErrorCode;
    public long ContextId;
    public long PathId;
    public int TypeId;
    public long Size;
    public byte* PayloadKind;
    public int PayloadKindLen;
    public byte* SuggestedExtension;
    public int SuggestedExtensionLen;
    public byte* Payload;
    public long PayloadLen;
    public byte* Buffer;
    public long BufferLen;
    public long DurationMs;
}

internal sealed class NativeObjectReadItemBuildResult
{
    public NativeObjectReadItemBuildResult(
        int index,
        int status,
        NativeObjectReadErrorCode errorCode,
        long pathId,
        int typeId,
        long size,
        string? payloadKind,
        string? suggestedExtension,
        string? errorMessage,
        byte[]? payload,
        long payloadOffset,
        long? payloadLen = null)
    {
        Index = index;
        Status = status;
        ErrorCode = errorCode;
        PathId = pathId;
        TypeId = typeId;
        Size = size;
        PayloadKind = payloadKind;
        SuggestedExtension = suggestedExtension;
        ErrorMessage = errorMessage;
        Payload = payload;
        PayloadOffset = payloadOffset;
        PayloadLen = payloadLen ?? payload?.Length ?? 0;
    }

    public int Index { get; }
    public int Status { get; }
    public NativeObjectReadErrorCode ErrorCode { get; }
    public long PathId { get; }
    public int TypeId { get; }
    public long Size { get; }
    public string? PayloadKind { get; }
    public string? SuggestedExtension { get; }
    public string? ErrorMessage { get; }
    public byte[]? Payload { get; }
    public long PayloadOffset { get; }
    public long PayloadLen { get; }

    public static NativeObjectReadItemBuildResult Fail(int index, long pathId, int status, NativeObjectReadErrorCode errorCode, string? errorMessage)
    {
        return new NativeObjectReadItemBuildResult(
            index,
            status,
            errorCode,
            pathId,
            typeId: 0,
            size: 0,
            payloadKind: null,
            suggestedExtension: null,
            errorMessage: errorMessage,
            payload: null,
            payloadOffset: 0);
    }
}

internal sealed class NativeObjectReadBatchBuildResult
{
    public NativeObjectReadBatchBuildResult(
        IReadOnlyList<NativeObjectReadItemBuildResult> reads,
        int failedCount,
        long itemsBufferLen,
        int stringDataLen,
        long payloadLen,
        bool hasCapturedPayloads)
    {
        Reads = reads;
        FailedCount = failedCount;
        ItemsBufferLen = itemsBufferLen;
        StringDataLen = stringDataLen;
        PayloadLen = payloadLen;
        HasCapturedPayloads = hasCapturedPayloads;
    }

    public IReadOnlyList<NativeObjectReadItemBuildResult> Reads { get; }
    public int FailedCount { get; }
    public long ItemsBufferLen { get; }
    public int StringDataLen { get; }
    public long PayloadLen { get; }

    /// <summary>
    /// True when <see cref="NativeObjectReadItemBuildResult.Payload"/> holds the payload
    /// bytes for every successful read, so the packed payload block can be reproduced by
    /// memcpy without re-reading (and re-decoding) the objects.
    /// </summary>
    public bool HasCapturedPayloads { get; }
}

internal sealed class NativeObjectTableBuildResult
{
    public NativeObjectTableBuildResult(
        ActiveNativeContext context,
        long contextId,
        int offset,
        int limit,
        int nextOffset,
        bool hasMore,
        int totalCount,
        AssetStudioAssetInfo[] page)
    {
        Context = context;
        ContextId = contextId;
        Offset = offset;
        Limit = limit;
        NextOffset = nextOffset;
        HasMore = hasMore;
        TotalCount = totalCount;
        Page = page;
    }

    public ActiveNativeContext Context { get; }
    public long ContextId { get; }
    public int Offset { get; }
    public int Limit { get; }
    public int NextOffset { get; }
    public bool HasMore { get; }
    public int TotalCount { get; }
    public AssetStudioAssetInfo[] Page { get; }
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeObjectTable
{
    public int StructSize;
    public int AbiVersion;
    public int SchemaVersion;
    public int ObjectTableAbiVersion;
    public int Status;
    public NativeObjectTableErrorCode ErrorCode;
    public long ContextId;
    public int Offset;
    public int Limit;
    public int NextOffset;
    public int HasMore;
    public int TotalCount;
    public int ReturnedCount;
    public NativeAssetObject* Objects;
    public byte* StringData;
    public int StringDataLen;
    public byte* Buffer;
    public long BufferLen;
    public long DurationMs;
    public int Flags;
    public int Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeAssetObject
{
    public int Index;
    public int TypeId;
    public long PathId;
    public long Size;
    public long EstimatedPayloadCapacity;
    public long RawPayloadCapacity;
    public long ImagePayloadCapacity;
    public long TextPayloadCapacity;
    public int PayloadCapacityFlags;
    public int Reserved;
    public int NameOffset;
    public int NameLen;
    public int ContainerOffset;
    public int ContainerLen;
    public int TypeOffset;
    public int TypeLen;
    public int UniqueIdOffset;
    public int UniqueIdLen;
    public int SourceFileOffset;
    public int SourceFileLen;
}

internal sealed class NativeDiagnostics
{
    private readonly object sync = new();
    private readonly bool enabled;
    private readonly string? logPath;

    private NativeDiagnostics(bool enabled, string? logPath)
    {
        this.enabled = enabled;
        this.logPath = logPath;
    }

    public static NativeDiagnostics CreateFromEnvironment()
    {
        var enabled = IsEnabled(Environment.GetEnvironmentVariable("HARUKI_ASSET_STUDIO_NATIVE_TRACE"))
            || IsEnabled(Environment.GetEnvironmentVariable("HARUKI_ASSET_STUDIO_NATIVE_DIAGNOSTICS"));
        if (!enabled)
        {
            return new NativeDiagnostics(false, null);
        }

        var logDir = Environment.GetEnvironmentVariable("HARUKI_ASSET_STUDIO_NATIVE_LOG_DIR");
        if (string.IsNullOrWhiteSpace(logDir))
        {
            logDir = Path.Combine(Path.GetTempPath(), "haruki-assetstudio-native");
        }
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"native-{Environment.ProcessId}.log");
        return new NativeDiagnostics(true, logPath);
    }

    public string Begin(string operation, string? inputPath)
    {
        var operationId = $"{Environment.ProcessId}:{Environment.CurrentManagedThreadId}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        Event(operationId, "begin", $"{operation} input={inputPath}");
        return operationId;
    }

    public void End(string operationId, string operation, long durationMs, string detail)
    {
        Event(operationId, "end", $"{operation} duration_ms={durationMs} {detail}");
    }

    public void Event(string operationId, string eventName, string? detail = null)
    {
        if (!enabled || logPath == null)
        {
            return;
        }

        var line = $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} tid={Environment.CurrentManagedThreadId} op={operationId} event={eventName}";
        if (!string.IsNullOrWhiteSpace(detail))
        {
            line += $" {detail}";
        }
        Write(line);
    }

    public void Exception(string operationId, Exception exception)
    {
        Event(operationId, "exception", exception.ToString());
    }

    private void Write(string line)
    {
        lock (sync)
        {
            File.AppendAllText(logPath!, line + Environment.NewLine);
        }
    }

    private static bool IsEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("debug", StringComparison.OrdinalIgnoreCase)
            || value.Equals("trace", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class ActiveNativeContext
{
    private readonly object lifetimeSync = new();
    private readonly object pendingReadBatchSync = new();
    private PendingNativeObjectReadBatch? pendingReadBatch;
    private int activeCalls;
    private bool closing;

    public ActiveNativeContext(
        long contextId,
        string operationId,
        string inputPath,
        Stopwatch stopwatch,
        IReadOnlyCollection<string>? requestedAssetTypes,
        AssetStudioSession session)
    {
        ContextId = contextId;
        OperationId = operationId;
        InputPath = inputPath;
        Stopwatch = stopwatch;
        RequestedAssetTypes = requestedAssetTypes;
        Session = session;
    }

    public long ContextId { get; }
    public string OperationId { get; }
    public string InputPath { get; }
    public Stopwatch Stopwatch { get; }
    public IReadOnlyCollection<string>? RequestedAssetTypes { get; }
    public AssetStudioSession Session { get; }

    public bool TryAcquire()
    {
        lock (lifetimeSync)
        {
            if (closing)
            {
                return false;
            }

            activeCalls++;
        }

        return true;
    }

    private void ReleaseLifetimeOnly()
    {
        lock (lifetimeSync)
        {
            if (activeCalls > 0)
            {
                activeCalls--;
            }
        }
    }

    public void Release()
    {
        ReleaseLifetimeOnly();
    }

    public bool TryBeginClose()
    {
        lock (lifetimeSync)
        {
            if (closing || activeCalls != 0)
            {
                return false;
            }

            closing = true;
            return true;
        }
    }

    public void CancelClose()
    {
        lock (lifetimeSync)
        {
            if (closing)
            {
                closing = false;
            }
        }
    }

    public void SetPendingReadBatch(NativeObjectReadBatchSignature signature, NativeObjectReadBatchBuildResult result)
    {
        lock (pendingReadBatchSync)
        {
            pendingReadBatch = new PendingNativeObjectReadBatch(signature, result);
        }
    }

    public bool TryGetPendingReadBatch(NativeObjectReadBatchSignature signature, [NotNullWhen(true)] out NativeObjectReadBatchBuildResult? result)
    {
        lock (pendingReadBatchSync)
        {
            if (pendingReadBatch != null && pendingReadBatch.Signature.Equals(signature))
            {
                result = pendingReadBatch.Result;
                return true;
            }
        }

        result = null;
        return false;
    }

    public void ClearPendingReadBatch(NativeObjectReadBatchSignature signature)
    {
        lock (pendingReadBatchSync)
        {
            if (pendingReadBatch != null && pendingReadBatch.Signature.Equals(signature))
            {
                pendingReadBatch = null;
            }
        }
    }

    public void ClearPendingReadBatch()
    {
        lock (pendingReadBatchSync)
        {
            pendingReadBatch = null;
        }
    }
}

internal enum NativeContextAcquireResult
{
    Acquired,
    NotFound,
    Busy,
}

internal sealed class PendingNativeObjectReadBatch
{
    public PendingNativeObjectReadBatch(NativeObjectReadBatchSignature signature, NativeObjectReadBatchBuildResult result)
    {
        Signature = signature;
        Result = result;
    }

    public NativeObjectReadBatchSignature Signature { get; }
    public NativeObjectReadBatchBuildResult Result { get; }
}

internal readonly struct NativeObjectReadBatchSignature : IEquatable<NativeObjectReadBatchSignature>
{
    private readonly byte[] bytes;

    public NativeObjectReadBatchSignature(long hash, byte[] bytes)
    {
        Hash = hash;
        this.bytes = bytes;
    }

    public long Hash { get; }

    public bool Equals(NativeObjectReadBatchSignature other)
    {
        return Hash == other.Hash && bytes.AsSpan().SequenceEqual(other.bytes);
    }

    public override bool Equals(object? obj)
    {
        return obj is NativeObjectReadBatchSignature other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Hash.GetHashCode();
    }
}

internal sealed unsafe class NativeResultArena : IDisposable
{
    private readonly IntPtr itemsBuffer;
    private readonly IntPtr payload;

    public NativeResultArena(long contextId, IntPtr itemsBuffer, IntPtr payload)
    {
        ContextId = contextId;
        this.itemsBuffer = itemsBuffer;
        this.payload = payload;
    }

    public long ContextId { get; }

    public void Dispose()
    {
        if (itemsBuffer != IntPtr.Zero)
        {
            NativeMemory.Free((void*)itemsBuffer);
        }
        if (payload != IntPtr.Zero)
        {
            NativeMemory.Free((void*)payload);
        }
    }
}

/// <summary>
/// Streams payload bytes straight into the caller-provided buffer while they fit.
/// On overflow it either spills to an owned native buffer (retry paths: the data is
/// still needed) or degrades to counting only (direct into paths: the call will
/// return BUFFER_TOO_SMALL, only the required sizes matter). <see cref="Length"/>
/// always reflects the full logical payload size, so per-item measurements taken
/// from stream position deltas stay correct after an overflow.
/// </summary>
internal sealed unsafe class NativeOptimisticPayloadStream : Stream
{
    private enum WriteMode
    {
        CallerBuffer,
        NativeSpill,
        CountOnly,
    }

    private readonly byte* callerBuffer;
    private readonly long callerCapacity;
    private readonly bool spillToNativeOnOverflow;
    private byte* ownedPointer;
    private long ownedCapacity;
    private long length;
    private WriteMode mode = WriteMode.CallerBuffer;

    public NativeOptimisticPayloadStream(byte* callerBuffer, long callerCapacity, bool spillToNativeOnOverflow)
    {
        this.callerBuffer = callerBuffer;
        this.callerCapacity = callerBuffer == null ? 0 : Math.Max(0, callerCapacity);
        this.spillToNativeOnOverflow = spillToNativeOnOverflow;
    }

    /// <summary>True when the payload did not fit in the caller buffer.</summary>
    public bool Overflowed => mode != WriteMode.CallerBuffer;

    /// <summary>True when the payload lives in an owned native buffer (retry spill).</summary>
    public bool UsedNativeBuffer => mode == WriteMode.NativeSpill;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => length;
    public override long Position
    {
        get => length;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Transfers ownership of the spilled native buffer to the caller, first shrinking
    /// the doubling-grown allocation back to the exact payload length so the result
    /// arena does not retain over-allocated capacity.
    /// </summary>
    public byte* Detach()
    {
        if (ownedPointer != null && length > 0 && length < ownedCapacity)
        {
            var trimmed = (byte*)NativeMemory.Realloc(ownedPointer, (nuint)length);
            if (trimmed != null)
            {
                ownedPointer = trimmed;
                ownedCapacity = length;
            }
        }
        var detached = ownedPointer;
        ownedPointer = null;
        ownedCapacity = 0;
        return detached;
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] source, int offset, int count)
    {
        Write(source.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> source)
    {
        if (source.Length == 0)
        {
            return;
        }

        switch (mode)
        {
            case WriteMode.CallerBuffer when callerBuffer != null && length + source.Length <= callerCapacity:
                source.CopyTo(new Span<byte>(callerBuffer + length, source.Length));
                length += source.Length;
                return;
            case WriteMode.CallerBuffer when spillToNativeOnOverflow:
                // First write that no longer fits: move what the caller buffer already
                // holds into an owned native buffer and continue there.
                EnsureOwnedCapacity(length + source.Length);
                if (length > 0)
                {
                    Buffer.MemoryCopy(callerBuffer, ownedPointer, ownedCapacity, length);
                }
                mode = WriteMode.NativeSpill;
                source.CopyTo(new Span<byte>(ownedPointer + length, source.Length));
                length += source.Length;
                return;
            case WriteMode.CallerBuffer:
                mode = WriteMode.CountOnly;
                length += source.Length;
                return;
            case WriteMode.NativeSpill:
                EnsureOwnedCapacity(length + source.Length);
                source.CopyTo(new Span<byte>(ownedPointer + length, source.Length));
                length += source.Length;
                return;
            default:
                length += source.Length;
                return;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (ownedPointer != null)
        {
            NativeMemory.Free(ownedPointer);
            ownedPointer = null;
            ownedCapacity = 0;
        }
        base.Dispose(disposing);
    }

    private void EnsureOwnedCapacity(long required)
    {
        if (required <= ownedCapacity)
        {
            return;
        }

        var next = ownedCapacity <= 0 ? Math.Max(64 * 1024L, callerCapacity) : ownedCapacity;
        while (next < required)
        {
            if (next > long.MaxValue / 2)
            {
                next = required;
                break;
            }
            next *= 2;
        }

        if ((ulong)next > nuint.MaxValue)
        {
            throw new InvalidOperationException("object read batch payload is too large to allocate as one native buffer");
        }

        var nextPointer = ownedPointer == null
            ? (byte*)NativeMemory.Alloc((nuint)next)
            : (byte*)NativeMemory.Realloc(ownedPointer, (nuint)next);
        if (nextPointer == null)
        {
            throw new OutOfMemoryException($"failed to allocate {next} bytes for object read batch payload");
        }

        ownedPointer = nextPointer;
        ownedCapacity = next;
    }
}

internal sealed unsafe class NativePayloadAppendStream : Stream
{
    private byte* pointer;
    private long capacity;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    private long length;
    public override long Length => length;
    public override long Position
    {
        get => Length;
        set => throw new NotSupportedException();
    }

    public void Reserve(long capacity)
    {
        if (capacity > 0)
        {
            EnsureCapacity(capacity);
        }
    }

    public byte* Detach()
    {
        var detached = pointer;
        pointer = null;
        capacity = 0;
        return detached;
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] source, int offset, int count)
    {
        Write(source.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> source)
    {
        if (source.Length == 0)
        {
            return;
        }

        EnsureCapacity(Length + source.Length);
        source.CopyTo(new Span<byte>(pointer + Length, source.Length));
            length += source.Length;
    }

    protected override void Dispose(bool disposing)
    {
        if (pointer != null)
        {
            NativeMemory.Free(pointer);
            pointer = null;
            capacity = 0;
            length = 0;
        }
        base.Dispose(disposing);
    }

    private void EnsureCapacity(long required)
    {
        if (required <= capacity)
        {
            return;
        }

        var next = capacity <= 0 ? 64 * 1024L : capacity;
        while (next < required)
        {
            if (next > long.MaxValue / 2)
            {
                next = required;
                break;
            }
            next *= 2;
        }

        if ((ulong)next > nuint.MaxValue)
        {
            throw new InvalidOperationException("object read batch payload is too large to allocate as one native buffer");
        }

        var nextPointer = pointer == null
            ? (byte*)NativeMemory.Alloc((nuint)next)
            : (byte*)NativeMemory.Realloc(pointer, (nuint)next);
        if (nextPointer == null)
        {
            throw new OutOfMemoryException($"failed to allocate {next} bytes for object read batch payload");
        }

        pointer = nextPointer;
        capacity = next;
    }
}

internal static class NativeErrorCodes
{
    public const string NullPointer = "null_pointer";
    public const string InvalidRequest = "invalid_request";
    public const string ContextNotFound = "context_not_found";
    public const string ContextLimit = "context_limit";
    public const string ContextBusy = "context_busy";
    public const string AssetNotFound = "asset_not_found";
    public const string UnsupportedKind = "unsupported_kind";
    public const string InternalError = "internal_error";
}
