using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Lumina.Data.Files;
using Lumina.Data.Parsing;
using SkinTattoo.Http;

namespace SkinTattoo.Services;

/// <summary>Modifies shader keys and constants for emissive glow, rebuilds binary .mtrl.</summary>
public static class MtrlFileWriter
{
    private const uint CategorySkinType = 0x380CAED0;
    private const uint ValueEmissive = 0x72E697CD;
    private const uint ConstantEmissiveColor = 0x38A64362;

    /// <summary>Write .mtrl with emissive enabled. Returns g_EmissiveColor byte offset.</summary>
    public static bool WriteEmissiveMtrl(MtrlFile mtrl, byte[] originalBytes, string outputPath,
        Vector3 emissiveColor, out int emissiveByteOffset)
    {
        emissiveByteOffset = -1;
        try
        {
            // Lumina's MtrlFile parser can't handle Dawntrail ColorTable (2048 bytes)
            // — it only reads 544 bytes then parses MaterialHeader from the wrong
            // offset, producing garbage for shader keys / constants / samplers.
            // Detect this case and use raw-byte patching instead.
            if (TryPatchEmissiveRaw(originalBytes, outputPath, emissiveColor, out emissiveByteOffset))
                return true;

            // Fallback: Lumina parsed correctly — use the structured rebuild path.
            return WriteEmissiveMtrlViaLumina(mtrl, originalBytes, outputPath, emissiveColor, out emissiveByteOffset);
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[MtrlWriter] Error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fast path: parse the raw .mtrl bytes directly, find g_EmissiveColor, patch
    /// its float values in-place, and write the result. No Lumina dependency.
    /// Returns false if g_EmissiveColor is not found (caller should use Lumina path).
    /// </summary>
    private static bool TryPatchEmissiveRaw(byte[] src, string outputPath,
        Vector3 color, out int emissiveByteOffset)
    {
        emissiveByteOffset = -1;
        if (src.Length < 16) return false;

        // ── Parse FileHeader ──
        int dataSetSize = BitConverter.ToUInt16(src, 6);
        int stringTableSize = BitConverter.ToUInt16(src, 8);
        int texCount = src[12];
        int uvCount = src[13];
        int colorCount = src[14];
        int addlDataSize = src[15];

        // ── Locate MaterialHeader ──
        int matHeaderOff = 16
            + texCount * 4
            + uvCount * 4
            + colorCount * 4
            + stringTableSize
            + addlDataSize
            + dataSetSize;

        if (matHeaderOff + 12 > src.Length) return false;

        int shaderValueListSize = BitConverter.ToUInt16(src, matHeaderOff);
        int shaderKeyCount = BitConverter.ToUInt16(src, matHeaderOff + 2);
        int constantCount = BitConverter.ToUInt16(src, matHeaderOff + 4);
        int samplerCount = BitConverter.ToUInt16(src, matHeaderOff + 6);

        // ── Locate Constants array ──
        int constantsOff = matHeaderOff + 12 + shaderKeyCount * 8;
        // Each constant: uint32 ID + uint16 ValueOffset + uint16 ValueSize = 8 bytes
        int samplersOff = constantsOff + constantCount * 8;
        int shaderValuesOff = samplersOff + samplerCount * 12;

        // ── Find g_EmissiveColor constant ──
        for (int i = 0; i < constantCount; i++)
        {
            int off = constantsOff + i * 8;
            if (off + 8 > src.Length) break;
            uint id = BitConverter.ToUInt32(src, off);
            if (id != ConstantEmissiveColor) continue;

            int valueOffset = BitConverter.ToUInt16(src, off + 4);
            int valueSize = BitConverter.ToUInt16(src, off + 6);
            if (valueSize < 12) break;

            // Absolute byte offset of the 3 floats in the file
            int absOff = shaderValuesOff + valueOffset;
            if (absOff + 12 > src.Length) break;

            emissiveByteOffset = valueOffset;

            // Patch in-place on a copy
            var patched = (byte[])src.Clone();
            BitConverter.TryWriteBytes(new Span<byte>(patched, absOff, 4), color.X);
            BitConverter.TryWriteBytes(new Span<byte>(patched, absOff + 4, 4), color.Y);
            BitConverter.TryWriteBytes(new Span<byte>(patched, absOff + 8, 4), color.Z);

            File.WriteAllBytes(outputPath, patched);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Lumina-based rebuild path: used when the material doesn't already have
    /// g_EmissiveColor (vanilla materials). Adds the emissive shader key and constant.
    /// </summary>
    private static bool WriteEmissiveMtrlViaLumina(MtrlFile mtrl, byte[] originalBytes,
        string outputPath, Vector3 emissiveColor, out int emissiveByteOffset)
    {
        emissiveByteOffset = -1;

        // Lumina skips AdditionalData — extract from raw bytes
        int addlDataOffset = 16
            + mtrl.FileHeader.TextureCount * 4
            + mtrl.FileHeader.UvSetCount * 4
            + mtrl.FileHeader.ColorSetCount * 4
            + mtrl.FileHeader.StringTableSize;
        int addlDataSize = mtrl.FileHeader.AdditionalDataSize;
        byte[] additionalData = new byte[addlDataSize];
        if (addlDataSize > 0 && addlDataOffset + addlDataSize <= originalBytes.Length)
            Array.Copy(originalBytes, addlDataOffset, additionalData, 0, addlDataSize);

        var shaderKeys = (ShaderKey[])mtrl.ShaderKeys.Clone();
        var constants = new List<Constant>(mtrl.Constants);
        var shaderValues = new List<float>(mtrl.ShaderValues);

        // Check if g_EmissiveColor already exists (shouldn't reach here if
        // TryPatchEmissiveRaw succeeded, but handle gracefully).
        bool foundEmissive = false;
        for (int i = 0; i < constants.Count; i++)
        {
            if (constants[i].ConstantId == ConstantEmissiveColor)
            {
                emissiveByteOffset = constants[i].ValueOffset;
                int floatOffset = emissiveByteOffset / 4;
                shaderValues[floatOffset] = emissiveColor.X;
                shaderValues[floatOffset + 1] = emissiveColor.Y;
                shaderValues[floatOffset + 2] = emissiveColor.Z;
                foundEmissive = true;
                break;
            }
        }

        if (!foundEmissive)
        {
            // Activate emissive shader variant
            bool foundSkinType = false;
            for (int i = 0; i < shaderKeys.Length; i++)
            {
                if (shaderKeys[i].Category == CategorySkinType)
                {
                    shaderKeys[i].Value = ValueEmissive;
                    foundSkinType = true;
                    break;
                }
            }
            if (!foundSkinType)
            {
                var keyList = new List<ShaderKey>(shaderKeys);
                keyList.Add(new ShaderKey { Category = CategorySkinType, Value = ValueEmissive });
                shaderKeys = keyList.ToArray();
            }

            emissiveByteOffset = shaderValues.Count * 4;
            constants.Add(new Constant
            {
                ConstantId = ConstantEmissiveColor,
                ValueOffset = (ushort)emissiveByteOffset,
                ValueSize = 12,
            });
            shaderValues.Add(emissiveColor.X);
            shaderValues.Add(emissiveColor.Y);
            shaderValues.Add(emissiveColor.Z);
        }

        // Extract raw ColorTable data (supports Dawntrail 2048-byte format).
        // Strip ColorTable for skin.shpk when we had to add the emissive key,
        // because the emissive variant is incompatible with non-standard ColorTable.
        byte[] colorTableData = Array.Empty<byte>();
        int dataSetSize = mtrl.FileHeader.DataSetSize;
        bool stripColorTable = false;
        if (!foundEmissive && dataSetSize > 0)
        {
            int so = mtrl.FileHeader.ShaderPackageNameOffset;
            if (so < mtrl.Strings.Length)
            {
                int end = so;
                while (end < mtrl.Strings.Length && mtrl.Strings[end] != 0) end++;
                var shpkName = System.Text.Encoding.UTF8.GetString(mtrl.Strings, so, end - so);
                stripColorTable = shpkName == "skin.shpk";
            }
        }
        if (dataSetSize > 0 && !stripColorTable)
        {
            int colorDataOffset = 16
                + mtrl.FileHeader.TextureCount * 4
                + mtrl.FileHeader.UvSetCount * 4
                + mtrl.FileHeader.ColorSetCount * 4
                + mtrl.FileHeader.StringTableSize
                + mtrl.FileHeader.AdditionalDataSize;
            if (colorDataOffset + dataSetSize <= originalBytes.Length)
            {
                colorTableData = new byte[dataSetSize];
                Array.Copy(originalBytes, colorDataOffset, colorTableData, 0, dataSetSize);
            }
        }

        RebuildMtrl(mtrl, shaderKeys, constants.ToArray(), mtrl.Samplers,
            shaderValues.ToArray(), additionalData, colorTableData, outputPath);
        return true;
    }

    private static void RebuildMtrl(MtrlFile mtrl, ShaderKey[] shaderKeys, Constant[] constants,
        Sampler[] samplers, float[] shaderValues, byte[] additionalData,
        byte[] colorTableData, string outputPath)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var shaderValueListSize = (ushort)(shaderValues.Length * 4);
        var materialHeader = new MaterialHeader
        {
            ShaderValueListSize = shaderValueListSize,
            ShaderKeyCount = (ushort)shaderKeys.Length,
            ConstantCount = (ushort)constants.Length,
            SamplerCount = (ushort)samplers.Length,
            Unknown1 = mtrl.MaterialHeader.Unknown1,
            Unknown2 = mtrl.MaterialHeader.Unknown2,
        };

        bw.Write(mtrl.FileHeader.Version);

        long fileSizePos = ms.Position;
        bw.Write(0u); // placeholder

        bw.Write(mtrl.FileHeader.StringTableSize);
        bw.Write(mtrl.FileHeader.ShaderPackageNameOffset);
        bool hasColorTable = colorTableData.Length > 0;
        byte effectiveColorSetCount = hasColorTable ? mtrl.FileHeader.ColorSetCount : (byte)0;

        bw.Write(mtrl.FileHeader.TextureCount);
        bw.Write(mtrl.FileHeader.UvSetCount);
        bw.Write(effectiveColorSetCount);
        bw.Write(mtrl.FileHeader.AdditionalDataSize);

        for (int i = 0; i < mtrl.TextureOffsets.Length; i++)
        {
            uint packed = mtrl.TextureOffsets[i].Offset | ((uint)mtrl.TextureOffsets[i].Flags << 16);
            bw.Write(packed);
        }

        for (int i = 0; i < mtrl.UvColorSets.Length; i++)
        {
            bw.Write(mtrl.UvColorSets[i].NameOffset);
            bw.Write(mtrl.UvColorSets[i].Index);
            bw.Write(mtrl.UvColorSets[i].Unknown1);
        }

        if (hasColorTable)
        {
            for (int i = 0; i < mtrl.ColorSets.Length; i++)
            {
                bw.Write(mtrl.ColorSets[i].NameOffset);
                bw.Write(mtrl.ColorSets[i].Index);
                bw.Write(mtrl.ColorSets[i].Unknown1);
            }
        }

        bw.Write(mtrl.Strings);
        bw.Write(additionalData);

        if (colorTableData.Length > 0)
            bw.Write(colorTableData);

        bw.Write(materialHeader.ShaderValueListSize);
        bw.Write(materialHeader.ShaderKeyCount);
        bw.Write(materialHeader.ConstantCount);
        bw.Write(materialHeader.SamplerCount);
        bw.Write(materialHeader.Unknown1);
        bw.Write(materialHeader.Unknown2);

        foreach (var key in shaderKeys)
        {
            bw.Write(key.Category);
            bw.Write(key.Value);
        }

        foreach (var c in constants)
        {
            bw.Write(c.ConstantId);
            bw.Write(c.ValueOffset);
            bw.Write(c.ValueSize);
        }

        foreach (var s in samplers)
        {
            bw.Write(s.SamplerId);
            bw.Write(s.Flags);
            bw.Write(s.TextureIndex);
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((byte)0);
        }

        foreach (var v in shaderValues)
            bw.Write(v);

        var totalSize = (ushort)ms.Length;
        var actualDataSetSize = (uint)colorTableData.Length;
        ms.Position = fileSizePos;
        bw.Write((uint)totalSize | (actualDataSetSize << 16));

        bw.Flush();
        File.WriteAllBytes(outputPath, ms.ToArray());
    }
}
