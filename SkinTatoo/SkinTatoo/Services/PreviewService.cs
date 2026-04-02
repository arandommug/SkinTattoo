using System;
using System.IO;
using Dalamud.Plugin.Services;
using SkinTatoo.Core;
using SkinTatoo.Gpu;
using SkinTatoo.Http;
using SkinTatoo.Interop;
using SkinTatoo.Mesh;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11_BIND_FLAG;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace SkinTatoo.Services;

public unsafe class PreviewService : IDisposable
{
    private readonly DxManager dx;
    private readonly ComputeShaderPipeline pipeline;
    private readonly StagingReadback readback;
    private readonly MeshExtractor meshExtractor;
    private readonly DecalImageLoader imageLoader;
    private readonly PenumbraBridge penumbra;
    private readonly IPluginLog log;
    private readonly Configuration config;

    private ID3D11Texture2D* positionMapTex;
    private ID3D11ShaderResourceView* positionMapSRV;
    private ID3D11Texture2D* normalMapTex;
    private ID3D11ShaderResourceView* normalMapSRV;
    private uint mapWidth;
    private uint mapHeight;

    private MeshData? currentMesh;
    private readonly string outputDir;
    private bool disposed;

    public bool IsReady => dx.IsInitialized && pipeline.IsInitialized && currentMesh != null;
    public MeshData? CurrentMesh => currentMesh;

    public PreviewService(
        DxManager dx,
        ComputeShaderPipeline pipeline,
        StagingReadback readback,
        MeshExtractor meshExtractor,
        DecalImageLoader imageLoader,
        PenumbraBridge penumbra,
        IPluginLog log,
        Configuration config,
        string outputDir)
    {
        this.dx = dx;
        this.pipeline = pipeline;
        this.readback = readback;
        this.meshExtractor = meshExtractor;
        this.imageLoader = imageLoader;
        this.penumbra = penumbra;
        this.log = log;
        this.config = config;
        this.outputDir = outputDir;

        Directory.CreateDirectory(outputDir);
    }

    // Synchronous — must be called from a thread that can access Dalamud IDataManager
    public bool LoadMesh(string gameMdlPath)
    {
        DebugServer.AppendLog($"[PreviewService] LoadMesh: {gameMdlPath}");

        if (!dx.IsInitialized || !pipeline.IsInitialized)
        {
            var msg = "LoadMesh failed: GPU not ready";
            log.Error(msg);
            DebugServer.AppendLog($"[PreviewService] {msg}");
            return false;
        }

        MeshData? meshData;
        try
        {
            meshData = meshExtractor.ExtractMesh(gameMdlPath);
        }
        catch (Exception ex)
        {
            var msg = $"LoadMesh exception: {ex.Message}";
            log.Error(ex, msg);
            DebugServer.AppendLog($"[PreviewService] {msg}");
            return false;
        }

        if (meshData == null)
        {
            var msg = $"LoadMesh failed: ExtractMesh returned null for {gameMdlPath}";
            log.Error(msg);
            DebugServer.AppendLog($"[PreviewService] {msg}");
            return false;
        }

        var res = (uint)Math.Clamp(config.TextureResolution, 256, 4096);
        mapWidth = res;
        mapHeight = res;

        var gen = new PositionMapGenerator((int)res, (int)res);
        gen.Generate(meshData);

        FreeMapResources();

        positionMapTex = dx.CreateTexture2D(mapWidth, mapHeight,
            DXGI_FORMAT_R32G32B32A32_FLOAT, (uint)D3D11_BIND_SHADER_RESOURCE);
        if (positionMapTex == null) return false;
        dx.UploadTextureData(positionMapTex, gen.PositionMap, mapWidth, mapHeight);
        positionMapSRV = dx.CreateSRV(positionMapTex, DXGI_FORMAT_R32G32B32A32_FLOAT);

        normalMapTex = dx.CreateTexture2D(mapWidth, mapHeight,
            DXGI_FORMAT_R32G32B32A32_FLOAT, (uint)D3D11_BIND_SHADER_RESOURCE);
        if (normalMapTex == null) return false;
        dx.UploadTextureData(normalMapTex, gen.NormalMap, mapWidth, mapHeight);
        normalMapSRV = dx.CreateSRV(normalMapTex, DXGI_FORMAT_R32G32B32A32_FLOAT);

        currentMesh = meshData;

        var info = $"Mesh loaded: {meshData.Vertices.Length} verts, {meshData.TriangleCount} tris, {res}x{res} maps";
        log.Information(info);
        DebugServer.AppendLog($"[PreviewService] {info}");
        return true;
    }

    // Synchronous preview update
    public string? UpdatePreview(DecalProject project, string? gameTexturePath)
    {
        DebugServer.AppendLog($"[PreviewService] UpdatePreview: texPath={gameTexturePath}, layers={project.Layers.Count}");

        if (!IsReady)
        {
            DebugServer.AppendLog("[PreviewService] UpdatePreview failed: not ready (mesh not loaded)");
            return null;
        }

        ID3D11Texture2D* accumTex = CreateFloatRWTexture();
        if (accumTex == null) return null;
        ID3D11ShaderResourceView* accumSRV = dx.CreateSRV(accumTex, DXGI_FORMAT_R32G32B32A32_FLOAT);
        ID3D11UnorderedAccessView* accumUAV = dx.CreateUAV(accumTex, DXGI_FORMAT_R32G32B32A32_FLOAT);

        try
        {
            var processedLayers = 0;
            foreach (var layer in project.Layers)
            {
                if (!layer.IsVisible || string.IsNullOrEmpty(layer.ImagePath)) continue;
                ProcessLayer(layer, ref accumTex, ref accumSRV, ref accumUAV);
                processedLayers++;
            }

            if (processedLayers == 0)
            {
                DebugServer.AppendLog("[PreviewService] UpdatePreview: no visible layers with images");
                return null;
            }

            var rawData = readback.Readback(accumTex, mapWidth, mapHeight);
            if (rawData == null)
            {
                DebugServer.AppendLog("[PreviewService] UpdatePreview: GPU readback failed");
                return null;
            }

            var localPath = Path.Combine(outputDir, "preview.tex");
            TexFileWriter.WriteUncompressed(localPath, rawData, (int)mapWidth, (int)mapHeight);

            if (!string.IsNullOrEmpty(gameTexturePath))
            {
                penumbra.SetTextureRedirect(gameTexturePath, localPath);
                penumbra.RedrawPlayer();
            }

            DebugServer.AppendLog($"[PreviewService] Preview written: {localPath}");
            return localPath;
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PreviewService] UpdatePreview exception: {ex.Message}");
            log.Error(ex, "UpdatePreview failed");
            return null;
        }
        finally
        {
            if (accumUAV != null) accumUAV->Release();
            if (accumSRV != null) accumSRV->Release();
            if (accumTex != null) accumTex->Release();
        }
    }

    private void ProcessLayer(
        DecalLayer layer,
        ref ID3D11Texture2D* accumTex,
        ref ID3D11ShaderResourceView* accumSRV,
        ref ID3D11UnorderedAccessView* accumUAV)
    {
        var imageResult = imageLoader.LoadImage(layer.ImagePath!);
        if (imageResult == null)
        {
            DebugServer.AppendLog($"[PreviewService] Cannot load image: {layer.ImagePath}");
            return;
        }

        var (imgData, imgW, imgH) = imageResult.Value;
        DebugServer.AppendLog($"[PreviewService] Processing layer '{layer.Name}': {imgW}x{imgH} image");
        var floatData = ConvertRgba8ToFloat4(imgData, imgW, imgH);

        ID3D11Texture2D* decalTex = dx.CreateTexture2D(
            (uint)imgW, (uint)imgH,
            DXGI_FORMAT_R32G32B32A32_FLOAT,
            (uint)D3D11_BIND_SHADER_RESOURCE);
        if (decalTex == null) return;

        dx.UploadTextureData(decalTex, floatData, (uint)imgW, (uint)imgH);
        ID3D11ShaderResourceView* decalSRV = dx.CreateSRV(decalTex, DXGI_FORMAT_R32G32B32A32_FLOAT);

        ID3D11Texture2D* projBufTex = CreateFloatRWTexture();
        ID3D11UnorderedAccessView* projBufUAV = projBufTex != null
            ? dx.CreateUAV(projBufTex, DXGI_FORMAT_R32G32B32A32_FLOAT) : null;
        ID3D11ShaderResourceView* projBufSRV = projBufTex != null
            ? dx.CreateSRV(projBufTex, DXGI_FORMAT_R32G32B32A32_FLOAT) : null;

        ID3D11Texture2D* dilatedTex = CreateFloatRWTexture();
        ID3D11UnorderedAccessView* dilatedUAV = dilatedTex != null
            ? dx.CreateUAV(dilatedTex, DXGI_FORMAT_R32G32B32A32_FLOAT) : null;
        ID3D11ShaderResourceView* dilatedSRV = dilatedTex != null
            ? dx.CreateSRV(dilatedTex, DXGI_FORMAT_R32G32B32A32_FLOAT) : null;

        ID3D11Texture2D* compositedTex = null;
        ID3D11UnorderedAccessView* compositedUAV = null;
        ID3D11ShaderResourceView* compositedSRV = null;

        try
        {
            if (projBufUAV == null || projBufSRV == null || dilatedUAV == null || dilatedSRV == null)
                return;

            var projCB = new ProjectionCB
            {
                ViewProjection = layer.GetProjectionMatrix(),
                ProjectionDir = layer.GetForwardDirection(),
                BackfaceThreshold = layer.BackfaceCullingThreshold,
                GrazingFade = layer.GrazingAngleFade,
                Opacity = layer.Opacity,
            };
            pipeline.DispatchProjection(positionMapSRV, normalMapSRV, decalSRV,
                projBufUAV, projCB, mapWidth, mapHeight);

            pipeline.DispatchDilation(projBufSRV, projBufSRV, dilatedUAV, mapWidth, mapHeight);

            compositedTex = CreateFloatRWTexture();
            if (compositedTex == null) return;
            compositedUAV = dx.CreateUAV(compositedTex, DXGI_FORMAT_R32G32B32A32_FLOAT);
            compositedSRV = dx.CreateSRV(compositedTex, DXGI_FORMAT_R32G32B32A32_FLOAT);
            if (compositedUAV == null) return;

            var compCB = new CompositeCB
            {
                BlendMode = (uint)layer.BlendMode,
                IsNormalMap = layer.AffectsNormal ? 1u : 0u,
            };
            pipeline.DispatchComposite(accumSRV, dilatedSRV, compositedUAV, compCB, mapWidth, mapHeight);

            accumUAV->Release();
            accumSRV->Release();
            accumTex->Release();

            accumTex = compositedTex;
            accumSRV = compositedSRV!;
            accumUAV = compositedUAV!;

            compositedTex = null;
            compositedSRV = null;
            compositedUAV = null;
        }
        finally
        {
            if (projBufUAV != null) projBufUAV->Release();
            if (projBufSRV != null) projBufSRV->Release();
            if (projBufTex != null) projBufTex->Release();
            if (dilatedUAV != null) dilatedUAV->Release();
            if (dilatedSRV != null) dilatedSRV->Release();
            if (dilatedTex != null) dilatedTex->Release();
            if (compositedUAV != null) compositedUAV->Release();
            if (compositedSRV != null) compositedSRV->Release();
            if (compositedTex != null) compositedTex->Release();
            if (decalSRV != null) decalSRV->Release();
            if (decalTex != null) decalTex->Release();
        }
    }

    private ID3D11Texture2D* CreateFloatRWTexture()
    {
        return dx.CreateTexture2D(mapWidth, mapHeight,
            DXGI_FORMAT_R32G32B32A32_FLOAT,
            (uint)(D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS));
    }

    private static float[] ConvertRgba8ToFloat4(byte[] data, int w, int h)
    {
        var result = new float[w * h * 4];
        const float inv255 = 1.0f / 255.0f;
        for (var i = 0; i < result.Length; i++)
            result[i] = data[i] * inv255;
        return result;
    }

    private void FreeMapResources()
    {
        if (positionMapSRV != null) { positionMapSRV->Release(); positionMapSRV = null; }
        if (positionMapTex != null) { positionMapTex->Release(); positionMapTex = null; }
        if (normalMapSRV != null) { normalMapSRV->Release(); normalMapSRV = null; }
        if (normalMapTex != null) { normalMapTex->Release(); normalMapTex = null; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        FreeMapResources();
    }
}
