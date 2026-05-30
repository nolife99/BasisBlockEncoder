using System;

namespace BasisBlockEncoder;

/// <summary>
/// Thrown when a native encode call returns a non-zero <c>bbe_result</c> code.
/// </summary>
public sealed class BasisEncoderException : Exception
{
    /// <summary>The raw native result code (see the native <c>bbe_result</c> enum).</summary>
    public int ResultCode { get; }

    /// <summary>The native function that failed.</summary>
    public string NativeFunction { get; }

    internal BasisEncoderException(int resultCode, string nativeFunction)
        : base(BuildMessage(resultCode, nativeFunction))
    {
        ResultCode = resultCode;
        NativeFunction = nativeFunction;
    }

    private static string BuildMessage(int code, string fn)
    {
        string reason = code switch
        {
            1 => "the native encoder was not initialized",
            2 => "a null pointer was passed",
            3 => "invalid arguments (format, dimensions, or stride)",
            4 => "the destination buffer was too small",
            _ => "unknown error",
        };
        return $"{fn} failed with code {code}: {reason}.";
    }
}
