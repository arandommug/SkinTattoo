using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
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
    private readonly IPluginLog log;
    private readonly Configuration config;

    private MeshData? currentMesh;
    private readonly string outputDir;
    private bool disposed;

    public MeshData? CurrentMesh => currentMesh;

    public PreviewService(
        MeshExtractor meshExtractor,
        DecalImageLoader imageLoader,
        PenumbraBridge penumbra,
        IPluginLog log,
        Configuration config,
        string outputDir)
    {
        this.meshExtractor = meshExtractor;
        this.imageLoader = imageLoader;
        this.penumbra = penumbra;
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

    /// <summary>Update preview for all target groups in the project.</summary>
    public void UpdatePreview(DecalProject project)
    {
        DebugServer.AppendLog($"[PreviewService] UpdatePreview: {project.Groups.Count} groups");

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
            DebugServer.AppendLog($"[PreviewService] Preview updated ({redirects.Count} redirects)");
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PreviewService] UpdatePreview exception: {ex.Message}");
            log.Error(ex, "UpdatePreview failed");
        }
    }

    private void ProcessGroup(TargetGroup group, Dictionary<string, string> redirects)
    {
        var diffuseDisk = group.OrigDiffuseDiskPath ?? group.DiffuseDiskPath;
        var baseTex = LoadTexture(diffuseDisk);
        int w, h;
        byte[] baseData;

        if (baseTex != null)
        {
            (baseData, w, h) = baseTex.Value;
        }
        else
        {
            w = h = config.TextureResolution;
            baseData = new byte[w * h * 4];
        }

        // Diffuse composite
        var diffResult = CpuUvComposite(group.Layers, baseData, w, h);
        if (diffResult != null)
        {
            var safeName = MakeSafeFileName(group.Name);
            var path = Path.Combine(outputDir, $"preview_{safeName}_d.tex");
            WriteBgraTexFile(path, diffResult, w, h);
            redirects[group.DiffuseGamePath!] = path;
            DebugServer.AppendLog($"[PreviewService] Diffuse → {group.DiffuseGamePath}");
        }

        // Emissive: modify .mtrl + write emissive area into normal map alpha
        var hasEmissive = group.HasEmissiveLayers();
        if (hasEmissive && !string.IsNullOrEmpty(group.MtrlGamePath))
        {
            var emissiveColor = GetCombinedEmissiveColor(group.Layers);
            var safeName = MakeSafeFileName(group.Name);
            var mtrlOutPath = Path.Combine(outputDir, $"preview_{safeName}.mtrl");
            var mtrlDisk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;

            if (TryBuildEmissiveMtrl(mtrlDisk ?? group.MtrlGamePath!, mtrlOutPath, emissiveColor))
            {
                redirects[group.MtrlGamePath!] = mtrlOutPath;
                DebugServer.AppendLog($"[PreviewService] Mtrl (emissive) → {group.MtrlGamePath}");
            }

            // Write emissive area into normal map alpha channel
            if (!string.IsNullOrEmpty(group.NormGamePath) && !string.IsNullOrEmpty(group.NormDiskPath))
            {
                var normDisk = group.OrigNormDiskPath ?? group.NormDiskPath;
                var normResult = CompositeEmissiveNorm(group.Layers, normDisk!, w, h);
                if (normResult != null)
                {
                    var normPath = Path.Combine(outputDir, $"preview_{safeName}_n.tex");
                    WriteBgraTexFile(normPath, normResult, w, h);
                    redirects[group.NormGamePath!] = normPath;
                    DebugServer.AppendLog($"[PreviewService] Norm (emissive alpha) → {group.NormGamePath}");
                }
            }
        }
    }

    private static string MakeSafeFileName(string name)
    {
        var safe = name.Replace(' ', '_').Replace('/', '_').Replace('\\', '_');
        if (safe.Length > 40) safe = safe[..40];
        return string.IsNullOrEmpty(safe) ? "group" : safe;
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
        var normImg = File.Exists(normDiskPath) ? imageLoader.LoadImage(normDiskPath) : LoadGameTexture(normDiskPath);
        if (normImg != null)
        {
            var (data, iw, ih) = normImg.Value;
            baseNorm = (iw != w || ih != h) ? ResizeBilinear(data, iw, ih, w, h) : (byte[])data.Clone();
        }
        else
        {
            baseNorm = new byte[w * h * 4];
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

                    float du = (ru + 0.5f) * decalW - 0.5f;
                    float dv = (rv + 0.5f) * decalH - 0.5f;
                    SampleBilinear(decalData, decalW, decalH, du, dv, out _, out _, out _, out float da);
                    da *= opacity;
                    if (da < 0.001f) continue;

                    float maskValue = ComputeEmissiveMask(layer.EmissiveMask, layer.EmissiveMaskFalloff, ru, rv, da);

                    int oIdx = (py * w + px) * 4;
                    byte emByte = (byte)Math.Clamp((int)(maskValue * 255), 0, 255);
                    output[oIdx + 3] = (byte)Math.Max(output[oIdx + 3], emByte);
                }
            }

            anyEmissive = true;
        }

        return anyEmissive ? output : null;
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

    private bool TryBuildEmissiveMtrl(string mtrlPath, string outputPath, Vector3 emissiveColor)
    {
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

            var tempPath = Path.Combine(outputDir, "temp_orig.mtrl");
            File.WriteAllBytes(tempPath, mtrlBytes);
            var lumina = meshExtractor.GetLuminaForDisk();
            mtrl = lumina!.GetFileFromDisk<MtrlFile>(tempPath);
            try { File.Delete(tempPath); } catch { }

            DebugServer.AppendLog($"[PreviewService] Loaded mtrl: {mtrlPath} ({mtrlBytes.Length} bytes)");
            return MtrlFileWriter.WriteEmissiveMtrl(mtrl, mtrlBytes, outputPath, emissiveColor);
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
            var tempPath = Path.Combine(outputDir, "temp_base.tex");
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

    private byte[]? CpuUvComposite(List<DecalLayer> layers, byte[] baseRgba, int w, int h)
    {
        var output = (byte[])baseRgba.Clone();
        int processedLayers = 0;

        foreach (var layer in layers)
        {
            if (!layer.IsVisible || string.IsNullOrEmpty(layer.ImagePath)) continue;
            if (!layer.AffectsDiffuse) continue;

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

            int hitPixels = 0;

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

                    output[oIdx]     = (byte)Math.Clamp((int)((dr * da + br * (1 - da)) * 255), 0, 255);
                    output[oIdx + 1] = (byte)Math.Clamp((int)((dg * da + bg * (1 - da)) * 255), 0, 255);
                    output[oIdx + 2] = (byte)Math.Clamp((int)((db * da + bb * (1 - da)) * 255), 0, 255);
                    output[oIdx + 3] = 255;

                    hitPixels++;
                }
            }

            DebugServer.AppendLog($"[PreviewService] UV composite: layer '{layer.Name}' hit {hitPixels} pixels");
            processedLayers++;
        }

        if (processedLayers == 0)
        {
            DebugServer.AppendLog("[PreviewService] No visible layers with images");
            return null;
        }

        return output;
    }

    private static void WriteBgraTexFile(string path, byte[] rgbaData, int width, int height)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs);

        bw.Write(0x00800000u);
        bw.Write(0x1450u);
        bw.Write((ushort)width);
        bw.Write((ushort)height);
        bw.Write((ushort)1);
        bw.Write((ushort)1);
        bw.Write(0u); bw.Write(0u); bw.Write(0u);
        bw.Write(80u);
        for (int i = 1; i < 13; i++) bw.Write(0u);

        for (int i = 0; i < rgbaData.Length; i += 4)
        {
            bw.Write(rgbaData[i + 2]); // B
            bw.Write(rgbaData[i + 1]); // G
            bw.Write(rgbaData[i + 0]); // R
            bw.Write(rgbaData[i + 3]); // A
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
    public static float ComputeEmissiveMask(EmissiveMask mask, float falloff, float ru, float rv, float da)
    {
        if (mask == Core.EmissiveMask.Uniform)
            return da;

        float dist = MathF.Sqrt(ru * ru + rv * rv) * 2f;
        dist = MathF.Min(dist, 1f);

        float edgeDist = MathF.Min(0.5f - MathF.Abs(ru), 0.5f - MathF.Abs(rv));
        edgeDist = MathF.Max(edgeDist, 0f) * 2f;

        float f = MathF.Max(falloff, 0.01f);
        float m;

        switch (mask)
        {
            case Core.EmissiveMask.RadialFadeOut:
                m = 1f - Smoothstep(0f, f, dist);
                break;
            case Core.EmissiveMask.RadialFadeIn:
                m = Smoothstep(1f - f, 1f, dist);
                break;
            case Core.EmissiveMask.EdgeGlow:
                m = 1f - Smoothstep(0f, f, edgeDist);
                break;
            default:
                m = 1f;
                break;
        }

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
        DebugServer.AppendLog("[PreviewService] Texture cache cleared");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
    }
}
