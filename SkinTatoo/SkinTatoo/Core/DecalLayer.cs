using System.Numerics;

namespace SkinTatoo.Core;

public enum BlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    SoftLight,
    HardLight,
    Darken,
    Lighten,
    ColorDodge,
    ColorBurn,
    Difference,
    Exclusion,
}

// Renamed from EmissiveMask. Semantics widened: the shape now controls
// the ENTIRE layer's participation weight (all PBR fields fade together,
// not just emissive). Enum values and ordering preserved for compat.
public enum LayerFadeMask
{
    Uniform,
    RadialFadeOut,
    RadialFadeIn,
    EdgeGlow,
    DirectionalGradient,
    GaussianFeather,
    ShapeOutline,
}

public enum ClipMode
{
    None,
    ClipLeft,
    ClipRight,
    ClipTop,
    ClipBottom,
}

public class DecalLayer
{
    // New in v1 PBR: layer type
    public LayerKind Kind { get; set; } = LayerKind.Decal;

    public string Name { get; set; } = "New Decal";
    public string? ImagePath { get; set; }

    // UV-space placement — only meaningful when Kind == Decal
    public Vector2 UvCenter { get; set; } = new(0.5f, 0.5f);
    public Vector2 UvScale { get; set; } = new(0.2f, 0.2f);
    public float RotationDeg { get; set; } = 0f;

    public float Opacity { get; set; } = 1.0f;
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;
    public ClipMode Clip { get; set; } = ClipMode.None;
    public bool IsVisible { get; set; } = true;

    // Field-level PBR affect switches (G1 granularity per spec Q6-B)
    public bool AffectsDiffuse { get; set; } = true;
    public bool AffectsSpecular { get; set; } = false;
    public bool AffectsEmissive { get; set; } = false;
    public bool AffectsRoughness { get; set; } = false;
    public bool AffectsMetalness { get; set; } = false;
    public bool AffectsSheen { get; set; } = false;   // Sheen Rate/Tint/Aperture combined

    // PBR field values
    public Vector3 DiffuseColor { get; set; } = new(1f, 1f, 1f);
    public Vector3 SpecularColor { get; set; } = new(1f, 1f, 1f);
    public Vector3 EmissiveColor { get; set; } = new(1f, 1f, 1f);
    public float EmissiveIntensity { get; set; } = 1.0f;
    public float Roughness { get; set; } = 0.5f;
    public float Metalness { get; set; } = 0f;
    public float SheenRate { get; set; } = 0.1f;
    public float SheenTint { get; set; } = 0.2f;        // single Half at offset [13], NOT RGB
    public float SheenAperture { get; set; } = 5.0f;

    // Layer fade mask (renamed from EmissiveMask*; applies to ALL PBR fields now)
    public LayerFadeMask FadeMask { get; set; } = LayerFadeMask.Uniform;
    public float FadeMaskFalloff { get; set; } = 0.5f;
    public float GradientAngleDeg { get; set; } = 0f;
    public float GradientScale { get; set; } = 1f;
    public float GradientOffset { get; set; } = 0f;

    // Runtime-only: which ColorTable row pair is assigned to this layer (0-15).
    // Not persisted — SavedLayer is the serialization middleman and doesn't carry this field,
    // so vanilla scan results re-compute every session (intentional per spec).
    public int AllocatedRowPair { get; set; } = -1;

    /// <summary>True if any PBR field is enabled — gate for row pair allocation.</summary>
    public bool RequiresRowPair =>
        AffectsDiffuse || AffectsSpecular || AffectsEmissive
        || AffectsRoughness || AffectsMetalness || AffectsSheen;
}
