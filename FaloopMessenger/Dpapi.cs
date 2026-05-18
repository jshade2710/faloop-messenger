using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FaloopMessenger;

// Windows DPAPI (CurrentUser scope) via P/Invoke — no extra NuGet, and FFXIV
// is Windows-only so crypt32 is always present. Used to keep the Faloop
// password from sitting in the plugin config as plaintext on disk. The blob
// is bound to the Windows user account; copying the config file to another
// machine/user simply yields an empty password (caller treats that as
// "not set" and falls back to anonymous), never a crash.
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int    cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    // Encrypt → base64. Returns "" for empty input or on any failure (the
    // caller persists "" rather than risking a crash on Save()).
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;

        var data    = Encoding.UTF8.GetBytes(plain);
        var inBlob  = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        var pin     = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = data.Length;
            inBlob.pbData = pin.AddrOfPinnedObject();
            if (!CryptProtectData(ref inBlob, "FaloopMessenger", IntPtr.Zero,
                                   IntPtr.Zero, IntPtr.Zero, 0, ref outBlob))
                return string.Empty;

            var outBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
            return Convert.ToBase64String(outBytes);
        }
        catch { return string.Empty; }
        finally
        {
            pin.Free();
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    // base64 → decrypt. Returns "" on empty/garbled/foreign-user input.
    public static string Unprotect(string b64)
    {
        if (string.IsNullOrEmpty(b64)) return string.Empty;

        byte[] data;
        try { data = Convert.FromBase64String(b64); }
        catch { return string.Empty; }

        var inBlob  = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        var pin     = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = data.Length;
            inBlob.pbData = pin.AddrOfPinnedObject();
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero,
                                    IntPtr.Zero, IntPtr.Zero, 0, ref outBlob))
                return string.Empty;

            var outBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
            return Encoding.UTF8.GetString(outBytes);
        }
        catch { return string.Empty; }
        finally
        {
            pin.Free();
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
