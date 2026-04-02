using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Meddle.Utils.Export;
using Meddle.Utils.Files;
using Meddle.Utils.Files.SqPack;
using SkinTatoo.Http;
using MeddleModel = Meddle.Utils.Export.Model;

namespace SkinTatoo.Mesh;

public class MeshExtractor
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private SqPack? sqPack;

    public MeshExtractor(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    private SqPack GetSqPack()
    {
        if (sqPack == null)
        {
            var sqpackPath = dataManager.GameData.DataPath.FullName;
            var gamePath = System.IO.Path.GetDirectoryName(sqpackPath)!;
            DebugServer.AppendLog($"[MeshExtractor] Initializing Meddle SqPack at: {gamePath}");
            sqPack = new SqPack(gamePath);
        }
        return sqPack;
    }

    public MeshData? ExtractMesh(string gamePath)
    {
        DebugServer.AppendLog($"[MeshExtractor] Loading: {gamePath}");

        byte[]? mdlBytes = null;

        // Use Meddle's SqPack reader
        try
        {
            var pack = GetSqPack();

            // Diagnostic: check file existence with both hash types
            var exists = pack.FileExists(gamePath, out var hash);
            DebugServer.AppendLog($"[MeshExtractor] Meddle FileExists={exists}, IndexHash={hash.IndexHash:X16}, Index2Hash={hash.Index2Hash:X16}");
            DebugServer.AppendLog($"[MeshExtractor] Repositories: {pack.Repositories.Count}");
            foreach (var repo in pack.Repositories)
            {
                var cats = repo.Categories.Where(c => c.Key.category == 4).ToList();
                DebugServer.AppendLog($"[MeshExtractor] Repo categories with id=4: {cats.Count}");
                foreach (var (key, cat) in cats)
                {
                    DebugServer.AppendLog($"[MeshExtractor]   cat=({key.category},{key.expansion},{key.chunk}), entries={cat.UnifiedIndexEntries.Count}");
                }
            }

            var result = pack.GetFile(gamePath);
            if (result != null)
            {
                mdlBytes = result.Value.file.RawData.ToArray();
                DebugServer.AppendLog($"[MeshExtractor] Loaded via Meddle SqPack: {mdlBytes.Length} bytes");
            }
            else
            {
                DebugServer.AppendLog($"[MeshExtractor] Meddle SqPack: file not found in any repo/category");
            }
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[MeshExtractor] Meddle SqPack exception: {ex.GetType().Name}: {ex.Message}");
        }

        // Fallback: try reading as local file path (for Penumbra mod redirects)
        if (mdlBytes == null && System.IO.File.Exists(gamePath))
        {
            try
            {
                mdlBytes = System.IO.File.ReadAllBytes(gamePath);
                DebugServer.AppendLog($"[MeshExtractor] Loaded from local file: {mdlBytes.Length} bytes");
            }
            catch (Exception ex)
            {
                DebugServer.AppendLog($"[MeshExtractor] Local file read exception: {ex.Message}");
            }
        }

        // Fallback: Dalamud IDataManager
        if (mdlBytes == null)
        {
            try
            {
                var raw = dataManager.GetFile(gamePath);
                if (raw != null)
                {
                    mdlBytes = raw.Data;
                    DebugServer.AppendLog($"[MeshExtractor] Loaded via IDataManager: {mdlBytes.Length} bytes");
                }
            }
            catch (Exception ex)
            {
                DebugServer.AppendLog($"[MeshExtractor] IDataManager fallback exception: {ex.Message}");
            }
        }

        if (mdlBytes == null)
        {
            DebugServer.AppendLog($"[MeshExtractor] Failed to load: {gamePath}");
            return null;
        }

        try
        {
            var mdlFile = new MdlFile(mdlBytes);
            DebugServer.AppendLog($"[MeshExtractor] MdlFile parsed: {mdlFile.Meshes.Length} meshes, {mdlFile.Lods.Length} LODs");

            // Use Meddle's Model class to extract vertices properly
            var model = new MeddleModel(gamePath, mdlFile, null);
            DebugServer.AppendLog($"[MeshExtractor] Model created: {model.Meshes.Count} export meshes");

            return ConvertMeddleModel(model);
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[MeshExtractor] Parse error: {ex.GetType().Name}: {ex.Message}");
            log.Error(ex, "Failed to parse model: {0}", gamePath);
            return null;
        }
    }

    private static MeshData ConvertMeddleModel(MeddleModel model)
    {
        var allVertices = new List<MeshVertex>();
        var allIndices = new List<ushort>();

        foreach (var mesh in model.Meshes)
        {
            var baseVertex = (ushort)allVertices.Count;

            foreach (var v in mesh.Vertices)
            {
                var pos = v.Position ?? Vector3.Zero;
                var normal = (v.Normals != null && v.Normals.Length > 0) ? v.Normals[0] : Vector3.UnitY;
                var uv = (v.TexCoords != null && v.TexCoords.Length > 0) ? v.TexCoords[0] : Vector2.Zero;

                allVertices.Add(new MeshVertex
                {
                    Position = pos,
                    Normal = normal,
                    UV = uv,
                });
            }

            foreach (var idx in mesh.Indices)
            {
                allIndices.Add((ushort)(idx + baseVertex));
            }
        }

        var result = new MeshData
        {
            Vertices = allVertices.ToArray(),
            Indices = allIndices.ToArray(),
        };

        DebugServer.AppendLog($"[MeshExtractor] Total: {result.Vertices.Length} verts, {result.TriangleCount} tris");
        return result;
    }
}
