using System.Runtime.InteropServices;

namespace Clircs.Infrastructure;

public static class WindowsDataProtection
{
    public static byte[] Protect(byte[] value, byte[] entropy) => Transform(value, entropy, protect: true);

    public static byte[] Unprotect(byte[] value, byte[] entropy) => Transform(value, entropy, protect: false);

    private static byte[] Transform(byte[] value, byte[] entropy, bool protect)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(entropy);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Encrypted secrets currently require Windows.");
        }

        using var input = DataBlob.Create(value);
        using var optionalEntropy = DataBlob.Create(entropy);
        DataBlob output;
        var succeeded = protect
            ? CryptProtectData(ref input.Value, null, ref optionalEntropy.Value, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
            : CryptUnprotectData(ref input.Value, IntPtr.Zero, ref optionalEntropy.Value, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
        if (!succeeded)
        {
            throw new InvalidOperationException($"Windows could not {(protect ? "protect" : "unprotect")} secret data.");
        }

        try
        {
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
        public static BlobHandle Create(byte[] bytes) => new(bytes);
    }

    private sealed class BlobHandle : IDisposable
    {
        public BlobHandle(byte[] bytes)
        {
            Value = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)) };
            if (bytes.Length > 0) Marshal.Copy(bytes, 0, Value.Data, bytes.Length);
        }

        public DataBlob Value;

        public void Dispose()
        {
            if (Value.Data == IntPtr.Zero) return;
            Marshal.FreeHGlobal(Value.Data);
            Value.Data = IntPtr.Zero;
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
