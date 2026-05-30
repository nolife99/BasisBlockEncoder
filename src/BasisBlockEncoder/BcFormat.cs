namespace BasisBlockEncoder;

/// <summary>
/// Block-compressed GPU texture formats supported by <see cref="BlockEncoder"/>.
/// Numeric values match the native ABI and are stable.
/// </summary>
public enum BcFormat
{
    /// <summary>BC1 (DXT1): 8 bytes per 4x4 block, RGB with optional 1-bit alpha.</summary>
    Bc1 = 1,

    /// <summary>BC3 (DXT5): 16 bytes per block, RGBA (BC4 alpha block + BC1 color block).</summary>
    Bc3 = 3,

    /// <summary>BC4 (ATI1 / RGTC1): 8 bytes per block, single channel.</summary>
    Bc4 = 4,

    /// <summary>BC5 (ATI2 / RGTC2): 16 bytes per block, two channels (red+green by default).</summary>
    Bc5 = 5,

    /// <summary>BC6H (unsigned): 16 bytes per block, RGB half-float (HDR).</summary>
    Bc6h = 6,

    /// <summary>BC7: 16 bytes per block, high-quality RGBA.</summary>
    Bc7 = 7,
}
