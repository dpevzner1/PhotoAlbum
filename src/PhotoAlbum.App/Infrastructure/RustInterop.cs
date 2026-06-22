using System.Runtime.InteropServices;
using System.Text;

namespace PhotoAlbum.App.Infrastructure;

/// <summary>
/// Raw P/Invoke declarations for photoalbum_core.dll.
/// All FFI entry points use i32 return codes; no Rust types cross the boundary.
/// </summary>
internal static partial class RustInterop
{
    private const string DllName = "photoalbum_core";

    internal const int ERR_OK = 0;
    internal const int ERR_INVALID_ARG = 1;
    internal const int ERR_NOT_FOUND = 2;
    internal const int ERR_IO = 3;
    internal const int ERR_HASH_MISMATCH = 4;
    internal const int ERR_BUFFER_TOO_SMALL = 5;
    internal const int ERR_INTERNAL = 99;

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int pa_health_check();

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int pa_hash_file(
        string path,
        [MarshalAs(UnmanagedType.LPArray)] byte[] outBuf,
        nuint outLen);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int pa_verify_file(string path, string expectedHex);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int pa_scan_directory(
        string rootPath,
        [MarshalAs(UnmanagedType.LPArray)] byte[] outJson,
        nuint outLen);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int pa_generate_thumbnail(
        string path,
        uint targetSize,
        [MarshalAs(UnmanagedType.LPArray)] byte[] outBuf,
        nuint outBufLen,
        out nuint outWritten);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int pa_copy_file_verified(string srcPath, string dstPath);

    /// <summary>
    /// Compute a 64-bit dHash (difference hash) for the image at <paramref name="path"/>.
    /// Writes the hash into <paramref name="outHash"/> on success.
    /// Returns ERR_OK, ERR_INVALID_ARG, or ERR_IO.
    /// </summary>
    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int pa_phash_file(string path, out ulong outHash);

    /// <summary>
    /// Call a Rust function that writes a UTF-8 C string into a caller-allocated buffer.
    /// Retries with a larger buffer if ERR_BUFFER_TOO_SMALL is returned.
    /// </summary>
    internal static string CallWithStringBuffer(Func<byte[], nuint, int> fn, int initialSize = 4096)
    {
        var buf = new byte[initialSize];
        var code = fn(buf, (nuint)buf.Length);
        if (code == ERR_BUFFER_TOO_SMALL)
        {
            buf = new byte[initialSize * 16];
            code = fn(buf, (nuint)buf.Length);
        }
        ThrowOnError(code);
        var len = Array.IndexOf(buf, (byte)0);
        return Encoding.UTF8.GetString(buf, 0, len < 0 ? buf.Length : len);
    }

    internal static void ThrowOnError(int code, string? context = null)
    {
        if (code == ERR_OK) return;
        var msg = code switch
        {
            ERR_INVALID_ARG => "Invalid argument",
            ERR_NOT_FOUND => "File not found",
            ERR_IO => "I/O error",
            ERR_HASH_MISMATCH => "Hash mismatch — file may be corrupted",
            ERR_BUFFER_TOO_SMALL => "Buffer too small",
            ERR_INTERNAL => "Internal Rust error (panic caught)",
            _ => $"Unknown error code {code}",
        };
        throw new RustException(context is null ? msg : $"{context}: {msg}", code);
    }
}

public sealed class RustException(string message, int code) : Exception(message)
{
    public int ErrorCode { get; } = code;
}
