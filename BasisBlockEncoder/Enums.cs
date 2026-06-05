// Enums.cs — public configuration types for the managed BasisBlockEncoder drop-in.
//
// These mirror the original native-backed package's surface 1:1 (same names, same numeric values) so existing
// callers compile and behave the same. The Bc7Flags bit layout matches basis_universal's cPackBC7Flag* values.
using System;

namespace BasisBlockEncoder;

/// <summary>Target block-compression format. (BC2 is intentionally absent, matching the original package.)</summary>
public enum BcFormat
{
    /// <summary>BC1 — RGB (+1-bit alpha), 8 bytes/block.</summary>
    Bc1 = 0,
    /// <summary>BC3 — BC4 alpha block + BC1 color block, 16 bytes/block.</summary>
    Bc3 = 1,
    /// <summary>BC4 — single channel, 8 bytes/block.</summary>
    Bc4 = 2,
    /// <summary>BC5 — two channels, 16 bytes/block.</summary>
    Bc5 = 3,
    /// <summary>BC6H — unsigned HDR RGB (FP16), 16 bytes/block.</summary>
    Bc6h = 4,
    /// <summary>BC7 — high quality RGBA, 16 bytes/block.</summary>
    Bc7 = 5,
}

/// <summary>Quality level for the BC1/BC3 color block.</summary>
public enum Bc1Quality
{
    /// <summary>Fast path.</summary>
    Fast = 0,
    /// <summary>Higher quality (more refinement).</summary>
    HighQuality = 1,
}

/// <summary>Quality level for the BC6H encoder.</summary>
public enum Bc6hQuality
{
    /// <summary>Fastest, lowest quality.</summary>
    Fast = 0,
    /// <summary>Recommended balance.</summary>
    Default = 1,
    /// <summary>Highest quality.</summary>
    HighQuality = 2,
}

/// <summary>
/// Low-level configuration flags for the BC7 encoder. Bit values map 1:1 onto basis_universal's
/// <c>cPackBC7Flag*</c> values; most callers should use a preset (<see cref="Default"/>, <see cref="Fast"/>, ...).
/// </summary>
/// <remarks>
/// The managed backend implements the analytical/predictive mode families. The optional non-analytical
/// brute-force refinement passes (<see cref="NonAnalyticalRgb"/>/<see cref="NonAnalyticalRgba"/>) are not
/// performed; those bits are accepted for source compatibility but do not enable extra work, so
/// <see cref="HighQuality"/> resolves to the full analytical search.
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
    /// <summary>Brute-force RGB. Accepted for compatibility; not performed by the managed backend.</summary>
    NonAnalyticalRgb = 1024,
    /// <summary>Brute-force RGBA. Accepted for compatibility; not performed by the managed backend.</summary>
    NonAnalyticalRgba = 2048,

    /// <summary>Fastest, lowest quality: mode 6 only.</summary>
    Fastest = UseTrivialMode6,
    /// <summary>Mode 6 (+p-bits) for RGB; dual-plane modes for alpha.</summary>
    Faster = UseTrivialMode6 | PBitOptMode6 | UseDualPlaneRgba,
    /// <summary>Two-subset RGB/RGBA + dual-plane alpha + p-bit optimization.</summary>
    Fast = Use2SubsetsRgb | Use2SubsetsRgba | UseDualPlaneRgba | PBitOpt | UseTrivialMode6,
    /// <summary>Recommended default: all analytical mode families, dual-plane, and p-bit optimization.</summary>
    Default = Use2SubsetsRgb | Use2SubsetsRgba | Use3SubsetsRgb | UseDualPlaneRgb | UseDualPlaneRgba | PBitOpt | PBitOptMode6 | UseTrivialMode6,
    /// <summary>Maximum quality (analytical search; non-analytical passes are not performed by this backend).</summary>
    HighQuality = Default | PartiallyAnalyticalRgb | PartiallyAnalyticalRgba | NonAnalyticalRgb | NonAnalyticalRgba,
}
