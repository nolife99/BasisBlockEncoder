using System;

namespace BasisBlockEncoder;

/// <summary>
/// Low-level configuration flags for the BC7 (bc7f) encoder. These map 1:1 onto the native
/// <c>cPackBC7Flag*</c> values. Most callers should use one of the presets
/// (<see cref="Default"/>, <see cref="Fast"/>, <see cref="HighQuality"/>, ...).
/// </summary>
/// <remarks>
/// The encoder is analytical/predictive: higher presets enable more BC7 mode families and, at the
/// top end, optional non-analytical refinement that measures real SSE on hard blocks.
/// </remarks>
[Flags]
public enum Bc7Flags : uint
{
    /// <summary>No flags. The encoder substitutes <see cref="Default"/> when 0 is passed for BC7.</summary>
    None = 0,

    /// <summary>Use modes 1/3 (two subsets) for RGB blocks.</summary>
    Use2SubsetsRgb = 1,
    /// <summary>Use mode 7 (two subsets) for RGBA blocks.</summary>
    Use2SubsetsRgba = 2,
    /// <summary>Also use modes 0/2 (three subsets) for RGB blocks. Requires <see cref="Use2SubsetsRgb"/>.</summary>
    Use3SubsetsRgb = 4,
    /// <summary>Enable dual-plane modes 4/5 for RGB blocks.</summary>
    UseDualPlaneRgb = 8,
    /// <summary>Enable dual-plane modes 4/5 for RGBA blocks.</summary>
    UseDualPlaneRgba = 16,
    /// <summary>Optimize p-bits on some modes (slower, higher quality).</summary>
    PBitOpt = 32,
    /// <summary>Optimize p-bits on mode 6 (slower; fully-opaque alpha may become 254).</summary>
    PBitOptMode6 = 64,
    /// <summary>Use the fast trivial mode-6 path on very low-variance blocks (recommended).</summary>
    UseTrivialMode6 = 128,
    /// <summary>Partially analytical RGB: measure real SSE on complex blocks to resolve mode choice.</summary>
    PartiallyAnalyticalRgb = 256,
    /// <summary>Partially analytical RGBA: measure real SSE on complex blocks to resolve mode choice.</summary>
    PartiallyAnalyticalRgba = 512,
    /// <summary>Brute-force RGB (very slow). Must be combined with <see cref="PartiallyAnalyticalRgb"/>.</summary>
    NonAnalyticalRgb = 1024,
    /// <summary>Brute-force RGBA (very slow). Must be combined with <see cref="PartiallyAnalyticalRgba"/>.</summary>
    NonAnalyticalRgba = 2048,

    /// <summary>Fastest, lowest quality: mode 6 only.</summary>
    Fastest = UseTrivialMode6,

    /// <summary>Mode 6 (+p-bits) for RGB; dual-plane modes for alpha.</summary>
    Faster = PBitOpt | UseDualPlaneRgba | UseTrivialMode6,

    /// <summary>Two-subset RGB/RGBA + dual-plane alpha + p-bit optimization.</summary>
    Fast = Use2SubsetsRgb | Use2SubsetsRgba | UseDualPlaneRgba | PBitOpt | UseTrivialMode6,

    /// <summary>Recommended default: all analytical mode families, dual-plane, and p-bit optimization.</summary>
    Default = Use2SubsetsRgb | Use2SubsetsRgba | Use3SubsetsRgb |
              UseDualPlaneRgb | UseDualPlaneRgba |
              PBitOpt | PBitOptMode6 |
              UseTrivialMode6,

    /// <summary><see cref="Default"/> plus partially-analytical refinement (slower, higher PSNR).</summary>
    HighQuality = Default | PartiallyAnalyticalRgb | PartiallyAnalyticalRgba,
}

/// <summary>Quality setting for the BC1 / BC3 color block encoder.</summary>
public enum Bc1Quality : uint
{
    /// <summary>Single least-squares pass (fastest).</summary>
    Fast = 0,
    /// <summary>Higher-quality multi-pass encoding.</summary>
    HighQuality = 1,
}

/// <summary>Quality setting for the BC6H encoder.</summary>
public enum Bc6hQuality : uint
{
    /// <summary>Fast: single-pass, no high-quality least squares.</summary>
    Fast = 0,
    /// <summary>Default balance of speed and quality.</summary>
    Default = 1,
    /// <summary>High quality: more 2-subset patterns and brute-force weight assignment.</summary>
    HighQuality = 2,
}
