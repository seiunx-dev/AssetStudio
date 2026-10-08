#nullable enable

using AssetStudio;
using AssetStudioCore.Runtime;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AssetStudioCore
{
    public sealed class AssetStudioSession : IDisposable
    {
        private static readonly byte[] PayloadBundleMagic = Encoding.ASCII.GetBytes("HARUKI_ASSET_PAYLOAD_BUNDLE_V1");
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly Dictionary<long, AssetItem> pathIdIndex = new();
        private readonly Dictionary<long, int> pathIdPositionIndex = new();
        private readonly Dictionary<AssetItem, int> objectPositionIndex = new();
        private readonly List<AssetItem> objectList = new();
        private readonly IAssetStudioEngineContext engine;
        private Dictionary<string, List<AssetItem>>? nameIndex;
        private Dictionary<string, List<AssetItem>>? containerIndex;
        private Dictionary<string, List<AssetItem>>? typeIndex;
        private Dictionary<string, List<AssetItem>>? assetTypeFilterIndex;
        private bool disposed;

        private AssetStudioSession(IAssetStudioEngineContext engine, bool loaded, AssetStudioInspectResult inspectResult)
        {
            this.engine = engine;
            Loaded = loaded;
            InspectResult = inspectResult;
        }

        public bool Loaded { get; }

        public AssetStudioInspectResult InspectResult { get; private set; }

        public int ObjectIndexCount => pathIdIndex.Count;

        public AssetStudioAssetInfo[] ListObjects(AssetStudioObjectListOptions options)
        {
            ThrowIfDisposed();
            var safeOffset = Math.Max(0, options.Offset);
            var filtered = FilterObjectList(options.AssetTypes);
            var safeLimit = options.Limit <= 0 ? filtered.Count : options.Limit;
            return filtered
                .Skip(safeOffset)
                .Take(safeLimit)
                .Select(asset => ToAssetInfo(asset))
                .ToArray();
        }

        public int CountObjects(IReadOnlyCollection<string>? assetTypes = null)
        {
            ThrowIfDisposed();
            return FilterObjectList(assetTypes).Count;
        }

        public AssetStudioObjectLookupResult LookupObjects(AssetStudioObjectLookupOptions options)
        {
            ThrowIfDisposed();
            var safeOffset = Math.Max(0, options.Offset);
            var matched = LookupObjectList(options);
            var safeLimit = options.Limit <= 0 ? matched.Count : options.Limit;
            var assets = matched
                .Skip(safeOffset)
                .Take(safeLimit)
                .Select(asset => ToAssetInfo(asset))
                .ToArray();

            return new AssetStudioObjectLookupResult
            {
                Offset = safeOffset,
                Limit = safeLimit,
                TotalCount = matched.Count,
                Assets = assets,
            };
        }

        public long EstimateObjectPayloadCapacity(IEnumerable<long> pathIds)
        {
            ThrowIfDisposed();
            long total = 0;
            foreach (var pathId in pathIds)
            {
                var item = FindObject(pathId);
                if (item == null || item.FullSize <= 0)
                {
                    continue;
                }
                if (long.MaxValue - total < item.FullSize)
                {
                    return long.MaxValue;
                }
                total += item.FullSize;
            }
            return total;
        }

        public AssetStudioObjectReadBatchResult ReadObjectsBatch(
            IReadOnlyList<AssetStudioObjectReadOptions> options,
            bool capturePayloads)
        {
            ThrowIfDisposed();
            var reads = new List<AssetStudioObjectReadBatchItemResult>(options.Count);
            var failedCount = 0;
            long payloadOffset = 0;
            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                try
                {
                    var item = FindObject(option);
                    if (item == null)
                    {
                        throw new InvalidOperationException(option.ObjectIndex >= 0
                            ? $"asset index {option.ObjectIndex} was not found in the active context"
                            : $"asset path_id {option.PathId} was not found in the active context");
                    }

                    var payload = ReadObjectPayload(item, option);
                    reads.Add(new AssetStudioObjectReadBatchItemResult
                    {
                        Index = i,
                        Status = 0,
                        ErrorKind = AssetStudioObjectReadErrorKind.None,
                        Asset = ToAssetInfo(item),
                        PathId = item.m_PathID,
                        TypeId = (int)item.Type,
                        Size = item.FullSize,
                        PayloadKind = payload.PayloadKind,
                        SuggestedExtension = payload.SuggestedExtension,
                        Payload = capturePayloads ? payload.Payload : null,
                        PayloadOffset = payloadOffset,
                        PayloadLen = payload.Payload.Length,
                    });
                    payloadOffset = checked(payloadOffset + payload.Payload.Length);
                }
                catch (Exception ex)
                {
                    failedCount++;
                    var errorKind = ClassifyReadError(ex);
                    reads.Add(new AssetStudioObjectReadBatchItemResult
                    {
                        Index = i,
                        Status = (int)errorKind,
                        ErrorKind = errorKind,
                        PathId = option.PathId,
                        ErrorMessage = ex.Message,
                    });
                }
            }

            return new AssetStudioObjectReadBatchResult
            {
                Reads = reads,
                FailedCount = failedCount,
                PayloadLen = payloadOffset,
            };
        }

        public AssetStudioObjectReadBatchResult ReadObjectsBatchInto(
            IReadOnlyList<AssetStudioObjectReadOptions> options,
            IAssetStudioPayloadWriter writer)
        {
            ThrowIfDisposed();
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            // Item offsets and lengths are derived from ACTUAL stream positions, not
            // from the per-kind writers' recorded lengths. A writer that leaves extra
            // bytes on the stream (e.g. a typetree serializer that flushes partial JSON
            // before its raw-bytes fallback) is reported as a failed item instead of
            // silently shifting every following payload region.
            var payloadStream = writer.PayloadStream;
            var batchStart = payloadStream.Position;
            var reads = new List<AssetStudioObjectReadBatchItemResult>(options.Count);
            var failedCount = 0;
            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                var itemStart = payloadStream.Position;
                try
                {
                    var item = FindObject(option);
                    if (item == null)
                    {
                        throw new InvalidOperationException(option.ObjectIndex >= 0
                            ? $"asset index {option.ObjectIndex} was not found in the active context"
                            : $"asset path_id {option.PathId} was not found in the active context");
                    }

                    var payload = ReadObjectPayloadInto(item, option, payloadStream);
                    var actualLen = payloadStream.Position - itemStart;
                    if (actualLen != payload.PayloadLen)
                    {
                        throw new InvalidOperationException(
                            $"payload writer for path_id {item.m_PathID} wrote {actualLen} bytes but recorded {payload.PayloadLen}");
                    }
                    reads.Add(new AssetStudioObjectReadBatchItemResult
                    {
                        Index = i,
                        Status = 0,
                        ErrorKind = AssetStudioObjectReadErrorKind.None,
                        Asset = ToAssetInfo(item),
                        PathId = item.m_PathID,
                        TypeId = (int)item.Type,
                        Size = item.FullSize,
                        PayloadKind = payload.PayloadKind,
                        SuggestedExtension = payload.SuggestedExtension,
                        Payload = null,
                        PayloadOffset = itemStart - batchStart,
                        PayloadLen = payload.PayloadLen,
                        StreamingTier = payload.StreamingTier,
                    });
                }
                catch (Exception ex)
                {
                    failedCount++;
                    var errorKind = ClassifyReadError(ex);
                    reads.Add(new AssetStudioObjectReadBatchItemResult
                    {
                        Index = i,
                        Status = (int)errorKind,
                        ErrorKind = errorKind,
                        PathId = option.PathId,
                        ErrorMessage = ex.Message,
                        // Attribute any bytes the failed writer left behind to this item
                        // so the following items' regions stay aligned.
                        PayloadOffset = itemStart - batchStart,
                        PayloadLen = payloadStream.Position - itemStart,
                    });
                }
            }

            return new AssetStudioObjectReadBatchResult
            {
                Reads = reads,
                FailedCount = failedCount,
                PayloadLen = payloadStream.Position - batchStart,
            };
        }

        public static AssetStudioSession Open(AssetStudioInspectOptions options)
        {
            var phases = new Dictionary<string, long>();
            var engine = AssetStudioEngine.Open(options, phases);

            try
            {
                if (!engine.Loaded)
                {
                    return new AssetStudioSession(
                        engine,
                        loaded: false,
                        new AssetStudioInspectResult
                        {
                            AssetsFileCount = 0,
                            ExportableAssetCount = 0,
                            Assets = Array.Empty<AssetStudioAssetInfo>(),
                            PhaseMs = phases,
                        });
                }

                var session = new AssetStudioSession(engine, loaded: true, new AssetStudioInspectResult());
                Measure(phases, "build_object_index", session.BuildObjectIndex);
                session.InspectResult = session.CreateInspectResult(phases, options.IncludeAssets);
                return session;
            }
            catch
            {
                engine.Dispose();
                throw;
            }
        }

        public static void ResetProcessLocalState()
        {
            AssetStudioEngine.ResetProcessLocalState();
        }

        public AssetStudioObjectReadResult ReadObject(AssetStudioObjectReadOptions options)
        {
            ThrowIfDisposed();
            var phases = new Dictionary<string, long>();
            var item = Measure(phases, "find_object", () => FindObject(options));
            if (item == null)
            {
                throw new InvalidOperationException(options.ObjectIndex >= 0
                    ? $"asset index {options.ObjectIndex} was not found in the active context"
                    : $"asset path_id {options.PathId} was not found in the active context");
            }

            var payloadStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var payload = ReadObjectPayload(item, options);
            var payloadElapsedMs = payloadStopwatch.ElapsedMilliseconds;
            phases["read_payload"] = payloadElapsedMs;
            phases["read_payload.asset_type." + PhaseName(item.TypeString)] = payloadElapsedMs;
            phases["read_payload.payload_kind." + PhaseName(payload.PayloadKind)] = payloadElapsedMs;
            foreach (var phase in payload.PhaseMs)
            {
                AddPhase(phases, "read_payload.detail." + phase.Key, phase.Value);
            }

            return new AssetStudioObjectReadResult
            {
                Asset = ToAssetInfo(item),
                Payload = payload.Payload,
                PayloadKind = payload.PayloadKind,
                SuggestedExtension = payload.SuggestedExtension,
                PhaseMs = phases,
            };
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pathIdIndex.Clear();
            pathIdPositionIndex.Clear();
            objectPositionIndex.Clear();
            objectList.Clear();
            ClearLookupIndexes();
            engine.Dispose();
        }

        private void BuildObjectIndex()
        {
            objectList.Clear();
            pathIdIndex.Clear();
            pathIdPositionIndex.Clear();
            objectPositionIndex.Clear();
            ClearLookupIndexes();

            var nextSyntheticPathId = -1L;
            foreach (var asset in engine.ParsedAssets)
            {
                AddObject(asset);
                if (asset.Asset is Texture2DArray textureArray)
                {
                    var textures = textureArray.TextureList.Count > 0
                        ? textureArray.TextureList
                        : Enumerable.Range(0, Math.Max(textureArray.m_Depth, 0))
                            .Select(layer => new Texture2D(textureArray, layer))
                            .ToList();
                    foreach (var texture in textures)
                    {
                        AddObject(new AssetItem(texture)
                        {
                            Text = texture.m_Name,
                            Container = asset.Container,
                            m_PathID = nextSyntheticPathId--,
                        });
                    }
                }
            }
        }

        private void AddObject(AssetItem asset)
        {
            var index = objectList.Count;
            objectList.Add(asset);
            objectPositionIndex[asset] = index;
            if (pathIdIndex.ContainsKey(asset.m_PathID))
            {
                return;
            }

            pathIdIndex.Add(asset.m_PathID, asset);
            pathIdPositionIndex.Add(asset.m_PathID, index);
        }

        private IReadOnlyList<AssetItem> FilterObjectList(IReadOnlyCollection<string>? requestedTypes)
        {
            if (requestedTypes == null || requestedTypes.Count == 0)
            {
                return objectList;
            }

            var normalizedTypes = requestedTypes
                .Select(NormalizeAssetTypeName)
                .Where(type => type.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            if (normalizedTypes.Count == 0 || normalizedTypes.Contains("all") || normalizedTypes.Contains("*"))
            {
                return objectList;
            }

            return GetObjectsByNormalizedTypes(normalizedTypes);
        }

        private IReadOnlyList<AssetItem> GetObjectsByNormalizedTypes(HashSet<string> normalizedTypes)
        {
            assetTypeFilterIndex ??= BuildAssetTypeFilterIndex();
            var selected = new HashSet<AssetItem>();
            foreach (var normalizedType in normalizedTypes)
            {
                foreach (var bucketName in AssetTypeFilterBuckets(normalizedType))
                {
                    if (!assetTypeFilterIndex.TryGetValue(bucketName, out var bucket))
                    {
                        continue;
                    }

                    foreach (var asset in bucket)
                    {
                        selected.Add(asset);
                    }
                }
            }

            if (selected.Count == 0)
            {
                return Array.Empty<AssetItem>();
            }

            return selected
                .OrderBy(asset => objectPositionIndex.TryGetValue(asset, out var index) ? index : int.MaxValue)
                .ToArray();
        }

        private Dictionary<string, List<AssetItem>> BuildAssetTypeFilterIndex()
        {
            var index = new Dictionary<string, List<AssetItem>>(StringComparer.Ordinal);
            foreach (var asset in objectList)
            {
                var normalizedType = NormalizeAssetTypeName(asset.TypeString);
                if (normalizedType.Length == 0)
                {
                    continue;
                }

                if (!index.TryGetValue(normalizedType, out var assets))
                {
                    assets = new List<AssetItem>();
                    index.Add(normalizedType, assets);
                }
                assets.Add(asset);
            }
            return index;
        }

        private static IEnumerable<string> AssetTypeFilterBuckets(string normalizedType)
        {
            return normalizedType switch
            {
                "texture2darray" => new[] { "texture2darrayimage" },
                _ => new[] { normalizedType },
            };
        }

        private IReadOnlyList<AssetItem> LookupObjectList(AssetStudioObjectLookupOptions options)
        {
            if (options.LookupKind == AssetStudioObjectLookupKind.PathId)
            {
                var asset = FindObject(options.PathId);
                return asset != null && AssetMatchesRequestedTypes(asset, options.AssetTypes)
                    ? new[] { asset }
                    : Array.Empty<AssetItem>();
            }

            if (string.IsNullOrEmpty(options.Query))
            {
                return Array.Empty<AssetItem>();
            }

            if (!options.Contains)
            {
                return LookupIndexedObjects(options);
            }

            var candidates = FilterObjectList(options.AssetTypes);
            return candidates
                .Where(asset => LookupMatchesAsset(asset, options.LookupKind, options.Query, options.Contains))
                .ToArray();
        }

        private IReadOnlyList<AssetItem> LookupIndexedObjects(AssetStudioObjectLookupOptions options)
        {
            var index = options.LookupKind switch
            {
                AssetStudioObjectLookupKind.Name => nameIndex ??= BuildStringLookupIndex(asset => asset.Text),
                AssetStudioObjectLookupKind.Container => containerIndex ??= BuildStringLookupIndex(asset => asset.Container),
                AssetStudioObjectLookupKind.Type => typeIndex ??= BuildStringLookupIndex(asset => asset.TypeString),
                _ => null,
            };
            if (index == null || string.IsNullOrEmpty(options.Query))
            {
                return Array.Empty<AssetItem>();
            }

            if (!index.TryGetValue(options.Query, out var matches) || matches.Count == 0)
            {
                return Array.Empty<AssetItem>();
            }

            if (options.AssetTypes == null || options.AssetTypes.Count == 0)
            {
                return matches;
            }

            return matches
                .Where(asset => AssetMatchesRequestedTypes(asset, options.AssetTypes))
                .ToArray();
        }

        private Dictionary<string, List<AssetItem>> BuildStringLookupIndex(Func<AssetItem, string?> selector)
        {
            var index = new Dictionary<string, List<AssetItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in objectList)
            {
                var key = selector(asset);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (!index.TryGetValue(key, out var assets))
                {
                    assets = new List<AssetItem>();
                    index.Add(key, assets);
                }
                assets.Add(asset);
            }
            return index;
        }

        private void ClearLookupIndexes()
        {
            nameIndex = null;
            containerIndex = null;
            typeIndex = null;
            assetTypeFilterIndex = null;
        }

        private bool AssetMatchesRequestedTypes(AssetItem asset, IReadOnlyCollection<string>? requestedTypes)
        {
            if (requestedTypes == null || requestedTypes.Count == 0)
            {
                return true;
            }

            var normalizedTypes = requestedTypes
                .Select(NormalizeAssetTypeName)
                .Where(type => type.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            if (normalizedTypes.Count == 0 || normalizedTypes.Contains("all") || normalizedTypes.Contains("*"))
            {
                return true;
            }

            return RequestedTypesMatchAsset(normalizedTypes, asset.TypeString);
        }

        private static bool LookupMatchesAsset(
            AssetItem asset,
            AssetStudioObjectLookupKind lookupKind,
            string query,
            bool contains)
        {
            var value = lookupKind switch
            {
                AssetStudioObjectLookupKind.Name => asset.Text,
                AssetStudioObjectLookupKind.Container => asset.Container,
                AssetStudioObjectLookupKind.Type => asset.TypeString,
                _ => null,
            };
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return contains
                ? value.Contains(query, StringComparison.OrdinalIgnoreCase)
                : string.Equals(value, query, StringComparison.OrdinalIgnoreCase);
        }

        private static bool RequestedTypesMatchAsset(HashSet<string> normalizedTypes, string assetType)
        {
            var normalizedAssetType = NormalizeAssetTypeName(assetType);
            if (normalizedTypes.Contains(normalizedAssetType))
            {
                return normalizedAssetType != "texture2darray";
            }

            return normalizedAssetType switch
            {
                "texture2darrayimage" => normalizedTypes.Contains("texture2darray"),
                _ => false,
            };
        }

        private static string NormalizeAssetTypeName(string type)
        {
            return type.Trim().Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() switch
            {
                "tex2d" => "texture2d",
                "tex2darray" => "texture2darray",
                "texture2darrayimage" => "texture2darrayimage",
                "monobehavior" => "monobehaviour",
                "monobehaviour" => "monobehaviour",
                "textasset" => "textasset",
                "audio" => "audioclip",
                "audioclip" => "audioclip",
                "video" => "videoclip",
                "videoclip" => "videoclip",
                "image" => "texture2d",
                var normalized => normalized,
            };
        }

        private AssetStudioInspectResult CreateInspectResult(Dictionary<string, long> phases, bool includeAssets)
        {
            var assets = includeAssets
                ? objectList
                    .Select((asset, index) => ToAssetInfo(asset, index))
                    .ToArray()
                : Array.Empty<AssetStudioAssetInfo>();

            return new AssetStudioInspectResult
            {
                AssetsFileCount = engine.AssetsFileCount,
                ExportableAssetCount = objectList.Count,
                UnityVersion = engine.UnityVersion,
                Assets = assets,
                PhaseMs = phases,
            };
        }

        private AssetItem? FindObject(long pathId)
        {
            return pathIdIndex.TryGetValue(pathId, out var indexed)
                ? indexed
                : objectList.FirstOrDefault(asset => asset.m_PathID == pathId);
        }

        private AssetItem? FindObject(AssetStudioObjectReadOptions options)
        {
            if (options.ObjectIndex >= 0)
            {
                return options.ObjectIndex < objectList.Count ? objectList[options.ObjectIndex] : null;
            }

            return FindObject(options.PathId);
        }

        private int ObjectIndexOf(AssetItem asset)
        {
            return pathIdPositionIndex.TryGetValue(asset.m_PathID, out var index)
                ? index
                : objectList.IndexOf(asset);
        }

        private AssetStudioAssetInfo ToAssetInfo(AssetItem asset, int? index = null)
        {
            var payloadCapacity = EstimatePayloadCapacity(asset);
            return new AssetStudioAssetInfo
            {
                Index = index ?? ObjectIndexOf(asset),
                Name = asset.Text,
                Container = asset.Container,
                Type = asset.TypeString,
                TypeId = (int)asset.Type,
                PathId = asset.m_PathID,
                UniqueId = asset.UniqueID,
                Size = asset.FullSize,
                EstimatedPayloadCapacity = payloadCapacity.Auto,
                RawPayloadCapacity = payloadCapacity.Raw,
                ImagePayloadCapacity = payloadCapacity.Image,
                TextPayloadCapacity = payloadCapacity.Text,
                PayloadCapacityFlags = payloadCapacity.Flags,
                SourceFile = asset.SourceFile?.originalPath ?? asset.SourceFile?.fullName,
            };
        }

        private static AssetStudioPayloadCapacity EstimatePayloadCapacity(AssetItem item)
        {
            const int Estimated = 1;
            const int ExactForAuto = 2;
            const int HasRaw = 4;
            const int HasImage = 8;
            const int HasText = 16;

            static long Clamp(long value) => ClampToInt32Range(value);
            static AssetStudioPayloadCapacity Capacity(long auto, int flags, long raw = 0, long image = 0, long text = 0)
            {
                if (raw > 0)
                {
                    flags |= HasRaw;
                }
                if (image > 0)
                {
                    flags |= HasImage;
                }
                if (text > 0)
                {
                    flags |= HasText;
                }
                return new AssetStudioPayloadCapacity(auto, raw, image, text, flags);
            }

            return item.Asset switch
            {
                TextAsset text => Capacity(
                    Clamp(text.m_Script?.LongLength ?? item.FullSize),
                    ExactForAuto,
                    raw: Clamp(text.m_Script?.LongLength ?? item.FullSize),
                    text: Clamp(text.m_Script?.LongLength ?? item.FullSize)),
                Texture2D texture => Capacity(
                    EstimateTextureImageCapacity(texture, item.FullSize),
                    Estimated,
                    raw: Clamp(item.FullSize),
                    image: EstimateTextureImageCapacity(texture, item.FullSize)),
                Texture2DArray textureArray => Capacity(
                    EstimateTextureArrayImageCapacity(textureArray, item.FullSize),
                    Estimated,
                    raw: Clamp(item.FullSize),
                    image: EstimateTextureArrayImageCapacity(textureArray, item.FullSize)),
                Sprite sprite => Capacity(
                    EstimateSpriteImageCapacity(sprite, item.FullSize),
                    Estimated,
                    raw: Clamp(item.FullSize),
                    image: EstimateSpriteImageCapacity(sprite, item.FullSize)),
                AudioClip audio => Capacity(
                    Clamp(Math.Max(item.FullSize, audio.m_Size)),
                    Estimated,
                    raw: Clamp(Math.Max(item.FullSize, audio.m_Size))),
                VideoClip video => Capacity(
                    Clamp(Math.Max(item.FullSize, video.m_ExternalResources.m_Size)),
                    Estimated,
                    raw: Clamp(Math.Max(item.FullSize, video.m_ExternalResources.m_Size))),
                MovieTexture movie => Capacity(
                    Clamp(movie.m_MovieData?.LongLength ?? item.FullSize),
                    ExactForAuto,
                    raw: Clamp(movie.m_MovieData?.LongLength ?? item.FullSize)),
                Font font => Capacity(
                    Clamp(font.m_FontData?.LongLength ?? item.FullSize),
                    ExactForAuto,
                    raw: Clamp(font.m_FontData?.LongLength ?? item.FullSize)),
                Shader shader => Capacity(
                    shader.GetDirectScriptByteLength() >= 0
                        ? Clamp(shader.GetDirectScriptByteLength())
                        : Clamp(Math.Max(item.FullSize, item.FullSize * 4L)),
                    shader.GetDirectScriptByteLength() >= 0 ? ExactForAuto : Estimated,
                    raw: Clamp(item.FullSize),
                    text: shader.GetDirectScriptByteLength() >= 0
                        ? Clamp(shader.GetDirectScriptByteLength())
                        : Clamp(Math.Max(item.FullSize, item.FullSize * 4L))),
                _ => Capacity(Clamp(item.FullSize), Estimated, raw: Clamp(item.FullSize)),
            };
        }

        private readonly record struct AssetStudioPayloadCapacity(long Auto, long Raw, long Image, long Text, int Flags);

        private static long EstimateTextureImageCapacity(Texture2D texture, long fallback)
        {
            if (texture.m_Width <= 0 || texture.m_Height <= 0)
            {
                return ClampToInt32Range(fallback);
            }

            var pixels = SafeMultiply(texture.m_Width, texture.m_Height);
            var rgbaBytes = SafeMultiply(pixels, 4);
            return ClampToInt32Range(Math.Max(fallback, rgbaBytes + 4096));
        }

        private static long EstimateTextureArrayImageCapacity(Texture2DArray textureArray, long fallback)
        {
            var depth = Math.Max(textureArray.m_Depth, textureArray.TextureList?.Count ?? 0);
            if (textureArray.m_Width <= 0 || textureArray.m_Height <= 0 || depth <= 0)
            {
                return ClampToInt32Range(fallback);
            }

            var pixels = SafeMultiply(SafeMultiply(textureArray.m_Width, textureArray.m_Height), depth);
            var rgbaBytes = SafeMultiply(pixels, 4);
            return ClampToInt32Range(Math.Max(fallback, rgbaBytes + 4096L * depth + PayloadBundleHeaderLengthEstimate(depth)));
        }

        private static long EstimateSpriteImageCapacity(Sprite sprite, long fallback)
        {
            var width = Math.Max(0, (long)Math.Ceiling(sprite.m_Rect.width));
            var height = Math.Max(0, (long)Math.Ceiling(sprite.m_Rect.height));
            if (width <= 0 || height <= 0)
            {
                return ClampToInt32Range(fallback);
            }

            var rgbaBytes = SafeMultiply(SafeMultiply(width, height), 4);
            return ClampToInt32Range(Math.Max(fallback, rgbaBytes + 4096));
        }

        // Math.Clamp is not available on .NET Framework (net472).
        private static long ClampToInt32Range(long value)
        {
            return value < 0 ? 0 : value > int.MaxValue ? int.MaxValue : value;
        }

        private static long SafeMultiply(long left, long right)
        {
            if (left <= 0 || right <= 0)
            {
                return 0;
            }
            return left > int.MaxValue / right ? int.MaxValue : left * right;
        }

        private static long PayloadBundleHeaderLengthEstimate(int entryCount)
        {
            return 20L + Math.Max(entryCount, 0) * 64L;
        }

        private AssetStudioObjectPayload ReadObjectPayload(AssetItem item, AssetStudioObjectReadOptions options)
        {
            var requestedKind = string.IsNullOrWhiteSpace(options.Kind)
                ? "auto"
                : options.Kind.Trim().ToLowerInvariant();
            return item.Asset switch
            {
                Texture2D texture when requestedKind is "auto" or "image" => ReadTexturePayload(texture, options),
                Texture2DArray textureArray when requestedKind is "auto" or "image" or "image_archive" => ReadTextureArrayPayload(textureArray, options),
                Sprite sprite when requestedKind is "auto" or "image" => ReadSpritePayload(sprite, options),
                AudioClip audioClip when requestedKind is "auto" or "audio" or "raw" => ReadAudioClipPayload(audioClip),
                VideoClip videoClip when requestedKind is "auto" or "video" or "raw" => ReadVideoClipPayload(videoClip),
                MovieTexture movieTexture when requestedKind is "auto" or "video" or "raw" => ReadMovieTexturePayload(movieTexture),
                Font font when requestedKind is "auto" or "font" or "raw" => ReadFontPayload(font),
                Shader shader when requestedKind is "auto" or "shader" or "text" => ReadShaderPayload(shader),
                TextAsset textAsset when requestedKind is "auto" or "text_bytes" => ReadTextAssetPayload(textAsset),
                MonoBehaviour monoBehaviour when requestedKind is "auto" or "typetree_json" => ReadMonoBehaviourPayload(monoBehaviour),
                Mesh mesh when requestedKind is "auto" or "mesh" or "obj" => ReadMeshPayload(mesh),
                Animator _ when requestedKind is "auto" or "animator" or "fbx" => ReadAnimatorPayload(item),
                AssetStudio.Object asset when requestedKind is "auto" or "typetree_json" or "raw" => ReadGenericObjectPayload(asset, requestedKind),
                _ => throw new NotSupportedException($"unsupported kind `{requestedKind}` for asset type `{item.TypeString}` path_id {item.m_PathID}"),
            };
        }

        private AssetStudioObjectStreamPayload ReadObjectPayloadInto(AssetItem item, AssetStudioObjectReadOptions options, Stream destination)
        {
            var requestedKind = string.IsNullOrWhiteSpace(options.Kind)
                ? "auto"
                : options.Kind.Trim().ToLowerInvariant();

            switch (item.Asset)
            {
                case Texture2D texture when requestedKind is "auto" or "image":
                    return ReadTexturePayloadInto(texture, options, destination);
                case Texture2DArray textureArray when requestedKind is "auto" or "image" or "image_archive":
                    return ReadTextureArrayPayloadInto(textureArray, options, destination);
                case Sprite sprite when requestedKind is "auto" or "image":
                    return ReadSpritePayloadInto(sprite, options, destination);
                case AudioClip audioClip when requestedKind is "auto" or "audio" or "raw":
                    return ReadAudioClipPayloadInto(audioClip, destination);
                case VideoClip videoClip when requestedKind is "auto" or "video" or "raw":
                    return ReadVideoClipPayloadInto(videoClip, destination);
                case MovieTexture movieTexture when requestedKind is "auto" or "video" or "raw":
                    return WriteResidentPayload(movieTexture.m_MovieData, "movie_ogv", ".ogv", destination);
                case Font font when requestedKind is "auto" or "font" or "raw":
                    return WriteResidentPayload(font.m_FontData, "font", FontExtension(font.m_FontData ?? Array.Empty<byte>()), destination);
                case TextAsset textAsset when requestedKind is "auto" or "text_bytes":
                    return WriteResidentPayload(textAsset.m_Script, "text_bytes", ".bytes", destination);
                case Shader shader when requestedKind is "auto" or "shader" or "text":
                    return ReadShaderPayloadInto(shader, destination);
                case MonoBehaviour monoBehaviour when requestedKind is "auto" or "typetree_json":
                    return ReadGenericObjectPayloadInto(monoBehaviour, "typetree_json", destination);
                case Mesh mesh when requestedKind is "auto" or "mesh" or "obj":
                    return ReadMeshPayloadInto(mesh, destination);
                case Animator _ when requestedKind is "auto" or "animator" or "fbx":
                    return ReadAnimatorPayloadInto(item, destination);
                case AssetStudio.Object asset when requestedKind is "raw":
                    return ReadRawObjectPayloadInto(asset, destination);
                case AssetStudio.Object asset when requestedKind is "auto"
                    && asset is not Texture2D && asset is not Texture2DArray && asset is not Sprite
                    && asset is not AudioClip && asset is not VideoClip && asset is not MovieTexture && asset is not Font && asset is not Shader
                    && asset is not TextAsset && asset is not MonoBehaviour && asset is not Mesh && asset is not Animator
                    && asset.serializedType?.m_Type == null:
                    return ReadRawObjectPayloadInto(asset, destination);
                case AssetStudio.Object asset when requestedKind is "auto" or "typetree_json":
                    return ReadGenericObjectPayloadInto(asset, requestedKind, destination);
                default:
                    var payload = ReadObjectPayload(item, options);
                    if (payload.Payload.Length > 0)
                    {
                        destination.Write(payload.Payload, 0, payload.Payload.Length);
                    }
                    return new AssetStudioObjectStreamPayload(payload.Payload.Length, payload.PayloadKind, payload.SuggestedExtension, AssetStudioPayloadStreamingTier.ManagedPayload);
            }
        }

        private static AssetStudioObjectReadErrorKind ClassifyReadError(Exception exception)
        {
            return exception switch
            {
                ArgumentException => AssetStudioObjectReadErrorKind.InvalidRequest,
                InvalidOperationException invalidOperation when invalidOperation.Message.Contains("was not found", StringComparison.OrdinalIgnoreCase) => AssetStudioObjectReadErrorKind.AssetNotFound,
                NotSupportedException => AssetStudioObjectReadErrorKind.UnsupportedKind,
                _ => AssetStudioObjectReadErrorKind.InternalError,
            };
        }

        private AssetStudioObjectPayload ReadGenericObjectPayload(AssetStudio.Object asset, string requestedKind)
        {
            var phases = new Dictionary<string, long>();
            return ReadPayloadViaWriter(phases, "generic.write_payload", destination => ReadGenericObjectPayloadInto(asset, requestedKind, destination));
        }

        private AssetStudioObjectStreamPayload ReadGenericObjectPayloadInto(AssetStudio.Object asset, string requestedKind, Stream destination)
        {
            if (requestedKind != "raw" && asset.serializedType?.m_Type != null)
            {
                try
                {
                    var payloadLen = WriteAndMeasure(destination, () => WriteTypeTreeJsonPayload(asset, destination));
                    return new AssetStudioObjectStreamPayload(payloadLen, "typetree_json", ".json", AssetStudioPayloadStreamingTier.GeneratedStreaming);
                }
                catch (Exception ex)
                {
                    engine.LogWarning($"Failed to read {asset.type} path_id {asset.m_PathID} as typetree json, falling back to raw bytes: {ex.Message}");
                }
            }

            return ReadRawObjectPayloadInto(asset, destination);
        }

        private static AssetStudioObjectStreamPayload ReadRawObjectPayloadInto(AssetStudio.Object asset, Stream destination)
        {
            asset.CopyRawDataTo(destination);
            return new AssetStudioObjectStreamPayload(asset.byteSize, "raw", ".dat", AssetStudioPayloadStreamingTier.SourceStreaming);
        }

        private static void WriteTypeTreeJsonPayload(AssetStudio.Object asset, Stream destination)
        {
            var typeTree = asset.serializedType?.m_Type
                ?? throw new InvalidOperationException($"asset path_id {asset.m_PathID} has no typetree");
            asset.reader.Reset();
            using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            });
            writer.WriteStartObject();
            var nodes = typeTree.m_Nodes;
            var subtreeEnds = BuildTypeTreeSubtreeEnds(nodes);
            for (var i = 1; i < nodes.Count; i++)
            {
                writer.WritePropertyName(JsonFieldName(nodes[i].m_Name, i));
                WriteTypeTreeJsonValue(nodes, asset.reader, ref i, writer, subtreeEnds, nodes.Count);
            }
            writer.WriteEndObject();
            writer.Flush();
        }

        private static void WriteTypeTreeJsonValue(
            List<TypeTreeNode> nodes,
            BinaryReader reader,
            ref int i,
            Utf8JsonWriter writer,
            int[] subtreeEnds,
            int rangeEnd)
        {
            var node = nodes[i];
            var align = (node.m_MetaFlag & 0x4000) != 0;
            switch (node.m_Type)
            {
                case "SInt8":
                    writer.WriteNumberValue(reader.ReadSByte());
                    break;
                case "UInt8":
                    writer.WriteNumberValue(reader.ReadByte());
                    break;
                case "char":
                    writer.WriteStringValue(BitConverter.ToChar(reader.ReadBytes(2), 0).ToString());
                    break;
                case "short":
                case "SInt16":
                    writer.WriteNumberValue(reader.ReadInt16());
                    break;
                case "UInt16":
                case "unsigned short":
                    writer.WriteNumberValue(reader.ReadUInt16());
                    break;
                case "int":
                case "SInt32":
                    writer.WriteNumberValue(reader.ReadInt32());
                    break;
                case "UInt32":
                case "unsigned int":
                case "Type*":
                    writer.WriteNumberValue(reader.ReadUInt32());
                    break;
                case "long long":
                case "SInt64":
                    writer.WriteNumberValue(reader.ReadInt64());
                    break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                    writer.WriteNumberValue(reader.ReadUInt64());
                    break;
                case "float":
                    WriteFiniteNumberOrString(writer, reader.ReadSingle());
                    break;
                case "double":
                    WriteFiniteNumberOrString(writer, reader.ReadDouble());
                    break;
                case "bool":
                    writer.WriteBooleanValue(reader.ReadBoolean());
                    break;
                case "string" when i + 1 < nodes.Count && nodes[i + 1].m_Type == "Array":
                    writer.WriteStringValue(reader.ReadAlignedString());
                    i = subtreeEnds[i] - 1;
                    break;
                case "map":
                    if ((nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                    {
                        align = true;
                    }
                    var mapEnd = subtreeEnds[i];
                    var firstStart = i + 4;
                    var firstEnd = subtreeEnds[firstStart];
                    var secondStart = firstEnd;
                    var secondEnd = subtreeEnds[secondStart];
                    i = mapEnd - 1;
                    var mapSize = Math.Max(reader.ReadInt32(), 0);
                    writer.WriteStartArray();
                    for (var j = 0; j < mapSize; j++)
                    {
                        var keyIndex = firstStart;
                        var valueIndex = secondStart;
                        writer.WriteStartObject();
                        writer.WritePropertyName("key");
                        WriteTypeTreeJsonValue(nodes, reader, ref keyIndex, writer, subtreeEnds, firstEnd);
                        writer.WritePropertyName("value");
                        WriteTypeTreeJsonValue(nodes, reader, ref valueIndex, writer, subtreeEnds, secondEnd);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    break;
                case "TypelessData":
                    var typelessSize = Math.Max(reader.ReadInt32(), 0);
                    var offset = typelessSize > 0 ? reader.BaseStream.Position : 0;
                    writer.WriteStartObject();
                    writer.WriteNumber("Offset", offset);
                    writer.WriteNumber("Size", typelessSize);
                    writer.WriteEndObject();
                    reader.BaseStream.Position += typelessSize;
                    i += 2;
                    break;
                default:
                    if (i < rangeEnd - 1 && nodes[i + 1].m_Type == "Array")
                    {
                        if ((nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                        {
                            align = true;
                        }
                        var vectorEnd = subtreeEnds[i];
                        var itemStart = i + 3;
                        i = vectorEnd - 1;
                        var arraySize = Math.Max(reader.ReadInt32(), 0);
                        writer.WriteStartArray();
                        for (var j = 0; j < arraySize; j++)
                        {
                            var itemIndex = itemStart;
                            WriteTypeTreeJsonValue(nodes, reader, ref itemIndex, writer, subtreeEnds, vectorEnd);
                        }
                        writer.WriteEndArray();
                    }
                    else
                    {
                        var classStart = i;
                        var classEnd = subtreeEnds[i];
                        i = classEnd - 1;
                        writer.WriteStartObject();
                        for (var j = classStart + 1; j < classEnd; j++)
                        {
                            writer.WritePropertyName(JsonFieldName(nodes[j].m_Name, j));
                            WriteTypeTreeJsonValue(nodes, reader, ref j, writer, subtreeEnds, classEnd);
                        }
                        writer.WriteEndObject();
                    }
                    break;
            }
            if (align)
            {
                reader.AlignStream();
            }
        }

        private static int[] BuildTypeTreeSubtreeEnds(List<TypeTreeNode> nodes)
        {
            var ends = new int[nodes.Count];
            var stack = new Stack<int>();
            for (var i = 0; i < nodes.Count; i++)
            {
                while (stack.Count > 0 && nodes[i].m_Level <= nodes[stack.Peek()].m_Level)
                {
                    ends[stack.Pop()] = i;
                }
                stack.Push(i);
            }
            while (stack.Count > 0)
            {
                ends[stack.Pop()] = nodes.Count;
            }
            return ends;
        }

        private static void WriteFiniteNumberOrString(Utf8JsonWriter writer, float value)
        {
            if (!float.IsNaN(value) && !float.IsInfinity(value))
            {
                writer.WriteNumberValue(value);
            }
            else
            {
                writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void WriteFiniteNumberOrString(Utf8JsonWriter writer, double value)
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value))
            {
                writer.WriteNumberValue(value);
            }
            else
            {
                writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static string JsonFieldName(string? name, int index)
        {
            return string.IsNullOrEmpty(name) ? $"field_{index}" : name;
        }

        private AssetStudioObjectPayload ReadAnimatorPayload(AssetItem item)
        {
            var phases = new Dictionary<string, long>();
            return ReadPayloadViaWriter(phases, "animator.write_payload", destination => ReadAnimatorPayloadInto(item, destination));
        }

        private AssetStudioObjectStreamPayload ReadAnimatorPayloadInto(AssetItem item, Stream destination)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "haruki-assetstudio-animator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var exporter = new AssetExporter();
                exporter.Configure(new AssemblyLoader(), engine.Options);
                exporter.ExportAnimator(item, tempDir);
                var payloadLen = WriteAndMeasure(destination, () => WriteDirectoryPayloadBundle(tempDir, destination));
                return new AssetStudioObjectStreamPayload(payloadLen, "animator_bundle_fbx", "", AssetStudioPayloadStreamingTier.TempFileStreaming);
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup only; preserve the real object read error if export failed.
                }
            }
        }

        private static AssetStudioObjectPayload ReadTextureArrayPayload(Texture2DArray textureArray, AssetStudioObjectReadOptions options)
        {
            var phases = new Dictionary<string, long>();
            return ReadPayloadViaWriter(phases, "texture_array.write_payload", destination => ReadTextureArrayPayloadInto(textureArray, options, destination));
        }

        private static AssetStudioObjectStreamPayload ReadTextureArrayPayloadInto(Texture2DArray textureArray, AssetStudioObjectReadOptions options, Stream destination)
        {
            EnsureRawRgbaImageFormat(options.ImageFormat);

            // Decode each layer exactly once. The bundle header needs every entry
            // length up front, so decoded layers are held (pooled buffers) until the
            // header is written; the IR length is deterministic after a successful
            // decode, so no counting pass is required.
            var textures = GetTextureArrayLayers(textureArray);
            var decodedLayers = new List<(string Name, DecodedBgra32 Decoded)>();
            long payloadLen;
            try
            {
                for (var layer = 0; layer < textures.Count; layer++)
                {
                    var decoded = textures[layer].DecodeBgra32();
                    if (decoded == null)
                    {
                        continue;
                    }
                    decodedLayers.Add(($"layer_{layer:D4}.rgba", decoded));
                }

                payloadLen = WriteAndMeasure(destination, () =>
                {
                    WritePayloadBundleHeader(
                        destination,
                        decodedLayers
                            .Select(entry => (entry.Name, Texture2DExtensions.RgbaIrPayloadLength(entry.Decoded.Width, entry.Decoded.Height)))
                            .ToArray());
                    foreach (var entry in decodedLayers)
                    {
                        Texture2DExtensions.WriteRgbaIr(destination, entry.Decoded);
                    }
                });
            }
            finally
            {
                foreach (var entry in decodedLayers)
                {
                    entry.Decoded.Dispose();
                }
            }
            return new AssetStudioObjectStreamPayload(
                payloadLen,
                "image_array_bundle_raw_rgba",
                "",
                AssetStudioPayloadStreamingTier.GeneratedStreaming);
        }

        private static IReadOnlyList<Texture2D> GetTextureArrayLayers(Texture2DArray textureArray)
        {
            return textureArray.TextureList.Count > 0
                ? textureArray.TextureList
                : Enumerable.Range(0, Math.Max(textureArray.m_Depth, 0))
                    .Select(layer => new Texture2D(textureArray, layer))
                    .ToList();
        }

        private static AssetStudioObjectPayload ReadTexturePayload(Texture2D texture, AssetStudioObjectReadOptions options)
        {
            var phases = new Dictionary<string, long>();
            return ReadPayloadViaWriter(phases, "texture.write_payload", destination => ReadTexturePayloadInto(texture, options, destination));
        }

        private static AssetStudioObjectStreamPayload ReadTexturePayloadInto(Texture2D texture, AssetStudioObjectReadOptions options, Stream destination)
        {
            EnsureRawRgbaImageFormat(options.ImageFormat);
            // Direct span path: decode once into a pooled BGRA buffer and stream the
            // flipped/swizzled RGBA IR straight to the destination. No ImageSharp, no
            // process-wide image guard.
            long payloadLen;
            using (var decoded = texture.DecodeBgra32())
            {
                payloadLen = decoded == null
                    ? 0L
                    : WriteAndMeasure(destination, () => Texture2DExtensions.WriteRgbaIr(destination, decoded));
            }
            return new AssetStudioObjectStreamPayload(
                payloadLen,
                "image_raw_rgba",
                ".rgba",
                AssetStudioPayloadStreamingTier.GeneratedStreaming);
        }

        private static AssetStudioObjectPayload ReadSpritePayload(Sprite sprite, AssetStudioObjectReadOptions options)
        {
            var phases = new Dictionary<string, long>();
            return ReadPayloadViaWriter(phases, "sprite.write_payload", destination => ReadSpritePayloadInto(sprite, options, destination));
        }

        private static AssetStudioObjectStreamPayload ReadSpritePayloadInto(Sprite sprite, AssetStudioObjectReadOptions options, Stream destination)
        {
            EnsureRawRgbaImageFormat(options.ImageFormat);
            var payloadLen = ImageSharpNativeAotGuard.Run(() =>
            {
                using var image = sprite.GetImage(SpriteMaskMode.On);
                if (image == null)
                {
                    return 0L;
                }
                return WriteAndMeasure(destination, () => image.WriteRgbaIrToStream(destination));
            });
            return new AssetStudioObjectStreamPayload(
                payloadLen,
                "image_raw_rgba",
                ".rgba",
                AssetStudioPayloadStreamingTier.GeneratedStreaming);
        }

        private static AssetStudioObjectPayload ReadAudioClipPayload(AudioClip audioClip)
        {
            var phases = new Dictionary<string, long>();
            return ReadPayloadViaWriter(phases, "audio.copy_to", destination => ReadAudioClipPayloadInto(audioClip, destination));
        }

        private static AssetStudioObjectStreamPayload ReadAudioClipPayloadInto(AudioClip audioClip, Stream destination)
        {
            audioClip.m_AudioData.CopyTo(destination);
            return new AssetStudioObjectStreamPayload(audioClip.m_AudioData.Size, "audio_raw", AudioClipExtension(audioClip), AssetStudioPayloadStreamingTier.SourceStreaming);
        }

        private static AssetStudioObjectPayload ReadVideoClipPayload(VideoClip videoClip)
        {
            var phases = new Dictionary<string, long>();
            return ReadPayloadViaWriter(phases, "video.copy_to", destination => ReadVideoClipPayloadInto(videoClip, destination));
        }

        private static AssetStudioObjectStreamPayload ReadVideoClipPayloadInto(VideoClip videoClip, Stream destination)
        {
            var extension = Path.GetExtension(videoClip.m_OriginalPath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".video";
            }
            videoClip.m_VideoData.CopyTo(destination);
            return new AssetStudioObjectStreamPayload(videoClip.m_VideoData.Size, "video_raw", extension, AssetStudioPayloadStreamingTier.SourceStreaming);
        }

        private static AssetStudioObjectPayload ReadMovieTexturePayload(MovieTexture movieTexture)
        {
            return CreateResidentPayload(movieTexture.m_MovieData, "movie_ogv", ".ogv");
        }

        private static AssetStudioObjectPayload ReadFontPayload(Font font)
        {
            var phases = new Dictionary<string, long>();
            var payload = font.m_FontData ?? Array.Empty<byte>();
            return CreateResidentPayload(payload, "font", FontExtension(payload), phases);
        }

        private static string FontExtension(byte[] payload)
        {
            return payload.Length >= 4
                && payload[0] == 79
                && payload[1] == 84
                && payload[2] == 84
                && payload[3] == 79
                    ? ".otf"
                    : ".ttf";
        }

        private static AssetStudioObjectPayload ReadShaderPayload(Shader shader)
        {
            var phases = new Dictionary<string, long>();
            return ReadPayloadViaWriter(phases, "shader.write_payload", destination => ReadShaderPayloadInto(shader, destination));
        }

        private static AssetStudioObjectStreamPayload ReadShaderPayloadInto(Shader shader, Stream destination)
        {
            var directLen = shader.GetDirectScriptByteLength();
            var payloadLen = WriteAndMeasure(destination, () => shader.WriteTo(destination));
            return new AssetStudioObjectStreamPayload(
                payloadLen,
                "shader_text",
                ".shader",
                directLen >= 0 ? AssetStudioPayloadStreamingTier.DirectBufferResident : AssetStudioPayloadStreamingTier.GeneratedStreaming);
        }

        private static AssetStudioObjectPayload ReadTextAssetPayload(TextAsset textAsset)
        {
            var phases = new Dictionary<string, long>();
            return CreateResidentPayload(Measure(phases, "text_asset.bytes", () => textAsset.m_Script ?? Array.Empty<byte>()), "text_bytes", ".bytes", phases);
        }

        private static AssetStudioObjectPayload CreateResidentPayload(byte[]? payload, string payloadKind, string suggestedExtension, Dictionary<string, long>? phases = null)
        {
            return new AssetStudioObjectPayload
            {
                Payload = payload ?? Array.Empty<byte>(),
                PayloadKind = payloadKind,
                SuggestedExtension = suggestedExtension,
                PhaseMs = phases ?? new Dictionary<string, long>(),
            };
        }

        private static AssetStudioObjectStreamPayload WriteResidentPayload(byte[]? payload, string payloadKind, string suggestedExtension, Stream destination)
        {
            var residentPayload = payload ?? Array.Empty<byte>();
            destination.Write(residentPayload, 0, residentPayload.Length);
            return new AssetStudioObjectStreamPayload(residentPayload.Length, payloadKind, suggestedExtension, AssetStudioPayloadStreamingTier.DirectBufferResident);
        }

        private AssetStudioObjectPayload ReadMonoBehaviourPayload(MonoBehaviour monoBehaviour)
        {
            return ReadGenericObjectPayload(monoBehaviour, "typetree_json");
        }

        private static AssetStudioObjectPayload ReadMeshPayload(Mesh mesh)
        {
            var phases = new Dictionary<string, long>();
            return ReadPayloadViaWriter(phases, "mesh.write_payload", destination => ReadMeshPayloadInto(mesh, destination));
        }

        private static AssetStudioObjectStreamPayload ReadMeshPayloadInto(Mesh mesh, Stream destination)
        {
            mesh.ProcessData();
            var payloadLen = WriteAndMeasure(destination, () =>
            {
                using var writer = new StreamWriter(destination, Utf8NoBom, 8192, leaveOpen: true)
                {
                    NewLine = "\r\n",
                };
                WriteMeshObj(mesh, writer);
                writer.Flush();
            });
            return new AssetStudioObjectStreamPayload(payloadLen, "mesh_obj", ".obj", AssetStudioPayloadStreamingTier.GeneratedStreaming);
        }

        private static void WriteMeshObj(Mesh mesh, TextWriter writer)
        {
            if (mesh.m_VertexCount <= 0 || mesh.m_Vertices == null || mesh.m_Vertices.Length == 0)
            {
                return;
            }

            writer.WriteLine("g " + mesh.m_Name);

            var componentCount = mesh.m_Vertices.Length == mesh.m_VertexCount * 4 ? 4 : 3;
            for (var vertex = 0; vertex < mesh.m_VertexCount; vertex++)
            {
                WriteInvariantLine(writer, "v {0} {1} {2}", -mesh.m_Vertices[vertex * componentCount], mesh.m_Vertices[vertex * componentCount + 1], mesh.m_Vertices[vertex * componentCount + 2]);
            }

            if (mesh.m_UV0?.Length > 0)
            {
                componentCount = mesh.m_UV0.Length == mesh.m_VertexCount * 2 ? 2 : mesh.m_UV0.Length == mesh.m_VertexCount * 3 ? 3 : 4;
                for (var vertex = 0; vertex < mesh.m_VertexCount; vertex++)
                {
                    WriteInvariantLine(writer, "vt {0} {1}", mesh.m_UV0[vertex * componentCount], mesh.m_UV0[vertex * componentCount + 1]);
                }
            }

            if (mesh.m_Normals?.Length > 0)
            {
                componentCount = mesh.m_Normals.Length == mesh.m_VertexCount * 4 ? 4 : 3;
                for (var vertex = 0; vertex < mesh.m_VertexCount; vertex++)
                {
                    WriteInvariantLine(writer, "vn {0} {1} {2}", -mesh.m_Normals[vertex * componentCount], mesh.m_Normals[vertex * componentCount + 1], mesh.m_Normals[vertex * componentCount + 2]);
                }
            }

            var sum = 0;
            for (var subMeshIndex = 0; subMeshIndex < mesh.m_SubMeshes.Count; subMeshIndex++)
            {
                writer.WriteLine($"g {mesh.m_Name}_{subMeshIndex}");
                var indexCount = (int)mesh.m_SubMeshes[subMeshIndex].indexCount;
                var end = sum + indexCount / 3;
                for (var face = sum; face < end; face++)
                {
                    var a = mesh.m_Indices[face * 3 + 2] + 1;
                    var b = mesh.m_Indices[face * 3 + 1] + 1;
                    var c = mesh.m_Indices[face * 3] + 1;
                    WriteInvariantLine(writer, "f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}", a, b, c);
                }
                sum = end;
            }
        }

        private static void WriteInvariantLine(TextWriter writer, string format, params object[] args)
        {
            var line = string.Format(CultureInfo.InvariantCulture, format, args).Replace("NaN", "0", StringComparison.Ordinal);
            writer.WriteLine(line);
        }

        private static AssetStudioObjectPayload ReadPayloadViaWriter(
            Dictionary<string, long> phases,
            string phaseName,
            Func<Stream, AssetStudioObjectStreamPayload> writePayload)
        {
            AssetStudioObjectStreamPayload streamPayload = default;
            var payload = Measure(phases, phaseName, () =>
            {
                using var output = new MemoryStream();
                streamPayload = writePayload(output);
                return output.ToArray();
            });

            return new AssetStudioObjectPayload
            {
                Payload = payload,
                PayloadKind = streamPayload.PayloadKind,
                SuggestedExtension = streamPayload.SuggestedExtension,
                PhaseMs = phases,
            };
        }

        private static long WriteAndMeasure(Stream destination, Action write)
        {
            var start = destination.Position;
            write();
            return checked(destination.Position - start);
        }

        private static string AudioClipExtension(AudioClip audioClip)
        {
            if (audioClip.version < 5)
            {
                return audioClip.m_Type switch
                {
                    FMODSoundType.AAC => ".m4a",
                    FMODSoundType.AIFF => ".aif",
                    FMODSoundType.IT => ".it",
                    FMODSoundType.MOD => ".mod",
                    FMODSoundType.MPEG => ".mp3",
                    FMODSoundType.OGGVORBIS => ".ogg",
                    FMODSoundType.S3M => ".s3m",
                    FMODSoundType.WAV => ".wav",
                    FMODSoundType.XM => ".xm",
                    FMODSoundType.XMA => ".wav",
                    FMODSoundType.VAG => ".vag",
                    FMODSoundType.AUDIOQUEUE => ".fsb",
                    _ => ".AudioClip",
                };
            }

            return audioClip.m_CompressionFormat switch
            {
                AudioCompressionFormat.AAC => ".m4a",
                _ => ".fsb",
            };
        }

        private static string GetRelativePath(string directory, string path)
        {
#if NETFRAMEWORK
            // Path.GetRelativePath is not available on .NET Framework. Callers pass files enumerated under directory.
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath.Substring(root.Length) : fullPath;
#else
            return Path.GetRelativePath(directory, path);
#endif
        }

        private static void WriteDirectoryPayloadBundle(string directory, Stream destination)
        {
            var entries = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => (
                    Path: path,
                    Name: GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'),
                    PayloadLen: new FileInfo(path).Length))
                .ToArray();
            WritePayloadBundleHeader(destination, entries.Select(entry => (entry.Name, entry.PayloadLen)).ToArray());
            foreach (var entry in entries)
            {
                using var file = File.OpenRead(entry.Path);
                file.CopyTo(destination);
            }
        }

        private static void WritePayloadBundleHeader(Stream destination, IReadOnlyCollection<(string Name, long PayloadLen)> entries)
        {
            using var writer = new BinaryWriter(destination, Utf8NoBom, leaveOpen: true);
            writer.Write(PayloadBundleMagic);
            writer.Write(entries.Count);
            foreach (var (name, payload) in entries)
            {
                var nameBytes = Encoding.UTF8.GetBytes(name);
                writer.Write(nameBytes.Length);
                writer.Write(payload);
                writer.Write(nameBytes);
            }
            writer.Flush();
        }

        private static void EnsureRawRgbaImageFormat(string? imageFormat)
        {
            switch (imageFormat?.Trim().ToLowerInvariant())
            {
                case null:
                case "":
                case "raw_rgba":
                    return;
                default:
                    throw new ArgumentException($"unsupported image_format `{imageFormat}`; AssetStudioFFI image reads only support raw_rgba");
            }
        }

        private static string PhaseName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            var builder = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_');
            }
            return builder.ToString();
        }

        internal static void Measure(Dictionary<string, long> phases, string name, Action action)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            action();
            AddPhase(phases, name, stopwatch.ElapsedMilliseconds);
        }

        internal static T Measure<T>(Dictionary<string, long> phases, string name, Func<T> action)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = action();
            AddPhase(phases, name, stopwatch.ElapsedMilliseconds);
            return result;
        }

        private static void AddPhase(Dictionary<string, long> phases, string name, long elapsedMs)
        {
            phases[name] = phases.TryGetValue(name, out var current)
                ? current + elapsedMs
                : elapsedMs;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(AssetStudioSession));
            }
        }
    }
}
