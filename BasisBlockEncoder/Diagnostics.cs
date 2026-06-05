// Diagnostics.cs — exception type and the (now inert) NativeMethods type, kept for source compatibility
// with the original native-backed package. The managed backend performs no native interop, so these are
// essentially vestigial: BasisEncoderException is virtually never thrown (argument validation surfaces as
// ArgumentException), and NativeMethods exposes nothing callable.
using System;

namespace BasisBlockEncoder;

/// <summary>
/// Thrown when an encode call fails. Retained for API compatibility with the native-backed package; the
/// managed backend reports invalid arguments as <see cref="ArgumentException"/> and does not raise native
/// result codes, so this is rarely (if ever) seen in the managed build.
/// </summary>
public sealed class BasisEncoderException : Exception
{
    /// <summary>The raw result code (0 in the managed build; retained for compatibility).</summary>
    public int ResultCode { get; }

    /// <summary>The function that failed.</summary>
    public string NativeFunction { get; }

    public BasisEncoderException(string nativeFunction, int resultCode)
        : base($"{nativeFunction} failed with result {resultCode}.")
    {
        NativeFunction = nativeFunction;
        ResultCode = resultCode;
    }

    public BasisEncoderException(string message) : base(message)
    {
        NativeFunction = string.Empty;
        ResultCode = 0;
    }
}

/// <summary>
/// Compatibility placeholder. The original package exposed P/Invoke entry points here; the managed drop-in
/// has no native library, so this type intentionally has no callable members. Use <see cref="BlockEncoder"/>.
/// </summary>
[Obsolete("The managed BasisBlockEncoder has no native interop; use BlockEncoder. This type is retained only for source compatibility.")]
public static class NativeMethods
{
}
