# 3D Decal Editor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add decal half-clip preprocessing and a 3D model editor window with ray-picking for intuitive decal placement on FFXIV character models.

**Architecture:** Three phases — (1) DecalLayer gains a ClipMode enum; composition clips pixels accordingly. (2) SharpDX-based off-screen renderer displays the character mesh in an independent ImGui window with orbit camera. (3) Möller–Trumbore ray-triangle intersection converts mouse clicks to UV coordinates, feeding back into DecalLayer.UvCenter for the existing composition pipeline.

**Tech Stack:** C# / .NET 8, Dalamud SDK, SharpDX 4.2.0 (Direct3D11 + D3DCompiler + DXGI + Mathematics), HLSL shaders, ImGui via Dalamud bindings.

---

## File Structure

### New Files

| Path | Responsibility |
|------|---------------|
| `SkinTatoo/SkinTatoo/Shaders/Model.hlsl` | VS/PS for Blinn-Phong textured model rendering |
| `SkinTatoo/SkinTatoo/DirectX/DxRenderer.cs` | Off-screen DX11 render target management, state save/restore |
| `SkinTatoo/SkinTatoo/DirectX/MeshBuffer.cs` | Builds GPU VertexBuffer/IndexBuffer from MeshData |
| `SkinTatoo/SkinTatoo/DirectX/OrbitCamera.cs` | Yaw/Pitch/Distance/Pan orbit camera with View/Proj matrices |
| `SkinTatoo/SkinTatoo/Mesh/RayPicker.cs` | Screen→ray unproject, Möller–Trumbore intersection, UV interpolation |
| `SkinTatoo/SkinTatoo/Gui/ModelEditorWindow.cs` | Independent ImGui window: 3D viewport + toolbar + interaction |

### Modified Files

| Path | Changes |
|------|---------|
| `SkinTatoo/SkinTatoo/Core/DecalLayer.cs` | Add `ClipMode` enum and `Clip` property |
| `SkinTatoo/SkinTatoo/Services/PreviewService.cs` | Apply ClipMode in `CpuUvComposite` inner loop |
| `SkinTatoo/SkinTatoo/Gui/MainWindow.cs` | Add ClipMode dropdown in layer properties panel |
| `SkinTatoo/SkinTatoo/Configuration.cs` | Add `ClipMode` to `SavedLayer`, add `ModelEditorWindowOpen` |
| `SkinTatoo/SkinTatoo/Plugin.cs` | Register SharpDX device, create ModelEditorWindow, wire up lifecycle |
| `SkinTatoo/SkinTatoo/SkinTatoo.csproj` | Add SharpDX NuGet packages, add shader as EmbeddedResource |

---

## Task 1: Add ClipMode to DecalLayer

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Core/DecalLayer.cs`

- [ ] **Step 1: Add ClipMode enum and property**

```csharp
// Add after the EmissiveMask enum (line 22)

public enum ClipMode
{
    None,
    ClipLeft,   // discard left half (U < 0.5 in decal-local space)
    ClipRight,  // discard right half (U > 0.5 in decal-local space)
    ClipTop,    // discard top half (V < 0.5 in decal-local space)
    ClipBottom, // discard bottom half (V > 0.5 in decal-local space)
}
```

```csharp
// Add after BlendMode property (line 37) in DecalLayer class
public ClipMode Clip { get; set; } = ClipMode.None;
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add SkinTatoo/SkinTatoo/Core/DecalLayer.cs
git commit -m "feat: add ClipMode enum to DecalLayer"
```

---

## Task 2: Apply ClipMode in composition pipeline

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Services/PreviewService.cs:839-856`

- [ ] **Step 1: Add clip check in CpuUvComposite inner loop**

In `CpuUvComposite`, after computing `ru`/`rv` and the bounds check (line 849), before sampling the decal, add the clip logic. The `ru`/`rv` values are in decal-local space [-0.5, 0.5], and we can map them to [0, 1] for the clip test:

```csharp
// Add after line 849: if (ru < -0.5f || ru > 0.5f || rv < -0.5f || rv > 0.5f) continue;

// Clip mode: discard pixels in the specified half of decal-local space
// ru/rv are in [-0.5, 0.5], so 0 is the center line
switch (layer.Clip)
{
    case ClipMode.ClipLeft when ru < 0f: continue;
    case ClipMode.ClipRight when ru >= 0f: continue;
    case ClipMode.ClipTop when rv < 0f: continue;
    case ClipMode.ClipBottom when rv >= 0f: continue;
}
```

- [ ] **Step 2: Apply same clip logic to emissive normal composite**

Find the second composition loop in `CompositeEmissiveNorm` (around line 694 where `lu`/`lv`/`ru`/`rv` are computed). Add the same clip switch after the bounds check:

```csharp
// After: if (ru < -0.5f || ru > 0.5f || rv < -0.5f || rv > 0.5f) continue;
switch (layer.Clip)
{
    case ClipMode.ClipLeft when ru < 0f: continue;
    case ClipMode.ClipRight when ru >= 0f: continue;
    case ClipMode.ClipTop when rv < 0f: continue;
    case ClipMode.ClipBottom when rv >= 0f: continue;
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add SkinTatoo/SkinTatoo/Services/PreviewService.cs
git commit -m "feat: apply ClipMode in UV composite and emissive pipelines"
```

---

## Task 3: Add ClipMode UI and config persistence

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Gui/MainWindow.cs` (layer properties section)
- Modify: `SkinTatoo/SkinTatoo/Configuration.cs`

- [ ] **Step 1: Add ClipMode to SavedLayer**

In `Configuration.cs`, add to `SavedLayer` class:

```csharp
public int Clip { get; set; }
```

- [ ] **Step 2: Update DecalProject serialization**

Find where DecalLayer properties are saved/loaded in `DecalProject.cs` (the `SaveToConfig`/`LoadFromConfig` methods). Add `Clip` round-trip:

Save direction:
```csharp
Clip = (int)layer.Clip,
```

Load direction:
```csharp
Clip = (ClipMode)saved.Clip,
```

- [ ] **Step 3: Add ClipMode dropdown to MainWindow layer properties**

Find the layer properties section in `MainWindow.cs` where blend mode dropdown is drawn (search for `BlendModeNames`). Add a clip dropdown nearby:

```csharp
// Clip mode dropdown
var clipNames = new[] { "无", "切左半", "切右半", "切上半", "切下半" };
var clipIdx = (int)layer.Clip;
if (ImGui.Combo("裁剪", ref clipIdx, clipNames, clipNames.Length))
{
    layer.Clip = (ClipMode)clipIdx;
    MarkPreviewDirty();
}
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add SkinTatoo/SkinTatoo/Gui/MainWindow.cs SkinTatoo/SkinTatoo/Configuration.cs SkinTatoo/SkinTatoo/Core/DecalProject.cs
git commit -m "feat: add ClipMode UI dropdown and config persistence"
```

---

## Task 4: Add SharpDX NuGet packages

**Files:**
- Modify: `SkinTatoo/SkinTatoo/SkinTatoo.csproj`

- [ ] **Step 1: Add SharpDX package references**

Add to the `<ItemGroup>` with other PackageReferences:

```xml
<PackageReference Include="SharpDX" Version="4.2.0" />
<PackageReference Include="SharpDX.Direct3D11" Version="4.2.0" />
<PackageReference Include="SharpDX.D3DCompiler" Version="4.2.0" />
<PackageReference Include="SharpDX.DXGI" Version="4.2.0" />
<PackageReference Include="SharpDX.Mathematics" Version="4.2.0" />
```

- [ ] **Step 2: Add shader as EmbeddedResource**

Add a new ItemGroup for the shader file:

```xml
<ItemGroup>
  <EmbeddedResource Include="Shaders\Model.hlsl" />
</ItemGroup>
```

- [ ] **Step 3: Restore and build**

Run: `dotnet restore && dotnet build -c Release`
Expected: NuGet restore succeeds, build succeeds

- [ ] **Step 4: Commit**

```bash
git add SkinTatoo/SkinTatoo/SkinTatoo.csproj
git commit -m "chore: add SharpDX 4.2.0 NuGet packages"
```

---

## Task 5: Create HLSL shader

**Files:**
- Create: `SkinTatoo/SkinTatoo/Shaders/Model.hlsl`

- [ ] **Step 1: Write the Blinn-Phong shader with texture sampling**

```hlsl
// Constant buffers
cbuffer VSConstants : register(b0)
{
    float4x4 WorldMatrix;
    float4x4 ViewProjectionMatrix;
};

cbuffer PSConstants : register(b0)
{
    float3 LightDir;
    float _pad0;
    float3 CameraPos;
    float _pad1;
    int HasTexture;
    float3 _pad2;
};

// Textures
Texture2D DiffuseTex : register(t0);
SamplerState Sampler : register(s0);

struct VS_IN
{
    float3 pos : POSITION;
    float3 norm : NORMAL;
    float2 uv : TEXCOORD;
};

struct PS_IN
{
    float4 pos : SV_POSITION;
    float3 worldPos : WORLDPOS;
    float3 norm : NORMAL;
    float2 uv : TEXCOORD;
};

PS_IN VS(VS_IN input)
{
    PS_IN output = (PS_IN)0;
    float4 worldPos = mul(float4(input.pos, 1.0f), WorldMatrix);
    output.worldPos = worldPos.xyz;
    output.pos = mul(worldPos, ViewProjectionMatrix);
    output.norm = normalize(mul(input.norm, (float3x3)WorldMatrix));
    output.uv = input.uv;
    return output;
}

float4 PS(PS_IN input) : SV_Target
{
    float3 norm = normalize(input.norm);
    float3 lightDir = normalize(LightDir);

    // Ambient + diffuse
    float3 ambient = float3(0.3f, 0.3f, 0.35f);
    float ndotl = saturate(dot(norm, -lightDir));
    float3 diffuse = float3(0.7f, 0.7f, 0.65f) * ndotl;

    // Specular (Blinn-Phong)
    float3 viewDir = normalize(CameraPos - input.worldPos);
    float3 halfVec = normalize(-lightDir + viewDir);
    float spec = pow(saturate(dot(norm, halfVec)), 32.0f);
    float3 specular = float3(0.2f, 0.2f, 0.2f) * spec;

    float3 baseColor;
    if (HasTexture)
    {
        baseColor = DiffuseTex.Sample(Sampler, input.uv).rgb;
    }
    else
    {
        baseColor = float3(0.75f, 0.7f, 0.65f);
    }

    float3 result = (ambient + diffuse) * baseColor + specular;
    return float4(result, 1.0f);
}
```

- [ ] **Step 2: Build to verify embedded resource is included**

Run: `dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add SkinTatoo/SkinTatoo/Shaders/Model.hlsl
git commit -m "feat: add Blinn-Phong HLSL shader for 3D model rendering"
```

---

## Task 6: Implement DxRenderer (off-screen rendering)

**Files:**
- Create: `SkinTatoo/SkinTatoo/DirectX/DxRenderer.cs`

- [ ] **Step 1: Write DxRenderer class**

This class manages the DX11 off-screen render target, depth stencil, shader compilation, and state save/restore. Pattern taken from VFXEditor-CN `Renderer.cs` + `ModelRenderer.cs`.

```csharp
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace SkinTatoo.DirectX;

[StructLayout(LayoutKind.Sequential)]
public struct VSConstants
{
    public Matrix WorldMatrix;
    public Matrix ViewProjectionMatrix;
}

[StructLayout(LayoutKind.Sequential)]
public struct PSConstants
{
    public Vector3 LightDir;
    public float _pad0;
    public Vector3 CameraPos;
    public float _pad1;
    public int HasTexture;
    public Vector3 _pad2;
}

public class DxRenderer : IDisposable
{
    private readonly Device device;
    private readonly DeviceContext ctx;

    private int width = 4;
    private int height = 4;

    // Render target
    private Texture2D renderTexture;
    private ShaderResourceView renderSrv;
    private RenderTargetView renderTarget;

    // Depth
    private Texture2D depthTexture;
    private DepthStencilView depthView;
    private DepthStencilState depthState;

    // Shader
    private VertexShader vertexShader;
    private PixelShader pixelShader;
    private InputLayout inputLayout;

    // Constant buffers
    private SharpDX.Direct3D11.Buffer vsCBuffer;
    private SharpDX.Direct3D11.Buffer psCBuffer;

    // Sampler
    private SamplerState sampler;

    // Rasterizer
    private RasterizerState rasterState;

    public nint OutputPointer => renderSrv?.NativePointer ?? nint.Zero;
    public Device Device => device;
    public DeviceContext Context => ctx;

    public DxRenderer(nint deviceHandle)
    {
        device = new Device(deviceHandle);
        ctx = device.ImmediateContext;

        CompileShaders();
        CreateStates();
        ResizeResources();
    }

    private void CompileShaders()
    {
        // Load HLSL from embedded resource
        var assembly = Assembly.GetExecutingAssembly();
        string hlslSource;
        using (var stream = assembly.GetManifestResourceStream("SkinTatoo.Shaders.Model.hlsl"))
        {
            if (stream == null) throw new Exception("Shader resource not found");
            using var reader = new StreamReader(stream);
            hlslSource = reader.ReadToEnd();
        }

        using var vsCompiled = ShaderBytecode.Compile(hlslSource, "VS", "vs_4_0");
        if (vsCompiled.HasErrors) throw new Exception($"VS compile error: {vsCompiled.Message}");
        vertexShader = new VertexShader(device, vsCompiled);

        using var psCompiled = ShaderBytecode.Compile(hlslSource, "PS", "ps_4_0");
        if (psCompiled.HasErrors) throw new Exception($"PS compile error: {psCompiled.Message}");
        pixelShader = new PixelShader(device, psCompiled);

        using var signature = ShaderSignature.GetInputSignature(vsCompiled);
        inputLayout = new InputLayout(device, signature, new[]
        {
            new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElement("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
        });

        vsCBuffer = new SharpDX.Direct3D11.Buffer(device,
            Utilities.SizeOf<VSConstants>(),
            ResourceUsage.Default, BindFlags.ConstantBuffer,
            CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        psCBuffer = new SharpDX.Direct3D11.Buffer(device,
            Utilities.SizeOf<PSConstants>(),
            ResourceUsage.Default, BindFlags.ConstantBuffer,
            CpuAccessFlags.None, ResourceOptionFlags.None, 0);
    }

    private void CreateStates()
    {
        depthState = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            IsStencilEnabled = false,
            DepthWriteMask = DepthWriteMask.All,
            DepthComparison = Comparison.Less,
        });

        sampler = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            BorderColor = Color.Black,
            ComparisonFunction = Comparison.Never,
            MaximumAnisotropy = 16,
            MinimumLod = 0,
            MaximumLod = float.MaxValue,
        });

        rasterState = new RasterizerState(device, new RasterizerStateDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            IsDepthClipEnabled = true,
            IsMultisampleEnabled = true,
        });
    }

    public void Resize(int newWidth, int newHeight)
    {
        if (newWidth < 1) newWidth = 1;
        if (newHeight < 1) newHeight = 1;
        if (newWidth == width && newHeight == height) return;
        width = newWidth;
        height = newHeight;
        ResizeResources();
    }

    private void ResizeResources()
    {
        renderTexture?.Dispose();
        renderSrv?.Dispose();
        renderTarget?.Dispose();
        depthTexture?.Dispose();
        depthView?.Dispose();

        renderTexture = new Texture2D(device, new Texture2DDescription
        {
            Format = Format.B8G8R8A8_UNorm,
            ArraySize = 1,
            MipLevels = 1,
            Width = width,
            Height = height,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        });
        renderSrv = new ShaderResourceView(device, renderTexture);
        renderTarget = new RenderTargetView(device, renderTexture);

        depthTexture = new Texture2D(device, new Texture2DDescription
        {
            ArraySize = 1,
            BindFlags = BindFlags.DepthStencil,
            CpuAccessFlags = CpuAccessFlags.None,
            Format = Format.D32_Float,
            Width = width,
            Height = height,
            MipLevels = 1,
            OptionFlags = ResourceOptionFlags.None,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
        });
        depthView = new DepthStencilView(device, depthTexture);
    }

    public void BeginFrame(out RasterizerState oldRaster, out RenderTargetView[] oldRtvs, out DepthStencilView oldDsv, out DepthStencilState oldDss)
    {
        // Save current DX11 state
        oldRaster = ctx.Rasterizer.State;
        oldRtvs = ctx.OutputMerger.GetRenderTargets(OutputMergerStage.SimultaneousRenderTargetCount, out oldDsv);
        oldDss = ctx.OutputMerger.GetDepthStencilState(out _);

        // Set our render target
        ctx.ClearRenderTargetView(renderTarget, new Color4(0.15f, 0.15f, 0.18f, 1f));
        ctx.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth, 1f, 0);

        ctx.Rasterizer.SetViewport(new Viewport(0, 0, width, height, 0f, 1f));
        ctx.Rasterizer.State = rasterState;
        ctx.OutputMerger.SetDepthStencilState(depthState);
        ctx.OutputMerger.SetTargets(depthView, renderTarget);

        // Bind shaders and layout
        ctx.VertexShader.Set(vertexShader);
        ctx.PixelShader.Set(pixelShader);
        ctx.InputAssembler.InputLayout = inputLayout;
        ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;

        ctx.VertexShader.SetConstantBuffer(0, vsCBuffer);
        ctx.PixelShader.SetConstantBuffer(0, psCBuffer);
        ctx.PixelShader.SetSampler(0, sampler);
    }

    public void UpdateVSConstants(ref VSConstants data) => ctx.UpdateSubresource(ref data, vsCBuffer);
    public void UpdatePSConstants(ref PSConstants data) => ctx.UpdateSubresource(ref data, psCBuffer);

    public void BindDiffuseTexture(ShaderResourceView? srv)
    {
        ctx.PixelShader.SetShaderResource(0, srv);
    }

    public void EndFrame(RasterizerState oldRaster, RenderTargetView[] oldRtvs, DepthStencilView oldDsv, DepthStencilState oldDss)
    {
        ctx.Flush();

        // Restore state
        ctx.Rasterizer.State = oldRaster;
        ctx.OutputMerger.SetRenderTargets(oldDsv, oldRtvs);
        ctx.OutputMerger.SetDepthStencilState(oldDss);
    }

    public ShaderResourceView CreateTextureFromRgba(byte[] rgbaData, int texWidth, int texHeight, out Texture2D texture)
    {
        // Convert RGBA → BGRA for B8G8R8A8_UNorm
        var bgra = new byte[rgbaData.Length];
        for (int i = 0; i < rgbaData.Length; i += 4)
        {
            bgra[i] = rgbaData[i + 2];     // B
            bgra[i + 1] = rgbaData[i + 1]; // G
            bgra[i + 2] = rgbaData[i];     // R
            bgra[i + 3] = rgbaData[i + 3]; // A
        }

        var stream = DataStream.Create(bgra, true, true);
        var rect = new DataRectangle(stream.DataPointer, texWidth * 4);
        texture = new Texture2D(device, new Texture2DDescription
        {
            Width = texWidth,
            Height = texHeight,
            ArraySize = 1,
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            CpuAccessFlags = CpuAccessFlags.None,
            Format = Format.B8G8R8A8_UNorm,
            MipLevels = 1,
            OptionFlags = ResourceOptionFlags.None,
            SampleDescription = new SampleDescription(1, 0),
        }, rect);

        return new ShaderResourceView(device, texture);
    }

    public void Dispose()
    {
        renderTexture?.Dispose();
        renderSrv?.Dispose();
        renderTarget?.Dispose();
        depthTexture?.Dispose();
        depthView?.Dispose();
        depthState?.Dispose();
        sampler?.Dispose();
        rasterState?.Dispose();
        vertexShader?.Dispose();
        pixelShader?.Dispose();
        inputLayout?.Dispose();
        vsCBuffer?.Dispose();
        psCBuffer?.Dispose();
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add SkinTatoo/SkinTatoo/DirectX/DxRenderer.cs
git commit -m "feat: implement DxRenderer with off-screen DX11 rendering"
```

---

## Task 7: Implement MeshBuffer (GPU vertex/index buffers)

**Files:**
- Create: `SkinTatoo/SkinTatoo/DirectX/MeshBuffer.cs`

- [ ] **Step 1: Write MeshBuffer class**

Converts `MeshData` (CPU) to GPU vertex/index buffers.

```csharp
using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SkinTatoo.Mesh;
using Device = SharpDX.Direct3D11.Device;

namespace SkinTatoo.DirectX;

[StructLayout(LayoutKind.Sequential)]
public struct GpuVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 UV;

    public static readonly int SizeInBytes = Marshal.SizeOf<GpuVertex>();
}

public class MeshBuffer : IDisposable
{
    private SharpDX.Direct3D11.Buffer? vertexBuffer;
    private SharpDX.Direct3D11.Buffer? indexBuffer;
    private int indexCount;

    public int IndexCount => indexCount;
    public bool IsLoaded => vertexBuffer != null && indexCount > 0;

    public void Upload(Device device, MeshData mesh)
    {
        Dispose();

        var vertices = new GpuVertex[mesh.Vertices.Length];
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            var v = mesh.Vertices[i];
            vertices[i] = new GpuVertex
            {
                Position = new Vector3(v.Position.X, v.Position.Y, v.Position.Z),
                Normal = new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z),
                UV = new Vector2(v.UV.X, v.UV.Y),
            };
        }

        vertexBuffer = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.VertexBuffer, vertices);

        // Convert ushort[] to int[] for 32-bit index buffer
        var indices = new int[mesh.Indices.Length];
        for (int i = 0; i < mesh.Indices.Length; i++)
            indices[i] = mesh.Indices[i];

        indexBuffer = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.IndexBuffer, indices);
        indexCount = mesh.Indices.Length;
    }

    public void Bind(DeviceContext ctx)
    {
        if (vertexBuffer == null || indexBuffer == null) return;
        ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vertexBuffer, GpuVertex.SizeInBytes, 0));
        ctx.InputAssembler.SetIndexBuffer(indexBuffer, Format.R32_UInt, 0);
    }

    public void DrawIndexed(DeviceContext ctx)
    {
        if (indexCount <= 0) return;
        ctx.DrawIndexed(indexCount, 0, 0);
    }

    public void Dispose()
    {
        vertexBuffer?.Dispose();
        vertexBuffer = null;
        indexBuffer?.Dispose();
        indexBuffer = null;
        indexCount = 0;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add SkinTatoo/SkinTatoo/DirectX/MeshBuffer.cs
git commit -m "feat: implement MeshBuffer for GPU vertex/index upload"
```

---

## Task 8: Implement OrbitCamera

**Files:**
- Create: `SkinTatoo/SkinTatoo/DirectX/OrbitCamera.cs`

- [ ] **Step 1: Write OrbitCamera class**

```csharp
using System;
using SharpDX;

namespace SkinTatoo.DirectX;

public class OrbitCamera
{
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float Distance { get; set; } = 3f;
    public Vector3 Target { get; set; } = Vector3.Zero;
    public Vector3 PanOffset { get; set; } = Vector3.Zero;

    public Matrix ViewMatrix { get; private set; }
    public Matrix ProjMatrix { get; private set; }
    public Vector3 CameraPosition { get; private set; }

    private float aspect = 1f;

    public void SetAspect(float w, float h)
    {
        aspect = w / Math.Max(h, 1f);
    }

    public void Update()
    {
        var rotation = Quaternion.RotationYawPitchRoll(Yaw, Pitch, 0f);
        var forward = Vector3.Transform(-Vector3.UnitZ, rotation);
        CameraPosition = Target + PanOffset - Distance * forward;
        var up = Vector3.Transform(Vector3.UnitY, rotation);

        ViewMatrix = Matrix.LookAtLH(CameraPosition, Target + PanOffset, up);
        ProjMatrix = Matrix.PerspectiveFovLH((float)Math.PI / 4f, aspect, 0.01f, 100f);
    }

    public void Rotate(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, -1.55f, 1.55f);
        Update();
    }

    public void Pan(float deltaX, float deltaY)
    {
        var rotation = Quaternion.RotationYawPitchRoll(Yaw, Pitch, 0f);
        var right = Vector3.Transform(Vector3.UnitX, rotation);
        var up = Vector3.Transform(Vector3.UnitY, rotation);
        PanOffset += right * deltaX * Distance * 0.002f + up * deltaY * Distance * 0.002f;
        Update();
    }

    public void Zoom(float delta)
    {
        Distance = Math.Max(0.01f, Distance - delta * 0.2f);
        Update();
    }

    public void Reset()
    {
        Yaw = 0;
        Pitch = 0;
        Distance = 3f;
        PanOffset = Vector3.Zero;
        Update();
    }

    // Unproject screen coordinates to world-space ray
    public (Vector3 Origin, Vector3 Direction) ScreenToRay(
        float screenX, float screenY, float viewportWidth, float viewportHeight)
    {
        // NDC: [-1,1]
        float ndcX = (2f * screenX / viewportWidth) - 1f;
        float ndcY = 1f - (2f * screenY / viewportHeight);

        var invViewProj = Matrix.Invert(ViewMatrix * ProjMatrix);

        var nearPoint = Vector3.TransformCoordinate(new Vector3(ndcX, ndcY, 0f), invViewProj);
        var farPoint = Vector3.TransformCoordinate(new Vector3(ndcX, ndcY, 1f), invViewProj);

        var direction = Vector3.Normalize(farPoint - nearPoint);
        return (nearPoint, direction);
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add SkinTatoo/SkinTatoo/DirectX/OrbitCamera.cs
git commit -m "feat: implement OrbitCamera with screen-to-ray unproject"
```

---

## Task 9: Implement RayPicker (ray-triangle intersection + UV interpolation)

**Files:**
- Create: `SkinTatoo/SkinTatoo/Mesh/RayPicker.cs`

- [ ] **Step 1: Write RayPicker with Möller–Trumbore algorithm**

```csharp
using System;
using System.Numerics;

namespace SkinTatoo.Mesh;

public struct RayHit
{
    public int TriangleIndex;
    public float Distance;
    public Vector2 UV;
    public Vector3 WorldPosition;
    public Vector3 Normal;
}

public static class RayPicker
{
    // Pick the nearest triangle hit and return interpolated UV
    public static RayHit? Pick(MeshData mesh, Vector3 rayOrigin, Vector3 rayDir)
    {
        float bestDist = float.MaxValue;
        RayHit? bestHit = null;

        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var (v0, v1, v2) = mesh.GetTriangle(t);

            if (RayTriangleIntersect(rayOrigin, rayDir, v0.Position, v1.Position, v2.Position,
                    out float dist, out float u, out float v))
            {
                if (dist > 0 && dist < bestDist)
                {
                    bestDist = dist;
                    float w0 = 1f - u - v;
                    float w1 = u;
                    float w2 = v;

                    var hitUV = w0 * v0.UV + w1 * v1.UV + w2 * v2.UV;
                    var hitPos = w0 * v0.Position + w1 * v1.Position + w2 * v2.Position;
                    var hitNorm = Vector3.Normalize(w0 * v0.Normal + w1 * v1.Normal + w2 * v2.Normal);

                    bestHit = new RayHit
                    {
                        TriangleIndex = t,
                        Distance = dist,
                        UV = hitUV,
                        WorldPosition = hitPos,
                        Normal = hitNorm,
                    };
                }
            }
        }

        return bestHit;
    }

    // Möller–Trumbore ray-triangle intersection
    // Returns barycentric coordinates (u, v) where hit = (1-u-v)*v0 + u*v1 + v*v2
    private static bool RayTriangleIntersect(
        Vector3 orig, Vector3 dir,
        Vector3 v0, Vector3 v1, Vector3 v2,
        out float t, out float u, out float v)
    {
        t = u = v = 0;
        const float epsilon = 1e-8f;

        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var h = Vector3.Cross(dir, e2);
        float a = Vector3.Dot(e1, h);

        if (a > -epsilon && a < epsilon) return false;

        float f = 1f / a;
        var s = orig - v0;
        u = f * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return false;

        var q = Vector3.Cross(s, e1);
        v = f * Vector3.Dot(dir, q);
        if (v < 0f || u + v > 1f) return false;

        t = f * Vector3.Dot(e2, q);
        return t > epsilon;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add SkinTatoo/SkinTatoo/Mesh/RayPicker.cs
git commit -m "feat: implement RayPicker with Möller-Trumbore intersection"
```

---

## Task 10: Implement ModelEditorWindow

**Files:**
- Create: `SkinTatoo/SkinTatoo/Gui/ModelEditorWindow.cs`

- [ ] **Step 1: Write ModelEditorWindow with 3D viewport and interaction**

```csharp
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using SkinTatoo.Core;
using SkinTatoo.DirectX;
using SkinTatoo.Mesh;
using SkinTatoo.Services;

namespace SkinTatoo.Gui;

public class ModelEditorWindow : Window, IDisposable
{
    private readonly DecalProject project;
    private readonly PreviewService previewService;
    private readonly DxRenderer renderer;
    private readonly MeshBuffer meshBuffer;
    private readonly OrbitCamera camera;

    // Diffuse texture on GPU
    private SharpDX.Direct3D11.ShaderResourceView? diffuseSrv;
    private SharpDX.Direct3D11.Texture2D? diffuseTex;
    private bool needsTextureUpdate = true;

    // Interaction state
    private bool isDraggingCamera;
    private bool isDraggingDecal;
    private bool isRotatingDecal;
    private Vector2 lastMousePos;
    private bool meshUploaded;

    // For coordinate conversion between System.Numerics and SharpDX
    private static SharpDX.Vector3 ToSharpDX(Vector3 v) => new(v.X, v.Y, v.Z);
    private static Vector3 FromSharpDX(SharpDX.Vector3 v) => new(v.X, v.Y, v.Z);

    public ModelEditorWindow(
        DecalProject project,
        PreviewService previewService,
        nint deviceHandle)
        : base("3D 贴花编辑器###SkinTatooModelEditor",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.project = project;
        this.previewService = previewService;

        renderer = new DxRenderer(deviceHandle);
        meshBuffer = new MeshBuffer();
        camera = new OrbitCamera();
        camera.Update();

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 350),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void MarkTexturesDirty() => needsTextureUpdate = true;

    public override void Draw()
    {
        TryUploadMesh();
        TryUpdateTexture();

        DrawToolbar();
        ImGui.Separator();
        DrawViewport();
    }

    private void DrawToolbar()
    {
        if (ImGui.Button("重置相机"))
        {
            camera.Reset();
        }
        ImGui.SameLine();

        // Status info
        var mesh = previewService.CurrentMesh;
        if (mesh != null)
            ImGui.Text($"三角面: {mesh.TriangleCount}  顶点: {mesh.Vertices.Length}");
        else
            ImGui.TextColored(new Vector4(1, 0.5f, 0.3f, 1), "未加载网格");

        var layer = project.SelectedLayer;
        if (layer != null)
        {
            ImGui.SameLine();
            ImGui.Text($"  |  当前图层: {layer.Name}");
        }
    }

    private void DrawViewport()
    {
        var size = ImGui.GetContentRegionAvail();
        if (size.X < 1 || size.Y < 1) return;

        var iw = (int)size.X;
        var ih = (int)size.Y;
        renderer.Resize(iw, ih);
        camera.SetAspect(iw, ih);

        RenderScene();

        var outputPtr = renderer.OutputPointer;
        if (outputPtr == nint.Zero) return;

        var cursorBefore = ImGui.GetCursorScreenPos();
        ImGui.Image(new ImTextureID(outputPtr), size);

        // Handle interaction over the image
        if (ImGui.IsItemHovered())
        {
            HandleInteraction(cursorBefore, size);
        }
        else
        {
            isDraggingCamera = false;
            isDraggingDecal = false;
            isRotatingDecal = false;
        }
    }

    private void RenderScene()
    {
        if (!meshBuffer.IsLoaded) return;

        renderer.BeginFrame(out var oldR, out var oldRtv, out var oldDsv, out var oldDss);

        // FFXIV models are mirrored on X
        var world = SharpDX.Matrix.Scaling(-1, 1, 1);
        var viewProj = camera.ViewMatrix * camera.ProjMatrix;
        viewProj.Transpose();
        world.Transpose();

        var vsData = new VSConstants
        {
            WorldMatrix = world,
            ViewProjectionMatrix = viewProj,
        };
        renderer.UpdateVSConstants(ref vsData);

        var camPos = camera.CameraPosition;
        var psData = new PSConstants
        {
            LightDir = new SharpDX.Vector3(0.3f, -1f, 0.5f),
            CameraPos = camPos,
            HasTexture = diffuseSrv != null ? 1 : 0,
        };
        renderer.UpdatePSConstants(ref psData);

        renderer.BindDiffuseTexture(diffuseSrv);

        meshBuffer.Bind(renderer.Context);
        meshBuffer.DrawIndexed(renderer.Context);

        renderer.EndFrame(oldR, oldRtv, oldDsv, oldDss);
    }

    private void HandleInteraction(Vector2 viewportPos, Vector2 viewportSize)
    {
        var mousePos = ImGui.GetMousePos();
        var localMouse = mousePos - viewportPos;

        var io = ImGui.GetIO();

        // R key held = decal rotation mode
        bool rHeld = ImGui.IsKeyDown(ImGuiKey.R);

        // Right mouse: orbit camera
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Right))
        {
            if (!isDraggingCamera)
            {
                isDraggingCamera = true;
                lastMousePos = mousePos;
            }
            var delta = mousePos - lastMousePos;
            camera.Rotate(delta.X * 0.01f, -delta.Y * 0.01f);
            lastMousePos = mousePos;
        }
        else
        {
            isDraggingCamera = false;
        }

        // Middle mouse: pan camera
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
        {
            var delta = io.MouseDelta;
            camera.Pan(-delta.X, delta.Y);
        }

        // Ctrl + scroll = camera zoom; scroll = decal scale
        if (io.MouseWheel != 0)
        {
            if (io.KeyCtrl)
            {
                camera.Zoom(io.MouseWheel);
            }
            else
            {
                // Scale selected decal
                var layer = project.SelectedLayer;
                if (layer != null)
                {
                    float scaleFactor = 1f + io.MouseWheel * 0.05f;
                    layer.UvScale *= scaleFactor;
                    layer.UvScale = new Vector2(
                        Math.Clamp(layer.UvScale.X, 0.01f, 2f),
                        Math.Clamp(layer.UvScale.Y, 0.01f, 2f));
                    previewService.MarkDirty();
                    needsTextureUpdate = true;
                }
            }
        }

        // Left click/drag on model: place/move decal (or rotate if R held)
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && !isDraggingCamera)
        {
            var mesh = previewService.CurrentMesh;
            if (mesh != null)
            {
                // Screen-to-ray: use camera unproject
                var (rayOrig, rayDirSharp) = camera.ScreenToRay(
                    localMouse.X, localMouse.Y, viewportSize.X, viewportSize.Y);

                // Convert SharpDX Vector3 to System.Numerics for RayPicker
                // The world matrix mirrors X, so mirror the ray too
                var orig = FromSharpDX(rayOrig);
                var dir = FromSharpDX(rayDirSharp);
                orig.X = -orig.X;
                dir.X = -dir.X;

                var hit = RayPicker.Pick(mesh, orig, dir);
                if (hit.HasValue)
                {
                    var layer = project.SelectedLayer;
                    if (layer != null)
                    {
                        if (rHeld)
                        {
                            // Rotation mode
                            if (!isRotatingDecal)
                            {
                                isRotatingDecal = true;
                                lastMousePos = mousePos;
                            }
                            var delta = mousePos - lastMousePos;
                            layer.RotationDeg += delta.X * 0.5f;
                            lastMousePos = mousePos;
                        }
                        else
                        {
                            // Position mode: move decal center to hit UV
                            layer.UvCenter = hit.Value.UV;
                        }
                        previewService.MarkDirty();
                        needsTextureUpdate = true;
                    }
                }
            }
        }
        else
        {
            isDraggingDecal = false;
            isRotatingDecal = false;
        }
    }

    private void TryUploadMesh()
    {
        var mesh = previewService.CurrentMesh;
        if (mesh == null || meshUploaded) return;

        meshBuffer.Upload(renderer.Device, mesh);
        meshUploaded = true;

        // Auto-fit camera to mesh bounds
        AutoFitCamera(mesh);
    }

    private void AutoFitCamera(MeshData mesh)
    {
        if (mesh.Vertices.Length == 0) return;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in mesh.Vertices)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }
        var center = (min + max) * 0.5f;
        var extent = (max - min).Length();

        camera.Target = ToSharpDX(center);
        camera.Distance = extent * 0.8f;
        camera.PanOffset = SharpDX.Vector3.Zero;
        camera.Yaw = 0;
        camera.Pitch = 0;
        camera.Update();
    }

    private void TryUpdateTexture()
    {
        if (!needsTextureUpdate) return;
        needsTextureUpdate = false;

        // Get the latest composited texture from PreviewService
        var texData = previewService.GetLatestCompositeRgba();
        if (texData == null) return;

        diffuseSrv?.Dispose();
        diffuseTex?.Dispose();
        diffuseSrv = renderer.CreateTextureFromRgba(texData.Value.Data, texData.Value.Width, texData.Value.Height, out diffuseTex);
    }

    public void OnMeshChanged()
    {
        meshUploaded = false;
        needsTextureUpdate = true;
    }

    public void Dispose()
    {
        diffuseSrv?.Dispose();
        diffuseTex?.Dispose();
        meshBuffer.Dispose();
        renderer.Dispose();
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build -c Release`
Expected: Likely fails — `PreviewService.MarkDirty()` and `PreviewService.GetLatestCompositeRgba()` and `DecalProject.SelectedLayer` don't exist yet. We'll add them in the next task.

---

## Task 11: Add integration hooks to PreviewService and DecalProject

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Services/PreviewService.cs`
- Modify: `SkinTatoo/SkinTatoo/Core/DecalProject.cs`

- [ ] **Step 1: Add MarkDirty() and GetLatestCompositeRgba() to PreviewService**

Add a public method to mark preview as dirty (for external callers like ModelEditorWindow):

```csharp
// Add field near other fields
private (byte[] Data, int Width, int Height)? latestCompositeRgba;

// Public method for external callers
public void MarkDirty()
{
    // Signal that a preview update is needed — MainWindow's auto-preview will pick this up
    // Store a flag that MainWindow can check
    ExternalDirty = true;
}

public bool ExternalDirty { get; set; }

// Return the latest composite result for 3D preview
public (byte[] Data, int Width, int Height)? GetLatestCompositeRgba() => latestCompositeRgba;
```

Then in the `CpuUvComposite` method, before returning, cache the result:

```csharp
// Before "return output;" at line 905
latestCompositeRgba = (output, w, h);
```

- [ ] **Step 2: Add SelectedLayer convenience property to DecalProject**

In `DecalProject.cs`, add:

```csharp
public DecalLayer? SelectedLayer
{
    get
    {
        var group = SelectedGroup;
        if (group == null || SelectedLayerIndex < 0 || SelectedLayerIndex >= group.Layers.Count)
            return null;
        return group.Layers[SelectedLayerIndex];
    }
}
```

Check if `SelectedGroup` and `SelectedLayerIndex` already exist (they likely do since MainWindow uses them). If `SelectedLayerIndex` doesn't exist, it will be tracked in MainWindow's state and we should reference that instead. Adjust accordingly.

- [ ] **Step 3: Wire ExternalDirty into MainWindow auto-preview**

In `MainWindow.Draw()` or the auto-preview debounce section, check `previewService.ExternalDirty`:

```csharp
if (previewService.ExternalDirty)
{
    previewService.ExternalDirty = false;
    MarkPreviewDirty();
}
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add SkinTatoo/SkinTatoo/Services/PreviewService.cs SkinTatoo/SkinTatoo/Core/DecalProject.cs SkinTatoo/SkinTatoo/Gui/MainWindow.cs
git commit -m "feat: add integration hooks for 3D editor (MarkDirty, GetLatestCompositeRgba, SelectedLayer)"
```

---

## Task 12: Wire ModelEditorWindow into Plugin lifecycle

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Plugin.cs`
- Modify: `SkinTatoo/SkinTatoo/Configuration.cs`

- [ ] **Step 1: Add ModelEditorWindowOpen to Configuration**

```csharp
public bool ModelEditorWindowOpen { get; set; }
```

- [ ] **Step 2: Create and register ModelEditorWindow in Plugin constructor**

After the existing window creation (around line 95), add:

```csharp
// 3D Editor
var deviceHandle = pluginInterface.UiBuilder.DeviceHandle;
var modelEditorWindow = new ModelEditorWindow(project, previewService, deviceHandle);
windowSystem.AddWindow(modelEditorWindow);
modelEditorWindow.IsOpen = config.ModelEditorWindowOpen;
```

Add `modelEditorWindow` as a field:

```csharp
private readonly ModelEditorWindow modelEditorWindow;
```

Add the `using SkinTatoo.DirectX;` if needed for the namespace, and `using SkinTatoo.Gui;` is already there.

- [ ] **Step 3: Wire up dispose**

In `Dispose()`, before `windowSystem.RemoveAllWindows()`:

```csharp
modelEditorWindow.Dispose();
```

- [ ] **Step 4: Save window state**

In `SaveWindowStates()`:

```csharp
config.ModelEditorWindowOpen = modelEditorWindow.IsOpen;
```

- [ ] **Step 5: Add a button in MainWindow toolbar to open 3D editor**

In `MainWindow`, add a reference to ModelEditorWindow:

```csharp
public ModelEditorWindow? ModelEditorWindowRef { get; set; }
```

Set it in Plugin constructor:

```csharp
mainWindow.ModelEditorWindowRef = modelEditorWindow;
```

Add a toolbar button in `DrawToolbar()`:

```csharp
if (ImGui.Button("3D 编辑"))
{
    if (ModelEditorWindowRef != null)
        ModelEditorWindowRef.IsOpen = !ModelEditorWindowRef.IsOpen;
}
ImGui.SameLine();
```

- [ ] **Step 6: Notify ModelEditorWindow when mesh changes**

In `PreviewService.LoadMesh()` or wherever mesh is loaded, add an event or callback. Alternatively, in `MainWindow` where mesh load is triggered, call:

```csharp
ModelEditorWindowRef?.OnMeshChanged();
```

- [ ] **Step 7: Notify ModelEditorWindow when textures change**

After `previewService.ApplyPendingSwaps()` in `MainWindow.Draw()`:

```csharp
ModelEditorWindowRef?.MarkTexturesDirty();
```

Or more precisely, after any preview update completes.

- [ ] **Step 8: Build to verify**

Run: `dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 9: Commit**

```bash
git add SkinTatoo/SkinTatoo/Plugin.cs SkinTatoo/SkinTatoo/Configuration.cs SkinTatoo/SkinTatoo/Gui/MainWindow.cs SkinTatoo/SkinTatoo/Gui/ModelEditorWindow.cs
git commit -m "feat: wire ModelEditorWindow into plugin lifecycle"
```

---

## Task 13: In-game integration test

- [ ] **Step 1: Build release**

Run: `dotnet build -c Release`
Expected: Build succeeded with no errors

- [ ] **Step 2: Test ClipMode**

1. Load the plugin in game (`/skintatoo`)
2. Add a decal layer with a full-body tattoo image
3. Change Clip dropdown to "切左半" — verify left half of decal disappears
4. Change to "切右半" — verify right half disappears
5. Change to "切上半"/"切下半" — verify vertical clip
6. Change back to "无" — verify full decal restored

- [ ] **Step 3: Test 3D Editor window**

1. Click "3D 编辑" button in toolbar — verify 3D window opens
2. Verify character mesh is displayed with Blinn-Phong shading
3. Right-drag to rotate camera around model
4. Middle-drag to pan camera
5. Ctrl+scroll to zoom camera
6. Click "重置相机" to verify camera resets

- [ ] **Step 4: Test ray-picking and decal placement**

1. Select a decal layer in the main window
2. Left-click on the 3D model — verify decal UvCenter updates to the clicked position
3. Verify the 2D canvas in MainWindow updates to show the new position
4. Verify the in-game preview updates (via GPU swap)
5. Left-drag on model — verify decal follows the mouse across the surface
6. Scroll wheel — verify decal scales up/down
7. Hold R + left-drag — verify decal rotates

- [ ] **Step 5: Fix any issues found during testing**

Address any build errors, rendering artifacts, or interaction bugs discovered.

- [ ] **Step 6: Final commit**

```bash
git add -A
git commit -m "fix: address issues from integration testing"
```

---

## Notes

### SharpDX Vector3 vs System.Numerics.Vector3

The project uses `System.Numerics` throughout (MeshData, DecalLayer, etc.) but SharpDX has its own `SharpDX.Vector3`/`SharpDX.Matrix`. Conversion is needed at the boundary:
- `OrbitCamera` and `DxRenderer` use SharpDX types internally
- `RayPicker` uses System.Numerics (same as MeshData)
- `ModelEditorWindow` converts between the two as needed

### Dalamud Device Handle

`PluginInterface.UiBuilder.DeviceHandle` returns a `nint` that can be used with `new SharpDX.Direct3D11.Device(handle)`. This creates a SharpDX wrapper around the existing game device — it does NOT create a new device. VFXEditor-CN validates this pattern.

### State Save/Restore

Critical: DxRenderer.BeginFrame saves and EndFrame restores the DX11 pipeline state (RenderTargets, RasterizerState, DepthStencilState). Without this, rendering to our off-screen target would corrupt the game's rendering.

### Future Work: Shrinkwrap Projection

Documented in the design spec at `docs/superpowers/specs/2026-04-06-3d-decal-editor-design.md` under "Future Work". Key points:
- Requires per-triangle projection instead of UV-space rectangle
- Needs tangent space construction for decal orientation
- UV seam handling at island boundaries
- Estimated 3-4x complexity vs current UV positioning approach
