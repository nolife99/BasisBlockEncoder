using System;

namespace Bcn;

[Flags]
public enum Bc7Flags : uint
{
    None = 0,
    Use2SubsetsRgb = 1, Use2SubsetsRgba = 2, Use3SubsetsRgb = 4,
    UseDualPlaneRgb = 8, UseDualPlaneRgba = 16,
    PBitOpt = 32, PBitOptMode6 = 64,
    UseTrivialMode6 = 128,
    PartiallyAnalyticalRgb = 256, PartiallyAnalyticalRgba = 512,
    // Only the analytical-path configuration is exposed here; the non-analytical re-check passes are not implemented.

    DefaultFast = Use2SubsetsRgb | Use2SubsetsRgba | UseDualPlaneRgba | PBitOpt | UseTrivialMode6,

    // Matches bc7f's cPackBC7FlagDefault (all analytical modes; no partially/non-analytical re-check passes).
    Default = Use2SubsetsRgb | Use2SubsetsRgba | Use3SubsetsRgb | UseDualPlaneRgb | UseDualPlaneRgba | PBitOpt | PBitOptMode6 | UseTrivialMode6,
}
