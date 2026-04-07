using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using SkinTatoo.Core;
using SkinTatoo.Http;
using SkinTatoo.Interop;
using SkinTatoo.Mesh;

namespace SkinTatoo.Services;

public class PreviewService : IDisposable
{
    private readonly MeshExtractor meshExtractor;
    private readonly DecalImageLoader imageLoader;
    private readonly PenumbraBridge penumbra;
    private readonly TextureSwapService? textureSwap;
    private readonly EmissiveCBufferHook? emissiveHook;
    private readonly IPluginLog log;
    private readonly Configuration config;

    private MeshData? currentMesh;
    private readonly string outputDir;
    private bool disposed;

    // GPU swap state tracking — ConcurrentDictionary because background composite Task and
    // main thread (UpdatePreviewFull / EnsureEmissiveInitialized / ResetSwapState) can race.
    private readonly ConcurrentDictionary<string, byte> initializedRedirects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> previewDiskPaths =
        new(StringComparer.OrdinalIgnoreCase);

    // Cache base textures to avoid re-reading from disk on every update.
    // Disk-path key has no encoding constraint, so default comparer is fine.
    private readonly ConcurrentDictionary<string, (byte[] Data, int Width, int Height)> baseTextureCache = new();

    // Emissive ConstantBuffer offsets (mtrlGamePath → byte offset, used for full redraw .mtrl building)
    private readonly ConcurrentDictionary<string, int> emissiveOffsets = new();

    // Track mtrl preview disk paths (mtrlGamePath → disk path for ColorTable matching)
    private readonly ConcurrentDictionary<string, string> previewMtrlDiskPaths =
        new(StringComparer.OrdinalIgnoreCase);

    // v1 PBR: per-TargetGroup row pair allocators (keyed by MtrlGamePath)
    private readonly ConcurrentDictionary<string, RowPairAllocator> rowPairAllocators =
        new(StringComparer.OrdinalIgnoreCase);

    // v1 PBR: index map game path resolved from each mtrl's sampler 0x565F8FD8.
    // Keyed by MtrlGamePath. Cached after first resolution.
    private readonly ConcurrentDictionary<string, string> indexMapGamePaths =
        new(StringComparer.OrdinalIgnoreCase);

    // sampler ID for g_SamplerIndex per Penumbra ShpkFile.cs:17
    private const uint IndexSamplerId = 0x565F8FD8u;

    // Cached vanilla ColorTable bytes per material (keyed by MtrlGamePath).
    // Populated on first ColorTable write from the main thread; background composite
    // reads from here to avoid cross-thread GPU access.
    private readonly ConcurrentDictionary<string, (Half[] Data, int Width, int Height)> vanillaColorTables =
        new(StringComparer.OrdinalIgnoreCase);

    // Async compositing
    private CancellationTokenSource? asyncCancel;
    private volatile SwapBatch? pendingBatch;

    private record SwapBatchEntry(string GamePath, string? DiskPath, byte[] BgraData, int Width, int Height);
    private record EmissiveEntry(string MtrlGamePath, string? MtrlDiskPath, Vector3 Color, int CBufferOffset);
    private record ColorTableEntry(string MtrlGamePath, string? MtrlDiskPath, Half[] Data, int Width, int Height);
    private record SwapBatch(List<SwapBatchEntry> Textures, List<EmissiveEntry> Emissives, List<ColorTableEntry> ColorTables);

    public MeshData? CurrentMesh => currentMesh;

    /// <summary>Whether in-place GPU swap is available for all active groups.</summary>
    public bool CanSwapInPlace { get; private set; }

    /// <summary>Number of paths currently initialized for GPU swap.</summary>
    public int InitializedPathCount => initializedRedirects.Count;

    // 3D editor integration: per-group composite results keyed by diffuseGamePath
    private readonly ConcurrentDictionary<string, (byte[] Data, int Width, int Height)> compositeResults =
        new(StringComparer.OrdinalIgnoreCase);
    private long compositeVersion;
    public bool ExternalDirty { get; set; }
    public (byte[] Data, int Width, int Height)? GetCompositeForGroup(string? diffuseGamePath)
    {
        if (diffuseGamePath != null && compositeResults.TryGetValue(diffuseGamePath, out var result))
            return result;
        return null;
    }
    public long CompositeVersion => compositeVersion;
    public void MarkDirty() => ExternalDirty = true;

    /// <summary>Last update mode used.</summary>
    public string LastUpdateMode { get; private set; } = "none";

    /// <summary>Whether emissive CBuffer offset is known for a group's mtrl.</summary>
    public bool HasEmissiveOffset(string? mtrlGamePath)
        => !string.IsNullOrEmpty(mtrlGamePath) && emissiveOffsets.TryGetValue(mtrlGamePath, out var off) && off > 0;

    public PreviewService(
        MeshExtractor meshExtractor,
        DecalImageLoader imageLoader,
        PenumbraBridge penumbra,
        TextureSwapService? textureSwap,
        EmissiveCBufferHook? emissiveHook,
        IPluginLog log,
        Configuration config,
        string outputDir)
    {
        this.meshExtractor = meshExtractor;
        this.imageLoader = imageLoader;
        this.penumbra = penumbra;
        this.textureSwap = textureSwap;
        this.emissiveHook = emissiveHook;
        this.log = log;
        this.config = config;
        this.outputDir = outputDir;

        Directory.CreateDirectory(outputDir);
    }

    public bool LoadMesh(string gameMdlPath)
    {
        DebugServer.AppendLog($"[PreviewService] LoadMesh: {gameMdlPath}");

        MeshData? meshData;
        try
        {
            meshData = meshExtractor.ExtractMesh(gameMdlPath);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"LoadMesh exception: {ex.Message}");
            DebugServer.AppendLog($"[PreviewService] LoadMesh exception: {ex.Message}");
            return false;
        }

        if (meshData == null)
        {
            log.Error($"LoadMesh failed: ExtractMesh returned null for {gameMdlPath}");
            DebugServer.AppendLog($"[PreviewService] LoadMesh failed for {gameMdlPath}");
            return false;
        }

        currentMesh = meshData;
        DebugServer.AppendLog($"[PreviewService] Mesh loaded: {meshData.Vertices.Length} verts, {meshData.TriangleCount} tris");
        return true;
    }

    public bool LoadMeshes(List<string> paths)
    {
        if (paths.Count == 0) return false;
        if (paths.Count == 1) return LoadMesh(paths[0]);

        DebugServer.AppendLog($"[PreviewService] LoadMeshes: {paths.Count} files");
        try
        {
            var merged = meshExtractor.ExtractAndMerge(paths);
            if (merged == null) return false;
            currentMesh = merged;
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"LoadMeshes exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>Update preview — auto-selects full redraw or async in-place GPU swap.</summary>
    public void UpdatePreview(DecalProject project)
    {
        activeProject = project;  // remember for opportunistic vanilla CT caching in ApplyPendingSwaps
        if (config.UseGpuSwap && textureSwap != null && CheckCanSwapInPlace(project))
        {
            StartAsyncInPlace(project);
        }
        else
        {
            UpdatePreviewFull(project);
        }
    }

    // Sticky reference so ApplyPendingSwaps can attempt vanilla CT caching every frame
    // (only fires when project changes are pending). Set by UpdatePreview*.
    private DecalProject? activeProject;

    /// <summary>Call from Draw() every frame to apply completed async swaps.</summary>
    public unsafe void ApplyPendingSwaps()
    {
        // Opportunistic vanilla CT cache attempt — runs every frame but is idempotent
        // and only fires for materials not yet cached. Catches the case where Full Redraw
        // happened in an earlier frame and the GPU is now ready to read.
        if (activeProject != null)
            TryCacheVanillaColorTables(activeProject);

        var batch = Interlocked.Exchange(ref pendingBatch, null);
        if (batch == null) return;

        var charBase = textureSwap?.GetLocalPlayerCharacterBase();
        if (charBase == null) return;

        foreach (var entry in batch.Textures)
        {
            var slot = textureSwap!.FindTextureSlot(charBase, entry.GamePath, entry.DiskPath);
            if (slot != null)
                textureSwap.SwapTexture(slot, entry.BgraData, entry.Width, entry.Height);
        }

        foreach (var ct in batch.ColorTables)
        {
            textureSwap!.ReplaceColorTableRaw(charBase, ct.MtrlGamePath, ct.MtrlDiskPath, ct.Data, ct.Width, ct.Height);
        }

        foreach (var em in batch.Emissives)
        {
            // Legacy single-emissive path: only fires when no ColorTable entry was queued
            // for this material (skin.shpk fallback or non-Dawntrail layouts).
            textureSwap!.UpdateEmissiveViaColorTable(charBase, em.MtrlGamePath, em.MtrlDiskPath, em.Color);
            if (emissiveHook != null && em.CBufferOffset > 0)
                emissiveHook.SetTargetByPath(charBase, em.MtrlGamePath, em.MtrlDiskPath, em.Color);
        }

        LastUpdateMode = "inplace";
    }

    /// <summary>Get local player CharacterBase pointer for direct manipulation.</summary>
    public unsafe FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* GetCharacterBase()
        => textureSwap?.GetLocalPlayerCharacterBase();

    /// <summary>Write highlight emissive color via ColorTable texture swap or CBuffer hook.</summary>
    public unsafe void HighlightEmissiveColor(
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* charBase,
        TargetGroup group, Vector3 color)
    {
        if (textureSwap == null) return;
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return;

        var mtrlDiskPath = previewMtrlDiskPaths.GetValueOrDefault(group.MtrlGamePath!);
        emissiveOffsets.TryGetValue(group.MtrlGamePath!, out var cbufOffset);
        textureSwap.UpdateEmissiveViaColorTable(charBase, group.MtrlGamePath!, mtrlDiskPath, color);
        // Always set hook target for materials with known CBuffer offset (skin.shpk etc.)
        if (emissiveHook != null && cbufOffset > 0)
            emissiveHook.SetTargetByPath(charBase, group.MtrlGamePath!, mtrlDiskPath, color);
    }

    /// <summary>
    /// Ensure emissive mtrl redirect is initialized for a group (for highlight preview).
    /// Does a one-time mtrl build + Penumbra redirect + player redraw if needed.
    /// Returns true if emissive is ready for highlight.
    /// </summary>
    public bool EnsureEmissiveInitialized(TargetGroup group)
    {
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return false;

        // Already initialized?
        if (emissiveOffsets.ContainsKey(group.MtrlGamePath!) && emissiveOffsets[group.MtrlGamePath!] > 0)
            return true;

        // Build emissive mtrl
        var mtrlDisk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;
        if (string.IsNullOrEmpty(mtrlDisk) && string.IsNullOrEmpty(group.MtrlGamePath)) return false;

        var safeName = MakeSafeFileName(group.Name);
        var mtrlOutPath = Path.Combine(outputDir, $"preview_{safeName}.mtrl");
        var defaultColor = new Vector3(1f, 1f, 1f);

        if (!TryBuildEmissiveMtrl(mtrlDisk ?? group.MtrlGamePath!, mtrlOutPath, defaultColor, out var emOffset))
            return false;

        var redirects = new Dictionary<string, string> { [group.MtrlGamePath!] = mtrlOutPath };
        previewMtrlDiskPaths[group.MtrlGamePath!] = mtrlOutPath;
        if (emOffset >= 0)
            emissiveOffsets[group.MtrlGamePath!] = emOffset;
        initializedRedirects.TryAdd(group.MtrlGamePath!, 0);
        previewDiskPaths[group.MtrlGamePath!] = mtrlOutPath;

        // Also build norm with full-white alpha mask so highlight covers entire decal area
        if (!string.IsNullOrEmpty(group.NormGamePath) && !string.IsNullOrEmpty(group.NormDiskPath))
        {
            var normDisk = group.OrigNormDiskPath ?? group.NormDiskPath;
            if (!string.IsNullOrEmpty(normDisk))
            {
                var baseTex = LoadBaseTexture(group);
                var normResult = BuildFullAlphaNorm(normDisk, baseTex.Width, baseTex.Height);
                if (normResult != null)
                {
                    var normPath = Path.Combine(outputDir, $"preview_{safeName}_n.tex");
                    WriteBgraTexFile(normPath, normResult, baseTex.Width, baseTex.Height);
                    redirects[group.NormGamePath!] = normPath;
                    initializedRedirects.TryAdd(group.NormGamePath!, 0);
                    previewDiskPaths[group.NormGamePath!] = normPath;
                }
            }
        }

        penumbra.SetTextureRedirects(redirects);
        penumbra.RedrawPlayer();
        DebugServer.AppendLog($"[PreviewService] Emissive initialized for highlight: {group.MtrlGamePath} offset={emOffset}");
        return emOffset >= 0;
    }

    /// <summary>Build a normal map with alpha=255 everywhere (full emissive coverage for highlight).</summary>
    private byte[]? BuildFullAlphaNorm(string normDiskPath, int w, int h)
    {
        byte[] baseNorm;
        if (baseTextureCache.TryGetValue(normDiskPath, out var cachedNorm))
        {
            var (data, iw, ih) = cachedNorm;
            baseNorm = (iw != w || ih != h) ? ResizeBilinear(data, iw, ih, w, h) : (byte[])data.Clone();
        }
        else
        {
            var normImg = File.Exists(normDiskPath) ? imageLoader.LoadImage(normDiskPath) : LoadGameTexture(normDiskPath);
            if (normImg == null) return null;
            baseTextureCache[normDiskPath] = normImg.Value;
            var (data, iw, ih) = normImg.Value;
            baseNorm = (iw != w || ih != h) ? ResizeBilinear(data, iw, ih, w, h) : (byte[])data.Clone();
        }

        // Set alpha to 255 everywhere for full highlight coverage
        for (int i = 3; i < baseNorm.Length; i += 4)
            baseNorm[i] = 255;
        return baseNorm;
    }

    /// <summary>Force a full Penumbra redraw update (always flickers).</summary>
    public void UpdatePreviewFull(DecalProject project)
    {
        LastUpdateMode = "full";
        DebugServer.AppendLog("[PreviewService] Mode: FULL (Penumbra redraw)");

        try
        {
            var redirects = new Dictionary<string, string>();

            foreach (var group in project.Groups)
            {
                if (string.IsNullOrEmpty(group.DiffuseGamePath)) continue;
                if (group.Layers.Count == 0) continue;

                ProcessGroup(group, redirects);
            }

            if (redirects.Count > 0)
                penumbra.SetTextureRedirects(redirects);

            penumbra.RedrawPlayer();

            // Track initialized paths for future GPU swap
            foreach (var (gamePath, diskPath) in redirects)
            {
                initializedRedirects.TryAdd(gamePath, 0);
                previewDiskPaths[gamePath] = diskPath;
            }

            DebugServer.AppendLog(
                $"[PreviewService] Full update done ({redirects.Count} redirects, {initializedRedirects.Count} initialized)");
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PreviewService] Full update exception: {ex.Message}");
            log.Error(ex, "UpdatePreviewFull failed");
        }
    }

    /// <summary>Kick off background compositing, results applied via ApplyPendingSwaps.</summary>
    private unsafe void StartAsyncInPlace(DecalProject project)
    {
        // Cancel any previous background work
        asyncCancel?.Cancel();
        asyncCancel = new CancellationTokenSource();
        var token = asyncCancel.Token;

        // v1 PBR: cache vanilla ColorTable from GPU before spawning the background task.
        // Idempotent — only the FIRST successful cache per material counts. Vanilla normal
        // scan is handled lazily inside TryAllocateRowPairForLayer (disk-only, no GPU needed).
        TryCacheVanillaColorTables(project);

        // Capture all data needed by background thread (avoid touching mutable state later)
        var jobs = new List<(TargetGroup Group, string DiffuseGamePath, string? DiskPath,
            string? NormGamePath, string? NormDiskPath, string? MtrlGamePath, int EmissiveOffset,
            string? IndexGamePath, string? IndexDiskPath,
            List<LayerSnapshot> Layers, Vector3 EmissiveColor)>();

        foreach (var group in project.Groups)
        {
            if (string.IsNullOrEmpty(group.DiffuseGamePath) || group.Layers.Count == 0)
                continue;
            if (!previewDiskPaths.ContainsKey(group.DiffuseGamePath))
                continue;

            previewDiskPaths.TryGetValue(group.DiffuseGamePath, out var diffDisk);
            previewDiskPaths.TryGetValue(group.NormGamePath ?? "", out var normDisk);

            emissiveOffsets.TryGetValue(group.MtrlGamePath ?? "", out var emOff);

            // v1 PBR: resolve index map path (cached) and look up its staged disk path
            var indexGame = GetIndexMapGamePath(group);
            string? indexDisk = null;
            if (!string.IsNullOrEmpty(indexGame))
                previewDiskPaths.TryGetValue(indexGame, out indexDisk);

            // Snapshot layer parameters so background thread reads stable data
            var snapshots = new List<LayerSnapshot>();
            foreach (var l in group.Layers)
                snapshots.Add(new LayerSnapshot(l));

            jobs.Add((group, group.DiffuseGamePath, diffDisk,
                group.NormGamePath, normDisk ?? (group.OrigNormDiskPath ?? group.NormDiskPath),
                group.MtrlGamePath, emOff,
                indexGame, indexDisk,
                snapshots,
                GetCombinedEmissiveColor(group.Layers)));
        }

        Task.Run(() =>
        {
            try
            {
                var texEntries = new List<SwapBatchEntry>();
                var emEntries = new List<EmissiveEntry>();
                var ctEntries = new List<ColorTableEntry>();

                foreach (var job in jobs)
                {
                    if (token.IsCancellationRequested) return;

                    // Convert snapshots to DecalLayers for compositing
                    var layers = job.Layers.ConvertAll(s => s.ToDecalLayer());

                    // Diffuse composite
                    var baseTex = LoadBaseTexture(job.Group);

                    // GPU diffuse — only paints layers with AffectsDiffuse=true
                    var rgba = CpuUvComposite(layers, baseTex.Data, baseTex.Width, baseTex.Height);

                    // 3D editor preview diffuse — paints ALL visible layers with images so the
                    // user sees decal placement even when AffectsDiffuse is off.
                    bool hasPreviewOnly = false;
                    foreach (var l in layers)
                    {
                        if (l.IsVisible && !string.IsNullOrEmpty(l.ImagePath) && !l.AffectsDiffuse)
                        { hasPreviewOnly = true; break; }
                    }
                    var previewRgba = hasPreviewOnly
                        ? CpuUvComposite(layers, baseTex.Data, baseTex.Width, baseTex.Height, ignoreAffectsDiffuseFilter: true)
                        : rgba;

                    // 3D editor consumes compositeResults — feed it the preview-with-all-layers version
                    if (previewRgba != null)
                    {
                        compositeResults[job.DiffuseGamePath] = (previewRgba, baseTex.Width, baseTex.Height);
                        Interlocked.Increment(ref compositeVersion);
                    }

                    // GPU/file path uses the filtered version
                    if (rgba != null)
                    {
                        if (job.DiskPath != null)
                            WriteBgraTexFile(job.DiskPath, rgba, baseTex.Width, baseTex.Height);
                        var bgra = TextureSwapService.RgbaToBgra(rgba);
                        texEntries.Add(new SwapBatchEntry(
                            job.DiffuseGamePath, job.DiskPath, bgra, baseTex.Width, baseTex.Height));
                    }

                    // v1 PBR: layers with allocated row pairs drive the index-map + ColorTable path
                    var allocatedLayers = layers.Where(l => l.AllocatedRowPair >= 0 && l.IsVisible).ToList();
                    bool hasPbrLayers = allocatedLayers.Count > 0;
                    bool ctQueued = false;
                    bool indexQueued = false;

                    // Index map: rewrite R = rowPair*17, G = weight*255 (per Penumbra
                    // MaterialExporter:136-137). Vanilla bytes are read from the staged
                    // disk path that ProcessGroup populated, then cloned + modified.
                    if (hasPbrLayers && !string.IsNullOrEmpty(job.IndexGamePath) && !string.IsNullOrEmpty(job.IndexDiskPath))
                    {
                        var idxRgba = CompositeIndexMap(allocatedLayers, job.IndexDiskPath!, baseTex.Width, baseTex.Height);
                        if (idxRgba != null)
                        {
                            WriteBgraTexFile(job.IndexDiskPath!, idxRgba, baseTex.Width, baseTex.Height);
                            var idxBgra = TextureSwapService.RgbaToBgra(idxRgba);
                            texEntries.Add(new SwapBatchEntry(
                                job.IndexGamePath!, job.IndexDiskPath, idxBgra, baseTex.Width, baseTex.Height));
                            indexQueued = true;
                        }
                    }

                    if (hasPbrLayers && !string.IsNullOrEmpty(job.MtrlGamePath)
                        && vanillaColorTables.TryGetValue(job.MtrlGamePath!, out var vanilla)
                        && ColorTableBuilder.IsDawntrailLayout(vanilla.Width, vanilla.Height))
                    {
                        var modified = ColorTableBuilder.Build(vanilla.Data, vanilla.Width, vanilla.Height, allocatedLayers);
                        var mtrlDisk = previewMtrlDiskPaths.GetValueOrDefault(job.MtrlGamePath!);
                        ctEntries.Add(new ColorTableEntry(
                            job.MtrlGamePath!, mtrlDisk, modified, vanilla.Width, vanilla.Height));
                        ctQueued = true;
                    }

                    // Per-cycle PBR diagnostic — one line per group, fires only when relevant
                    if (hasPbrLayers)
                    {
                        bool vanillaCached = !string.IsNullOrEmpty(job.MtrlGamePath)
                            && vanillaColorTables.ContainsKey(job.MtrlGamePath!);
                        DebugServer.AppendLog(
                            $"[PBR] Cycle {job.Group.Name}: alloc={allocatedLayers.Count} " +
                            $"index={indexQueued} ct={ctQueued} vanillaCached={vanillaCached}");
                    }

                    // Legacy emissive fallback path: only fires when no ColorTable entry was queued
                    // (skin.shpk-class materials, or non-Dawntrail layouts).
                    if (!ctQueued && HasEmissiveLayers(job.Layers) && !string.IsNullOrEmpty(job.NormDiskPath))
                    {
                        var normRgba = CompositeEmissiveNorm(
                            layers, job.NormDiskPath!, baseTex.Width, baseTex.Height);
                        if (normRgba != null)
                        {
                            previewDiskPaths.TryGetValue(job.NormGamePath ?? "", out var normDiskOut);
                            if (normDiskOut != null)
                                WriteBgraTexFile(normDiskOut, normRgba, baseTex.Width, baseTex.Height);
                            var normBgra = TextureSwapService.RgbaToBgra(normRgba);
                            texEntries.Add(new SwapBatchEntry(
                                job.NormGamePath!, normDiskOut, normBgra, baseTex.Width, baseTex.Height));
                        }

                        if (!string.IsNullOrEmpty(job.MtrlGamePath))
                        {
                            var mtrlDisk = previewMtrlDiskPaths.GetValueOrDefault(job.MtrlGamePath!);
                            emissiveOffsets.TryGetValue(job.MtrlGamePath!, out var emOff);
                            emEntries.Add(new EmissiveEntry(job.MtrlGamePath!, mtrlDisk, job.EmissiveColor, emOff));
                        }
                    }
                }

                if (!token.IsCancellationRequested)
                    pendingBatch = new SwapBatch(texEntries, emEntries, ctEntries);
            }
            catch (Exception ex)
            {
                DebugServer.AppendLog($"[PreviewService] Async composite exception: {ex.Message}");
            }
        }, token);
    }

    // Snapshot of layer parameters for thread-safe background compositing
    private class LayerSnapshot
    {
        public LayerKind Kind;
        public string? Name;
        public bool IsVisible;
        public bool AffectsDiffuse, AffectsSpecular, AffectsEmissive, AffectsRoughness, AffectsMetalness, AffectsSheen;
        public string? ImagePath;
        public Vector2 UvCenter, UvScale;
        public float RotationDeg, Opacity;
        public BlendMode BlendMode;
        public ClipMode Clip;
        public LayerFadeMask FadeMask;
        public float FadeMaskFalloff;
        public Vector3 DiffuseColor, SpecularColor, EmissiveColor;
        public float EmissiveIntensity;
        public float Roughness, Metalness, SheenRate, SheenTint, SheenAperture;
        public float GradientAngleDeg;
        public float GradientScale;
        public float GradientOffset;
        public int AllocatedRowPair;

        public LayerSnapshot(DecalLayer l)
        {
            Kind = l.Kind; Name = l.Name; IsVisible = l.IsVisible;
            AffectsDiffuse = l.AffectsDiffuse; AffectsSpecular = l.AffectsSpecular; AffectsEmissive = l.AffectsEmissive;
            AffectsRoughness = l.AffectsRoughness; AffectsMetalness = l.AffectsMetalness; AffectsSheen = l.AffectsSheen;
            ImagePath = l.ImagePath; UvCenter = l.UvCenter; UvScale = l.UvScale;
            RotationDeg = l.RotationDeg; Opacity = l.Opacity; BlendMode = l.BlendMode;
            Clip = l.Clip;
            FadeMask = l.FadeMask; FadeMaskFalloff = l.FadeMaskFalloff;
            DiffuseColor = l.DiffuseColor; SpecularColor = l.SpecularColor;
            EmissiveColor = l.EmissiveColor; EmissiveIntensity = l.EmissiveIntensity;
            Roughness = l.Roughness; Metalness = l.Metalness;
            SheenRate = l.SheenRate; SheenTint = l.SheenTint; SheenAperture = l.SheenAperture;
            GradientAngleDeg = l.GradientAngleDeg; GradientScale = l.GradientScale;
            GradientOffset = l.GradientOffset;
            AllocatedRowPair = l.AllocatedRowPair;
        }

        public DecalLayer ToDecalLayer() => new()
        {
            Kind = Kind, Name = Name ?? "", IsVisible = IsVisible,
            AffectsDiffuse = AffectsDiffuse, AffectsSpecular = AffectsSpecular, AffectsEmissive = AffectsEmissive,
            AffectsRoughness = AffectsRoughness, AffectsMetalness = AffectsMetalness, AffectsSheen = AffectsSheen,
            ImagePath = ImagePath, UvCenter = UvCenter, UvScale = UvScale,
            RotationDeg = RotationDeg, Opacity = Opacity, BlendMode = BlendMode,
            Clip = Clip,
            FadeMask = FadeMask, FadeMaskFalloff = FadeMaskFalloff,
            DiffuseColor = DiffuseColor, SpecularColor = SpecularColor,
            EmissiveColor = EmissiveColor, EmissiveIntensity = EmissiveIntensity,
            Roughness = Roughness, Metalness = Metalness,
            SheenRate = SheenRate, SheenTint = SheenTint, SheenAperture = SheenAperture,
            GradientAngleDeg = GradientAngleDeg, GradientScale = GradientScale,
            GradientOffset = GradientOffset,
            AllocatedRowPair = AllocatedRowPair,
        };
    }

    private static bool HasEmissiveLayers(List<LayerSnapshot> layers)
    {
        foreach (var l in layers)
            if (l.IsVisible && l.AffectsEmissive && !string.IsNullOrEmpty(l.ImagePath))
                return true;
        return false;
    }

    /// <summary>
    /// v1 PBR: cache vanilla ColorTable from GPU for each managed target group.
    /// Idempotent — only fires for groups not yet cached. Must be called on the main thread.
    /// Path matching: uses the redirected staged disk path (post-Penumbra) since the live
    /// material's FileName contains the staged path, not the original.
    /// </summary>
    private unsafe void TryCacheVanillaColorTables(DecalProject project)
    {
        var charBase = textureSwap?.GetLocalPlayerCharacterBase();
        if (charBase == null) return;

        foreach (var group in project.Groups)
        {
            if (string.IsNullOrEmpty(group.MtrlGamePath)) continue;
            if (vanillaColorTables.ContainsKey(group.MtrlGamePath!)) continue;

            // After Penumbra redirect the live mtrl FileName contains the staged disk path,
            // not the original. Match by the staged path (falling back to orig for first-time
            // attempts before any redirect happened).
            var matchDisk = previewMtrlDiskPaths.GetValueOrDefault(group.MtrlGamePath!)
                            ?? group.OrigMtrlDiskPath
                            ?? group.MtrlDiskPath;

            if (textureSwap!.TryGetVanillaColorTable(
                    charBase, group.MtrlGamePath!, matchDisk,
                    out var ctData, out var ctW, out var ctH))
            {
                vanillaColorTables[group.MtrlGamePath!] = (ctData, ctW, ctH);
                DebugServer.AppendLog(
                    $"[PBR] Cached vanilla ColorTable {ctW}x{ctH} for {group.MtrlGamePath}");
            }
        }
    }

    /// <summary>Get (or lazily create) the row pair allocator for a target group.</summary>
    public RowPairAllocator GetOrCreateAllocator(TargetGroup group)
    {
        var key = group.MtrlGamePath ?? group.Name;
        return rowPairAllocators.GetOrAdd(key, _ => new RowPairAllocator());
    }

    /// <summary>
    /// Resolve the g_SamplerIndex (0x565F8FD8) texture's game path from a group's mtrl,
    /// caching the result. Returns null if mtrl is unreadable, the sampler is missing,
    /// or its texture index is out of range.
    /// </summary>
    public string? GetIndexMapGamePath(TargetGroup group)
    {
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return null;
        if (indexMapGamePaths.TryGetValue(group.MtrlGamePath!, out var cached))
            return cached;

        try
        {
            // Load mtrl bytes via disk first, then SqPack fallback
            var mtrlDisk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;
            byte[]? mtrlBytes = null;
            if (!string.IsNullOrEmpty(mtrlDisk) && File.Exists(mtrlDisk))
                mtrlBytes = File.ReadAllBytes(mtrlDisk);
            else
            {
                var pack = meshExtractor.GetSqPackInstance();
                var sqResult = pack?.GetFile(group.MtrlGamePath!);
                if (sqResult != null) mtrlBytes = sqResult.Value.file.RawData.ToArray();
            }
            if (mtrlBytes == null) return null;

            // Parse via Lumina by writing to a temp file (Lumina API limitation)
            var tempPath = Path.Combine(outputDir, $"temp_idx_{Guid.NewGuid():N}.mtrl");
            File.WriteAllBytes(tempPath, mtrlBytes);
            var lumina = meshExtractor.GetLuminaForDisk();
            var mtrl = lumina!.GetFileFromDisk<MtrlFile>(tempPath);
            try { File.Delete(tempPath); } catch { }

            // Find sampler 0x565F8FD8 → texture index → string offset → null-terminated path
            int texIndex = -1;
            foreach (var s in mtrl.Samplers)
            {
                if (s.SamplerId == IndexSamplerId) { texIndex = s.TextureIndex; break; }
            }
            if (texIndex < 0 || texIndex >= mtrl.TextureOffsets.Length)
            {
                DebugServer.AppendLog($"[PBR] No g_SamplerIndex (0x565F8FD8) in mtrl {group.MtrlGamePath}");
                return null;
            }

            int strOffset = mtrl.TextureOffsets[texIndex].Offset;
            int end = strOffset;
            while (end < mtrl.Strings.Length && mtrl.Strings[end] != 0) end++;
            var indexGamePath = System.Text.Encoding.UTF8.GetString(mtrl.Strings, strOffset, end - strOffset);
            if (string.IsNullOrEmpty(indexGamePath)) return null;

            indexMapGamePaths[group.MtrlGamePath!] = indexGamePath;
            DebugServer.AppendLog($"[PBR] Resolved index map for {group.Name}: {indexGamePath}");
            return indexGamePath;
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PBR] GetIndexMapGamePath failed for {group.Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Diagnostic snapshot for HTTP /api/debug/pbr — returns one entry per managed group
    /// describing allocator state and ColorTable cache hit/miss.
    /// </summary>
    public List<object> GetPbrDiagnostics(DecalProject project)
    {
        var result = new List<object>();
        foreach (var group in project.Groups)
        {
            var alloc = GetOrCreateAllocator(group);
            bool ctCached = !string.IsNullOrEmpty(group.MtrlGamePath)
                && vanillaColorTables.ContainsKey(group.MtrlGamePath!);
            (int W, int H) ctSize = (0, 0);
            if (ctCached && vanillaColorTables.TryGetValue(group.MtrlGamePath!, out var v))
                ctSize = (v.Width, v.Height);

            var layerStates = new List<object>();
            foreach (var l in group.Layers)
            {
                layerStates.Add(new
                {
                    name = l.Name,
                    kind = l.Kind.ToString(),
                    visible = l.IsVisible,
                    allocatedRowPair = l.AllocatedRowPair,
                    requiresRowPair = l.RequiresRowPair,
                    affectsDiffuse = l.AffectsDiffuse,
                    affectsSpecular = l.AffectsSpecular,
                    affectsEmissive = l.AffectsEmissive,
                    affectsRoughness = l.AffectsRoughness,
                    affectsMetalness = l.AffectsMetalness,
                    affectsSheen = l.AffectsSheen,
                });
            }

            result.Add(new
            {
                groupName = group.Name,
                mtrlGamePath = group.MtrlGamePath,
                hasPbrLayers = group.HasPbrLayers(),
                hasEmissiveLayers = group.HasEmissiveLayers(),
                allocator = new
                {
                    scanned = alloc.Scanned,
                    vanillaOccupied = alloc.VanillaOccupiedCount,
                    available = alloc.AvailableSlots,
                },
                vanillaColorTable = new
                {
                    cached = ctCached,
                    width = ctSize.W,
                    height = ctSize.H,
                    isDawntrail = ctCached && ColorTableBuilder.IsDawntrailLayout(ctSize.W, ctSize.H),
                },
                layers = layerStates,
            });
        }
        return result;
    }

    /// <summary>
    /// Called from UI when a layer needs a row pair (first Affects* toggled on).
    /// Returns true on success; false on exhaustion (caller must toast and revert the toggle).
    /// Lazy-scans vanilla normal histogram on first allocation per group, so we never
    /// hand out a slot that vanilla is already using.
    /// </summary>
    public bool TryAllocateRowPairForLayer(TargetGroup group, DecalLayer layer)
    {
        if (layer.AllocatedRowPair >= 0) return true;   // already has one
        var alloc = GetOrCreateAllocator(group);

        // Critical: scan vanilla BEFORE allocating, otherwise we may hand out a row pair
        // that vanilla already uses, causing layer PBR to bleed into vanilla regions.
        EnsureVanillaScan(group, alloc);

        var slot = alloc.TryAllocate();
        if (slot == null)
        {
            DebugServer.AppendLog(
                $"[PBR] Row pair exhausted for {group.Name} " +
                $"(avail={alloc.AvailableSlots}, vanilla={alloc.VanillaOccupiedCount})");
            return false;
        }
        layer.AllocatedRowPair = slot.Value;
        DebugServer.AppendLog($"[PBR] Allocated row pair {slot.Value} to '{layer.Name}'");
        return true;
    }

    /// <summary>
    /// Vanilla scan: read the index map (g_SamplerIndex) and feed its R channel to
    /// the row pair allocator so we never hand out a slot vanilla is already using.
    /// Loads via SqPack — no Penumbra redirect needed, no GPU access required.
    /// Idempotent.
    /// </summary>
    private void EnsureVanillaScan(TargetGroup group, RowPairAllocator alloc)
    {
        if (alloc.Scanned) return;

        var indexGamePath = GetIndexMapGamePath(group);
        if (string.IsNullOrEmpty(indexGamePath))
        {
            DebugServer.AppendLog($"[PBR] Vanilla scan skipped for {group.Name}: no index map sampler");
            return;
        }

        var indexImg = LoadGameTexture(indexGamePath);
        if (indexImg == null)
        {
            DebugServer.AppendLog($"[PBR] Vanilla scan failed for {group.Name}: cannot load index map {indexGamePath}");
            return;
        }

        var (indexData, w, h) = indexImg.Value;
        alloc.ScanVanillaOccupation(indexData, w, h);
        DebugServer.AppendLog(
            $"[PBR] Vanilla scan {group.Name}: vanillaOccupied={alloc.VanillaOccupiedCount}, available={alloc.AvailableSlots}");
    }

    /// <summary>
    /// Release this layer's row pair if it no longer requires one.
    /// Call after toggling off Affects* fields or before deleting a layer.
    /// </summary>
    public void ReleaseRowPairIfUnused(TargetGroup group, DecalLayer layer)
    {
        if (layer.AllocatedRowPair < 0) return;
        if (layer.RequiresRowPair) return;  // still needed

        var alloc = GetOrCreateAllocator(group);
        alloc.Release(layer.AllocatedRowPair);
        DebugServer.AppendLog($"[PreviewService] Released row pair {layer.AllocatedRowPair} from layer '{layer.Name}'");
        layer.AllocatedRowPair = -1;
    }

    /// <summary>Force-release this layer's row pair (e.g. on layer deletion).</summary>
    public void ForceReleaseRowPair(TargetGroup group, DecalLayer layer)
    {
        if (layer.AllocatedRowPair < 0) return;
        var alloc = GetOrCreateAllocator(group);
        alloc.Release(layer.AllocatedRowPair);
        layer.AllocatedRowPair = -1;
    }

    /// <summary>Clear emissive hook targets (e.g. when emissive is disabled).</summary>
    public void ClearEmissiveHookTargets() => emissiveHook?.ClearTargets();

    /// <summary>Clear GPU swap state, forcing next update to do full redraw.</summary>
    public void ResetSwapState()
    {
        asyncCancel?.Cancel();
        pendingBatch = null;
        initializedRedirects.Clear();
        previewDiskPaths.Clear();
        emissiveOffsets.Clear();
        previewMtrlDiskPaths.Clear();
        rowPairAllocators.Clear();
        vanillaColorTables.Clear();
        indexMapGamePaths.Clear();
        activeProject = null;
        emissiveHook?.ClearTargets();
        CanSwapInPlace = false;
        LastUpdateMode = "none";
        DebugServer.AppendLog("[PreviewService] GPU swap state reset");
    }

    /// <summary>
    /// Composite a group's textures into the given staging directory using
    /// game-path mirrored layout, only including visible layers. Does NOT mutate
    /// any PreviewService runtime state (GPU swap, caches, hook targets).
    /// Returns gamePath → relative disk path (forward slashes) for default_mod.json.
    /// </summary>
    internal Dictionary<string, string> CompositeForExport(TargetGroup group, string stagingDir)
    {
        var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(group.DiffuseGamePath))
            return redirects;

        // Build a temporary list of visible layers (skip hidden / null-image)
        var visibleLayers = new List<DecalLayer>();
        foreach (var l in group.Layers)
        {
            if (!l.IsVisible || string.IsNullOrEmpty(l.ImagePath)) continue;
            visibleLayers.Add(l);
        }
        if (visibleLayers.Count == 0)
            return redirects;

        var baseTex = LoadBaseTexture(group);
        int w = baseTex.Width, h = baseTex.Height;

        // Diffuse composite — write to staging/<gamePath>
        var diffResult = CpuUvComposite(visibleLayers, baseTex.Data, w, h);
        if (diffResult != null)
        {
            var diffOut = WriteStagingTex(stagingDir, group.DiffuseGamePath!, diffResult, w, h);
            redirects[group.DiffuseGamePath!] = diffOut;
            DebugServer.AppendLog($"[ModExport] Diffuse → {group.DiffuseGamePath}");
        }

        // Emissive: only if there are visible emissive layers + mtrl path is known
        bool hasEmissive = false;
        foreach (var l in visibleLayers)
            if (l.AffectsEmissive) { hasEmissive = true; break; }

        if (hasEmissive && !string.IsNullOrEmpty(group.MtrlGamePath))
        {
            var emissiveColor = GetCombinedEmissiveColor(visibleLayers);
            var mtrlSource = group.OrigMtrlDiskPath ?? group.MtrlDiskPath ?? group.MtrlGamePath!;
            var mtrlOut = StagingPathFor(stagingDir, group.MtrlGamePath!);
            Directory.CreateDirectory(Path.GetDirectoryName(mtrlOut)!);
            if (TryBuildEmissiveMtrl(mtrlSource, mtrlOut, emissiveColor, out _))
            {
                redirects[group.MtrlGamePath!] = ToForwardSlash(group.MtrlGamePath!);
                DebugServer.AppendLog($"[ModExport] Mtrl → {group.MtrlGamePath}");
            }

            // Emissive normal map (alpha mask)
            if (!string.IsNullOrEmpty(group.NormGamePath) && !string.IsNullOrEmpty(group.NormDiskPath))
            {
                var normSource = group.OrigNormDiskPath ?? group.NormDiskPath!;
                var normResult = CompositeEmissiveNorm(visibleLayers, normSource, w, h);
                if (normResult != null)
                {
                    var normOut = WriteStagingTex(stagingDir, group.NormGamePath!, normResult, w, h);
                    redirects[group.NormGamePath!] = normOut;
                    DebugServer.AppendLog($"[ModExport] Norm (emissive alpha) → {group.NormGamePath}");
                }
            }
        }

        return redirects;
    }

    /// <summary>Map a game path to a staging-rooted disk path (mirrors game tree).</summary>
    private static string StagingPathFor(string stagingDir, string gamePath)
        => Path.Combine(stagingDir, gamePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Convert any path to forward-slash form for default_mod.json.</summary>
    private static string ToForwardSlash(string p) => p.Replace('\\', '/');

    /// <summary>Write a composited RGBA buffer as .tex into staging at the game-path mirror.</summary>
    private string WriteStagingTex(string stagingDir, string gamePath, byte[] rgba, int w, int h)
    {
        var diskPath = StagingPathFor(stagingDir, gamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
        WriteBgraTexFile(diskPath, rgba, w, h);
        return ToForwardSlash(gamePath);
    }

    // ── Private: check if all groups can swap in-place ───────────────────────

    // One-shot CanSwap=NO log gating — avoid spamming the log on every poll
    private string? lastCanSwapDenyReason;

    private void LogCanSwapDeny(string reason)
    {
        if (lastCanSwapDenyReason != reason)
        {
            DebugServer.AppendLog($"[PBR] CanSwap=NO {reason}");
            lastCanSwapDenyReason = reason;
        }
    }

    private bool CheckCanSwapInPlace(DecalProject project)
    {
        if (initializedRedirects.Count == 0)
        {
            CanSwapInPlace = false;
            return false;
        }

        foreach (var group in project.Groups)
        {
            if (string.IsNullOrEmpty(group.DiffuseGamePath) || group.Layers.Count == 0)
                continue;

            // If this group never produced a redirect (no visible output),
            // it doesn't need to be initialized — skip it.
            if (!previewDiskPaths.ContainsKey(group.DiffuseGamePath))
                continue;

            if (!initializedRedirects.ContainsKey(group.DiffuseGamePath))
            {
                LogCanSwapDeny($"diffuse not initialized: {group.DiffuseGamePath}");
                CanSwapInPlace = false;
                return false;
            }

            if (group.HasEmissiveLayers() || group.HasPbrLayers())
            {
                if (!string.IsNullOrEmpty(group.MtrlGamePath)
                    && !previewDiskPaths.ContainsKey(group.MtrlGamePath))
                {
                    LogCanSwapDeny($"PBR/emissive needs mtrl init: {group.MtrlGamePath}");
                    CanSwapInPlace = false;
                    return false;
                }

                if (!string.IsNullOrEmpty(group.NormGamePath)
                    && previewDiskPaths.ContainsKey(group.NormGamePath)
                    && !initializedRedirects.ContainsKey(group.NormGamePath))
                {
                    LogCanSwapDeny($"norm not initialized: {group.NormGamePath}");
                    CanSwapInPlace = false;
                    return false;
                }

                if (!string.IsNullOrEmpty(group.MtrlGamePath)
                    && previewDiskPaths.ContainsKey(group.MtrlGamePath)
                    && !initializedRedirects.ContainsKey(group.MtrlGamePath))
                {
                    LogCanSwapDeny($"mtrl not initialized: {group.MtrlGamePath}");
                    CanSwapInPlace = false;
                    return false;
                }
            }

            // v1 PBR (character.shpk path): index map must be mounted before inplace can fire
            if (group.HasPbrLayers())
            {
                var indexGamePath = GetIndexMapGamePath(group);
                if (!string.IsNullOrEmpty(indexGamePath)
                    && !previewDiskPaths.ContainsKey(indexGamePath))
                {
                    LogCanSwapDeny($"PBR needs index map init: {indexGamePath}");
                    CanSwapInPlace = false;
                    return false;
                }
            }
        }

        if (lastCanSwapDenyReason != null)
        {
            DebugServer.AppendLog("[PBR] CanSwap=YES");
            lastCanSwapDenyReason = null;
        }
        CanSwapInPlace = true;
        return true;
    }

    // ── Private: shared helpers ──────────────────────────────────────────────

    private (byte[] Data, int Width, int Height) LoadBaseTexture(TargetGroup group)
    {
        var diffuseDisk = group.OrigDiffuseDiskPath ?? group.DiffuseDiskPath;
        if (string.IsNullOrEmpty(diffuseDisk))
        {
            int res = config.TextureResolution;
            return (new byte[res * res * 4], res, res);
        }

        // Return cached version if available
        if (baseTextureCache.TryGetValue(diffuseDisk, out var cached))
            return cached;

        var baseTex = LoadTexture(diffuseDisk);
        if (baseTex != null)
        {
            baseTextureCache[diffuseDisk] = baseTex.Value;
            return baseTex.Value;
        }

        // Don't cache the empty fallback — next call should retry the load
        int r = config.TextureResolution;
        return (new byte[r * r * 4], r, r);
    }

    private void ProcessGroup(TargetGroup group, Dictionary<string, string> redirects)
    {
        var baseTex = LoadBaseTexture(group);
        int w = baseTex.Width, h = baseTex.Height;

        // Diffuse composite
        // GPU diffuse — only paints layers with AffectsDiffuse=true
        var diffResult = CpuUvComposite(group.Layers, baseTex.Data, w, h);

        // 3D editor preview diffuse — paints ALL visible layers with images so the user
        // sees decal placement even when AffectsDiffuse is off (placement guide).
        // Skip the second pass when no layer is "preview-only" (perf optimization).
        bool hasPreviewOnly = false;
        foreach (var l in group.Layers)
        {
            if (l.IsVisible && !string.IsNullOrEmpty(l.ImagePath) && !l.AffectsDiffuse)
            { hasPreviewOnly = true; break; }
        }
        var diffPreview = hasPreviewOnly
            ? CpuUvComposite(group.Layers, baseTex.Data, w, h, ignoreAffectsDiffuseFilter: true)
            : diffResult;

        // PBR-only or WholeMaterial-only groups produce no diffuse delta but still need the
        // material mounted for inplace ColorTable swap. Synthesize a passthrough by cloning
        // vanilla diffuse so the redirect pipeline can engage.
        if (diffResult == null && group.HasPbrLayers())
        {
            diffResult = (byte[])baseTex.Data.Clone();
            DebugServer.AppendLog($"[PBR] Passthrough diffuse for PBR-only group {group.Name}");
        }
        if (diffPreview == null && group.HasPbrLayers())
            diffPreview = (byte[])baseTex.Data.Clone();

        // 3D editor reads compositeResults — feed it the preview version
        if (diffPreview != null)
        {
            compositeResults[group.DiffuseGamePath!] = (diffPreview, w, h);
            Interlocked.Increment(ref compositeVersion);
        }

        // GPU/file path uses the filtered version
        if (diffResult != null)
        {
            var safeName = MakeSafeFileName(group.Name);
            var path = Path.Combine(outputDir, $"preview_{safeName}_d.tex");
            WriteBgraTexFile(path, diffResult, w, h);
            redirects[group.DiffuseGamePath!] = path;
        }

        // Emissive / PBR: modify .mtrl, write emissive into norm.a (skin.shpk legacy path),
        // and mount index map for ColorTable PBR (character.shpk path).
        var hasEmissive = group.HasEmissiveLayers();
        var hasPbr = group.HasPbrLayers();
        if ((hasEmissive || hasPbr) && !string.IsNullOrEmpty(group.MtrlGamePath))
        {
            var emissiveColor = GetCombinedEmissiveColor(group.Layers);
            var safeName = MakeSafeFileName(group.Name);
            var mtrlOutPath = Path.Combine(outputDir, $"preview_{safeName}.mtrl");
            var mtrlDisk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;

            if (TryBuildEmissiveMtrl(mtrlDisk ?? group.MtrlGamePath!, mtrlOutPath, emissiveColor, out var emOffset))
            {
                redirects[group.MtrlGamePath!] = mtrlOutPath;
                previewMtrlDiskPaths[group.MtrlGamePath!] = mtrlOutPath;
                if (emOffset >= 0)
                    emissiveOffsets[group.MtrlGamePath!] = emOffset;
            }

            // Legacy emissive path: write emissive into normal map alpha (skin.shpk fallback)
            if (hasEmissive && !string.IsNullOrEmpty(group.NormGamePath) && !string.IsNullOrEmpty(group.NormDiskPath))
            {
                var normDisk = group.OrigNormDiskPath ?? group.NormDiskPath;
                var normResult = CompositeEmissiveNorm(group.Layers, normDisk!, w, h);
                if (normResult != null)
                {
                    var normPath = Path.Combine(outputDir, $"preview_{safeName}_n.tex");
                    WriteBgraTexFile(normPath, normResult, w, h);
                    redirects[group.NormGamePath!] = normPath;
                }
            }

            // v1 PBR (character.shpk path): mount index map (g_SamplerIndex 0x565F8FD8).
            // First Full Redraw writes a passthrough vanilla copy; subsequent inplace updates
            // overwrite it via CompositeIndexMap with row pair + weight in R/G channels.
            if (hasPbr)
            {
                var indexGamePath = GetIndexMapGamePath(group);
                if (!string.IsNullOrEmpty(indexGamePath))
                {
                    var indexImg = LoadGameTexture(indexGamePath);
                    if (indexImg != null)
                    {
                        var (data, iw, ih) = indexImg.Value;
                        var indexClone = (byte[])data.Clone();
                        var indexPath = Path.Combine(outputDir, $"preview_{safeName}_id.tex");
                        WriteBgraTexFile(indexPath, indexClone, iw, ih);
                        redirects[indexGamePath] = indexPath;
                        // Cache vanilla index bytes by the staged disk path so the background
                        // composite can read them back as the baseline for CompositeIndexMap.
                        baseTextureCache[indexPath] = (data, iw, ih);
                        DebugServer.AppendLog($"[PBR] Mounted index map: {indexGamePath} → {indexPath}");
                    }
                    else
                    {
                        DebugServer.AppendLog($"[PBR] Failed to load vanilla index map: {indexGamePath}");
                    }
                }
            }
        }
    }

    private static string MakeSafeFileName(string name)
    {
        // Use ASCII-only hash to avoid encoding mismatch between disk path
        // and game ResourceHandle.FileName (which garbles non-ASCII characters)
        var hash = (uint)name.GetHashCode(StringComparison.Ordinal);
        return $"g{hash:X8}";
    }

    private (byte[] Data, int Width, int Height)? LoadTexture(string? diskPath)
    {
        if (string.IsNullOrEmpty(diskPath)) return null;
        var img = File.Exists(diskPath) ? imageLoader.LoadImage(diskPath) : LoadGameTexture(diskPath);
        return img;
    }

    /// <summary>
    /// Composite emissive area into normal map alpha channel.
    /// </summary>
    private byte[]? CompositeEmissiveNorm(List<DecalLayer> layers, string normDiskPath, int w, int h)
    {
        byte[] baseNorm;

        // Cache the base normal texture
        if (baseTextureCache.TryGetValue(normDiskPath, out var cachedNorm))
        {
            var (data, iw, ih) = cachedNorm;
            baseNorm = (iw != w || ih != h) ? ResizeBilinear(data, iw, ih, w, h) : (byte[])data.Clone();
        }
        else
        {
            var normImg = File.Exists(normDiskPath) ? imageLoader.LoadImage(normDiskPath) : LoadGameTexture(normDiskPath);
            if (normImg != null)
            {
                baseTextureCache[normDiskPath] = normImg.Value;
                var (data, iw, ih) = normImg.Value;
                baseNorm = (iw != w || ih != h) ? ResizeBilinear(data, iw, ih, w, h) : (byte[])data.Clone();
            }
            else
            {
                baseNorm = new byte[w * h * 4];
            }
        }

        var output = (byte[])baseNorm.Clone();

        // Zero out alpha channel everywhere
        for (int i = 3; i < output.Length; i += 4)
            output[i] = 0;

        bool anyEmissive = false;
        foreach (var layer in layers)
        {
            if (!layer.IsVisible || !layer.AffectsEmissive || string.IsNullOrEmpty(layer.ImagePath)) continue;

            var decalImage = imageLoader.LoadImage(layer.ImagePath);
            if (decalImage == null) continue;

            var (decalData, decalW, decalH) = decalImage.Value;
            var center = layer.UvCenter;
            var scale = layer.UvScale;
            var opacity = layer.Opacity;
            var rotRad = layer.RotationDeg * (MathF.PI / 180f);
            var cosR = MathF.Cos(rotRad);
            var sinR = MathF.Sin(rotRad);

            int pxMin = Math.Max(0, (int)((center.X - scale.X / 2f) * w));
            int pxMax = Math.Min(w - 1, (int)((center.X + scale.X / 2f) * w));
            int pyMin = Math.Max(0, (int)((center.Y - scale.Y / 2f) * h));
            int pyMax = Math.Min(h - 1, (int)((center.Y + scale.Y / 2f) * h));

            for (int py = pyMin; py <= pyMax; py++)
            {
                for (int px = pxMin; px <= pxMax; px++)
                {
                    float u = (px + 0.5f) / w;
                    float v = (py + 0.5f) / h;
                    float lu = (u - center.X) / scale.X;
                    float lv = (v - center.Y) / scale.Y;
                    float ru = lu * cosR + lv * sinR;
                    float rv = -lu * sinR + lv * cosR;
                    if (ru < -0.5f || ru > 0.5f || rv < -0.5f || rv > 0.5f) continue;
                    switch (layer.Clip)
                    {
                        case ClipMode.ClipLeft when ru < 0f: continue;
                        case ClipMode.ClipRight when ru >= 0f: continue;
                        case ClipMode.ClipTop when rv < 0f: continue;
                        case ClipMode.ClipBottom when rv >= 0f: continue;
                    }

                    float du = (ru + 0.5f) * decalW - 0.5f;
                    float dv = (rv + 0.5f) * decalH - 0.5f;
                    SampleBilinear(decalData, decalW, decalH, du, dv, out _, out _, out _, out float da);
                    da *= opacity;
                    if (da < 0.001f) continue;

                    float maskValue;
                    if (layer.FadeMask == LayerFadeMask.DirectionalGradient)
                        maskValue = ComputeDirectionalGradient(ru, rv, da,
                            layer.GradientAngleDeg, layer.GradientScale, layer.FadeMaskFalloff, layer.GradientOffset);
                    else if (layer.FadeMask == LayerFadeMask.ShapeOutline)
                    {
                        // Sample 8 neighbors at 1-pixel offset to detect alpha edges
                        float sum = 0; int cnt = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            float ndu = du + dx; float ndv = dv + dy;
                            SampleBilinear(decalData, decalW, decalH, ndu, ndv, out _, out _, out _, out float na);
                            sum += na * opacity; cnt++;
                        }
                        maskValue = ComputeShapeOutline(da, layer.FadeMaskFalloff, sum / cnt);
                    }
                    else
                        maskValue = ComputeFadeMaskWeight(layer.FadeMask, layer.FadeMaskFalloff, ru, rv, da);

                    int oIdx = (py * w + px) * 4;
                    byte emByte = (byte)Math.Clamp((int)(maskValue * 255), 0, 255);
                    output[oIdx + 3] = (byte)Math.Max(output[oIdx + 3], emByte);
                }
            }

            anyEmissive = true;
        }

        return anyEmissive ? output : null;
    }

    /// <summary>
    /// v1 PBR index map rewrite: per Penumbra MaterialExporter:136-137, character.shpk
    /// shaders read `tablePair = round(g_SamplerIndex.r / 17)` and `rowBlend = 1 - g/255`,
    /// then `lerp(table[tablePair*2], table[tablePair*2+1], rowBlend)`. We write:
    ///   index.R = rowPair * 17  (selects which row pair this pixel uses)
    ///   index.G = weight * 255  (weight=1 ⇒ G=255 ⇒ rowBlend=0 ⇒ reads layer override row)
    /// Vanilla B and A are preserved.
    /// </summary>
    private byte[]? CompositeIndexMap(List<DecalLayer> allocatedLayers, string indexDiskOrGamePath, int w, int h)
    {
        byte[] baseIndex;
        if (baseTextureCache.TryGetValue(indexDiskOrGamePath, out var cachedIdx))
        {
            var (data, iw, ih) = cachedIdx;
            baseIndex = (iw != w || ih != h) ? ResizeBilinear(data, iw, ih, w, h) : (byte[])data.Clone();
        }
        else
        {
            var indexImg = File.Exists(indexDiskOrGamePath)
                ? imageLoader.LoadImage(indexDiskOrGamePath)
                : LoadGameTexture(indexDiskOrGamePath);
            if (indexImg == null) return null;
            baseTextureCache[indexDiskOrGamePath] = indexImg.Value;
            var (data, iw, ih) = indexImg.Value;
            baseIndex = (iw != w || ih != h) ? ResizeBilinear(data, iw, ih, w, h) : (byte[])data.Clone();
        }

        var output = (byte[])baseIndex.Clone();
        bool anyWritten = false;

        // z-order: iterate layers front-to-back so later layers overwrite earlier ones
        foreach (var layer in allocatedLayers)
        {
            if (!layer.IsVisible) continue;
            if (layer.AllocatedRowPair < 0) continue;
            if (string.IsNullOrEmpty(layer.ImagePath)) continue;

            byte rowPairByte = (byte)Math.Clamp(layer.AllocatedRowPair * 17, 0, 255);
            var decalImage = imageLoader.LoadImage(layer.ImagePath);
            if (decalImage == null) continue;
            var (decalData, decalW, decalH) = decalImage.Value;

            var center = layer.UvCenter;
            var scale = layer.UvScale;
            var opacity = layer.Opacity;
            var rotRad = layer.RotationDeg * (MathF.PI / 180f);
            var cosR = MathF.Cos(rotRad);
            var sinR = MathF.Sin(rotRad);

            int pxMin = Math.Max(0, (int)((center.X - scale.X / 2f) * w));
            int pxMax = Math.Min(w - 1, (int)((center.X + scale.X / 2f) * w));
            int pyMin = Math.Max(0, (int)((center.Y - scale.Y / 2f) * h));
            int pyMax = Math.Min(h - 1, (int)((center.Y + scale.Y / 2f) * h));

            for (int py = pyMin; py <= pyMax; py++)
            {
                for (int px = pxMin; px <= pxMax; px++)
                {
                    float u = (px + 0.5f) / w;
                    float v = (py + 0.5f) / h;
                    float lu = (u - center.X) / scale.X;
                    float lv = (v - center.Y) / scale.Y;
                    float ru = lu * cosR + lv * sinR;
                    float rv = -lu * sinR + lv * cosR;
                    if (ru < -0.5f || ru > 0.5f || rv < -0.5f || rv > 0.5f) continue;
                    switch (layer.Clip)
                    {
                        case ClipMode.ClipLeft when ru < 0f: continue;
                        case ClipMode.ClipRight when ru >= 0f: continue;
                        case ClipMode.ClipTop when rv < 0f: continue;
                        case ClipMode.ClipBottom when rv >= 0f: continue;
                    }

                    float du = (ru + 0.5f) * decalW - 0.5f;
                    float dv = (rv + 0.5f) * decalH - 0.5f;
                    SampleBilinear(decalData, decalW, decalH, du, dv, out _, out _, out _, out float da);
                    da *= opacity;
                    if (da < 0.001f) continue;

                    float weight;
                    if (layer.FadeMask == LayerFadeMask.DirectionalGradient)
                        weight = ComputeDirectionalGradient(ru, rv, da,
                            layer.GradientAngleDeg, layer.GradientScale, layer.FadeMaskFalloff, layer.GradientOffset);
                    else if (layer.FadeMask == LayerFadeMask.ShapeOutline)
                    {
                        float sum = 0; int cnt = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            float ndu = du + dx; float ndv = dv + dy;
                            SampleBilinear(decalData, decalW, decalH, ndu, ndv, out _, out _, out _, out float na);
                            sum += na * opacity; cnt++;
                        }
                        weight = ComputeShapeOutline(da, layer.FadeMaskFalloff, sum / cnt);
                    }
                    else
                        weight = ComputeFadeMaskWeight(layer.FadeMask, layer.FadeMaskFalloff, ru, rv, da);

                    weight = Math.Clamp(weight, 0f, 1f);
                    if (weight <= 0.001f) continue;

                    int oIdx = (py * w + px) * 4;
                    output[oIdx + 0] = rowPairByte;                                  // .r = row pair * 17 (Penumbra MaterialExporter:136)
                    output[oIdx + 1] = (byte)Math.Clamp((int)(weight * 255), 0, 255); // .g = weight (rowBlend = 1 - g/255, :137)
                    // .b and .a left at vanilla values
                    anyWritten = true;
                }
            }
        }

        return anyWritten ? output : null;
    }

    private static Vector3 GetCombinedEmissiveColor(List<DecalLayer> layers)
    {
        var color = Vector3.Zero;
        foreach (var layer in layers)
        {
            if (!layer.IsVisible || !layer.AffectsEmissive || string.IsNullOrEmpty(layer.ImagePath)) continue;
            color += layer.EmissiveColor * layer.EmissiveIntensity;
        }
        return color;
    }

    private bool TryBuildEmissiveMtrl(string mtrlPath, string outputPath, Vector3 emissiveColor, out int emissiveByteOffset)
    {
        emissiveByteOffset = -1;
        try
        {
            byte[] mtrlBytes;
            MtrlFile mtrl;

            if (File.Exists(mtrlPath))
            {
                mtrlBytes = File.ReadAllBytes(mtrlPath);
            }
            else
            {
                var pack = meshExtractor.GetSqPackInstance();
                if (pack == null) return false;
                var sqResult = pack.GetFile(mtrlPath);
                if (sqResult == null) return false;
                mtrlBytes = sqResult.Value.file.RawData.ToArray();
            }

            // Unique temp file: avoid races between concurrent main/background callers
            var tempPath = Path.Combine(outputDir, $"temp_{Guid.NewGuid():N}.mtrl");
            File.WriteAllBytes(tempPath, mtrlBytes);
            var lumina = meshExtractor.GetLuminaForDisk();
            mtrl = lumina!.GetFileFromDisk<MtrlFile>(tempPath);
            try { File.Delete(tempPath); } catch { }

            return MtrlFileWriter.WriteEmissiveMtrl(mtrl, mtrlBytes, outputPath, emissiveColor, out emissiveByteOffset);
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PreviewService] Emissive mtrl build failed: {ex.Message}");
            return false;
        }
    }

    private (byte[] Data, int Width, int Height)? LoadGameTexture(string gamePath)
    {
        try
        {
            var pack = meshExtractor.GetSqPackInstance();
            if (pack == null) return null;
            var sqResult = pack.GetFile(gamePath);
            if (sqResult == null) return null;

            var rawBytes = sqResult.Value.file.RawData.ToArray();
            // Unique temp file: avoid races between concurrent main/background callers
            var tempPath = Path.Combine(outputDir, $"temp_{Guid.NewGuid():N}.tex");
            File.WriteAllBytes(tempPath, rawBytes);
            var result = imageLoader.LoadImage(tempPath);
            try { File.Delete(tempPath); } catch { }
            return result;
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PreviewService] Game texture load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Composite layers' PNG into a diffuse RGBA buffer.
    /// <paramref name="ignoreAffectsDiffuseFilter"/> = true: paint every visible layer with an
    /// image, regardless of AffectsDiffuse — used for the 3D editor preview where the user
    /// needs to see decal placement even when the layer doesn't actually paint the GPU diffuse.
    /// </summary>
    private byte[]? CpuUvComposite(List<DecalLayer> layers, byte[] baseRgba, int w, int h,
        bool ignoreAffectsDiffuseFilter = false)
    {
        var output = (byte[])baseRgba.Clone();
        int processedLayers = 0;

        foreach (var layer in layers)
        {
            if (!layer.IsVisible || string.IsNullOrEmpty(layer.ImagePath)) continue;
            if (!ignoreAffectsDiffuseFilter && !layer.AffectsDiffuse) continue;

            var decalImage = imageLoader.LoadImage(layer.ImagePath);
            if (decalImage == null) continue;

            var (decalData, decalW, decalH) = decalImage.Value;
            var center = layer.UvCenter;
            var scale = layer.UvScale;
            var opacity = layer.Opacity;
            var rotRad = layer.RotationDeg * (MathF.PI / 180f);
            var cosR = MathF.Cos(rotRad);
            var sinR = MathF.Sin(rotRad);

            int pxMin = Math.Max(0, (int)((center.X - scale.X / 2f) * w));
            int pxMax = Math.Min(w - 1, (int)((center.X + scale.X / 2f) * w));
            int pyMin = Math.Max(0, (int)((center.Y - scale.Y / 2f) * h));
            int pyMax = Math.Min(h - 1, (int)((center.Y + scale.Y / 2f) * h));

            for (int py = pyMin; py <= pyMax; py++)
            {
                for (int px = pxMin; px <= pxMax; px++)
                {
                    float u = (px + 0.5f) / w;
                    float v = (py + 0.5f) / h;
                    float lu = (u - center.X) / scale.X;
                    float lv = (v - center.Y) / scale.Y;
                    float ru = lu * cosR + lv * sinR;
                    float rv = -lu * sinR + lv * cosR;
                    if (ru < -0.5f || ru > 0.5f || rv < -0.5f || rv > 0.5f) continue;
                    switch (layer.Clip)
                    {
                        case ClipMode.ClipLeft when ru < 0f: continue;
                        case ClipMode.ClipRight when ru >= 0f: continue;
                        case ClipMode.ClipTop when rv < 0f: continue;
                        case ClipMode.ClipBottom when rv >= 0f: continue;
                    }

                    float du = (ru + 0.5f) * decalW - 0.5f;
                    float dv = (rv + 0.5f) * decalH - 0.5f;

                    SampleBilinear(decalData, decalW, decalH, du, dv,
                        out float dr, out float dg, out float db, out float da);
                    da *= opacity;

                    if (da < 0.001f) continue;

                    int oIdx = (py * w + px) * 4;
                    float br = output[oIdx] / 255f;
                    float bg = output[oIdx + 1] / 255f;
                    float bb = output[oIdx + 2] / 255f;

                    float rr, rg, rb;
                    switch (layer.BlendMode)
                    {
                        case BlendMode.Multiply:
                            rr = br * dr; rg = bg * dg; rb = bb * db;
                            break;
                        case BlendMode.Screen:
                            rr = 1f - (1f - br) * (1f - dr);
                            rg = 1f - (1f - bg) * (1f - dg);
                            rb = 1f - (1f - bb) * (1f - db);
                            break;
                        case BlendMode.Overlay:
                            rr = br < 0.5f ? 2f * br * dr : 1f - 2f * (1f - br) * (1f - dr);
                            rg = bg < 0.5f ? 2f * bg * dg : 1f - 2f * (1f - bg) * (1f - dg);
                            rb = bb < 0.5f ? 2f * bb * db : 1f - 2f * (1f - bb) * (1f - db);
                            break;
                        case BlendMode.SoftLight:
                            rr = (1f - 2f * dr) * br * br + 2f * dr * br;
                            rg = (1f - 2f * dg) * bg * bg + 2f * dg * bg;
                            rb = (1f - 2f * db) * bb * bb + 2f * db * bb;
                            break;
                        case BlendMode.HardLight:
                            rr = dr < 0.5f ? 2f * br * dr : 1f - 2f * (1f - br) * (1f - dr);
                            rg = dg < 0.5f ? 2f * bg * dg : 1f - 2f * (1f - bg) * (1f - dg);
                            rb = db < 0.5f ? 2f * bb * db : 1f - 2f * (1f - bb) * (1f - db);
                            break;
                        case BlendMode.Darken:
                            rr = Math.Min(br, dr); rg = Math.Min(bg, dg); rb = Math.Min(bb, db);
                            break;
                        case BlendMode.Lighten:
                            rr = Math.Max(br, dr); rg = Math.Max(bg, dg); rb = Math.Max(bb, db);
                            break;
                        case BlendMode.ColorDodge:
                            rr = dr >= 1f ? 1f : Math.Min(1f, br / (1f - dr));
                            rg = dg >= 1f ? 1f : Math.Min(1f, bg / (1f - dg));
                            rb = db >= 1f ? 1f : Math.Min(1f, bb / (1f - db));
                            break;
                        case BlendMode.ColorBurn:
                            rr = dr <= 0f ? 0f : Math.Max(0f, 1f - (1f - br) / dr);
                            rg = dg <= 0f ? 0f : Math.Max(0f, 1f - (1f - bg) / dg);
                            rb = db <= 0f ? 0f : Math.Max(0f, 1f - (1f - bb) / db);
                            break;
                        case BlendMode.Difference:
                            rr = Math.Abs(br - dr); rg = Math.Abs(bg - dg); rb = Math.Abs(bb - db);
                            break;
                        case BlendMode.Exclusion:
                            rr = br + dr - 2f * br * dr;
                            rg = bg + dg - 2f * bg * dg;
                            rb = bb + db - 2f * bb * db;
                            break;
                        default:
                            rr = dr; rg = dg; rb = db;
                            break;
                    }

                    output[oIdx]     = (byte)Math.Clamp((int)((rr * da + br * (1 - da)) * 255), 0, 255);
                    output[oIdx + 1] = (byte)Math.Clamp((int)((rg * da + bg * (1 - da)) * 255), 0, 255);
                    output[oIdx + 2] = (byte)Math.Clamp((int)((rb * da + bb * (1 - da)) * 255), 0, 255);
                    output[oIdx + 3] = 255;
                }
            }

            processedLayers++;
        }

        if (processedLayers == 0)
            return null;

        return output;
    }

    private static void WriteBgraTexFile(string path, byte[] rgbaData, int width, int height)
    {
        // Retry — game/Penumbra may hold a transient lock during redraw
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                TexFileWriter.WriteRgba(path, rgbaData, width, height);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
        }
    }

    private static void SampleBilinear(byte[] data, int w, int h, float fx, float fy,
        out float r, out float g, out float b, out float a)
    {
        int x0 = Math.Clamp((int)MathF.Floor(fx), 0, w - 1);
        int y0 = Math.Clamp((int)MathF.Floor(fy), 0, h - 1);
        int x1 = Math.Min(x0 + 1, w - 1);
        int y1 = Math.Min(y0 + 1, h - 1);
        float tx = fx - MathF.Floor(fx);
        float ty = fy - MathF.Floor(fy);

        int i00 = (y0 * w + x0) * 4;
        int i10 = (y0 * w + x1) * 4;
        int i01 = (y1 * w + x0) * 4;
        int i11 = (y1 * w + x1) * 4;

        float Lerp(int ch) =>
            (data[i00 + ch] * (1 - tx) + data[i10 + ch] * tx) * (1 - ty) +
            (data[i01 + ch] * (1 - tx) + data[i11 + ch] * tx) * ty;

        r = Lerp(0) / 255f;
        g = Lerp(1) / 255f;
        b = Lerp(2) / 255f;
        a = Lerp(3) / 255f;
    }

    /// <summary>
    /// Compute emissive mask value based on position within the decal.
    /// </summary>
    public static float ComputeFadeMaskWeight(LayerFadeMask mask, float falloff, float ru, float rv, float da)
    {
        if (mask == Core.LayerFadeMask.Uniform)
            return da;

        float dist = MathF.Sqrt(ru * ru + rv * rv) * 2f;
        dist = MathF.Min(dist, 1f);

        float edgeDist = MathF.Min(0.5f - MathF.Abs(ru), 0.5f - MathF.Abs(rv));
        edgeDist = MathF.Max(edgeDist, 0f) * 2f;

        float f = MathF.Max(falloff, 0.01f);
        float m;

        switch (mask)
        {
            case Core.LayerFadeMask.RadialFadeOut:
                m = 1f - Smoothstep(0f, f, dist);
                break;
            case Core.LayerFadeMask.RadialFadeIn:
                m = Smoothstep(1f - f, 1f, dist);
                break;
            case Core.LayerFadeMask.EdgeGlow:
                m = 1f - Smoothstep(0f, f, edgeDist);
                break;
            case Core.LayerFadeMask.GaussianFeather:
                float sigma = MathF.Max(f, 0.01f) * 0.5f;
                float gEdge = MathF.Min(0.5f - MathF.Abs(ru), 0.5f - MathF.Abs(rv));
                gEdge = MathF.Max(gEdge, 0f);
                m = 1f - MathF.Exp(-(gEdge * gEdge) / (2f * sigma * sigma));
                break;
            default:
                m = 1f;
                break;
        }

        return da * m;
    }

    /// <summary>Compute directional gradient mask with angle, scale and offset parameters.</summary>
    public static float ComputeDirectionalGradient(float ru, float rv, float da,
        float angleDeg, float scale, float falloff, float offset)
    {
        float rad = angleDeg * (MathF.PI / 180f);
        float cosA = MathF.Cos(rad);
        float sinA = MathF.Sin(rad);

        float projected = ru * cosA + rv * sinA;
        float s = MathF.Max(scale, 0.01f);
        float t = (projected / s + 0.5f + offset);
        t = MathF.Max(0f, MathF.Min(1f, t));

        float f = MathF.Max(falloff, 0.01f);
        float m = Smoothstep(0.5f - f * 0.5f, 0.5f + f * 0.5f, t);

        return da * m;
    }

    /// <summary>Compute shape outline mask — glow along decal alpha edges (like PS outer glow).</summary>
    public static float ComputeShapeOutline(float da, float falloff, float neighborAvgAlpha)
    {
        // Edge = where alpha differs from neighbors.
        // da > 0 and neighborAvgAlpha < da means we're near an edge inside the decal.
        // We want: glow strongest at decal alpha boundary, fading inward.
        float edgeStrength = MathF.Abs(da - neighborAvgAlpha);
        float f = MathF.Max(falloff, 0.01f);
        float m = Smoothstep(0f, f, edgeStrength);
        return da * m;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = MathF.Max(0f, MathF.Min(1f, (x - edge0) / (edge1 - edge0 + 1e-6f)));
        return t * t * (3f - 2f * t);
    }

    private static byte[] ResizeBilinear(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        for (var y = 0; y < dstH; y++)
        {
            float fy = (y + 0.5f) * srcH / dstH - 0.5f;
            for (var x = 0; x < dstW; x++)
            {
                float fx = (x + 0.5f) * srcW / dstW - 0.5f;
                SampleBilinear(src, srcW, srcH, fx, fy, out float r, out float g, out float b, out float a);
                var i = (y * dstW + x) * 4;
                dst[i]     = (byte)Math.Clamp((int)(r * 255 + 0.5f), 0, 255);
                dst[i + 1] = (byte)Math.Clamp((int)(g * 255 + 0.5f), 0, 255);
                dst[i + 2] = (byte)Math.Clamp((int)(b * 255 + 0.5f), 0, 255);
                dst[i + 3] = (byte)Math.Clamp((int)(a * 255 + 0.5f), 0, 255);
            }
        }
        return dst;
    }

    public void ClearTextureCache()
    {
        baseTextureCache.Clear();
        DebugServer.AppendLog("[PreviewService] Texture cache cleared");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        // Cancel any in-flight background composite so it can't write to cleared state
        try { asyncCancel?.Cancel(); } catch { }
        asyncCancel?.Dispose();
        asyncCancel = null;
        pendingBatch = null;

        baseTextureCache.Clear();
        compositeResults.Clear();
        previewDiskPaths.Clear();
        previewMtrlDiskPaths.Clear();
        emissiveOffsets.Clear();
        initializedRedirects.Clear();
        rowPairAllocators.Clear();
        vanillaColorTables.Clear();
        indexMapGamePaths.Clear();
        emissiveHook?.ClearTargets();
    }
}
