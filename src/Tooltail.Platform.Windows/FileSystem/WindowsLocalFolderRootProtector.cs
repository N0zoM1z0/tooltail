using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Tooltail.Application.Abstractions;

namespace Tooltail.Platform.Windows.FileSystem;

public sealed class WindowsLocalFolderRootProtector : ILocalFolderRootProtector
{
    private const int MaximumCanonicalRootCharacters = 32_767;
    private const int MaximumProtectedBytes = 64 * 1024;
    private const int MaximumClearBytes = MaximumCanonicalRootCharacters * 4;
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy =
        Encoding.ASCII.GetBytes("Tooltail.LocalFolderRoot.v1");
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public RootProtectionResult Protect(string canonicalRoot)
    {
        if (!OperatingSystem.IsWindows() ||
            string.IsNullOrWhiteSpace(canonicalRoot) ||
            canonicalRoot.Length > MaximumCanonicalRootCharacters ||
            canonicalRoot.Contains('\0', StringComparison.Ordinal))
        {
            return ProtectionFailure("root_protection.input_invalid");
        }

        byte[] clearBytes = StrictUtf8.GetBytes(canonicalRoot);
        try
        {
            byte[]? protectedBytes = Transform(clearBytes, protect: true);
            return protectedBytes is { Length: > 0 and <= MaximumProtectedBytes }
                ? new RootProtectionResult(
                    true,
                    "root_protection.protected",
                    protectedBytes)
                : ProtectionFailure("root_protection.failed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    public RootUnprotectionResult Unprotect(
        ReadOnlySpan<byte> protectedCanonicalRoot)
    {
        if (!OperatingSystem.IsWindows() ||
            protectedCanonicalRoot.Length is < 1 or > MaximumProtectedBytes)
        {
            return UnprotectionFailure("root_unprotection.input_invalid");
        }

        byte[] protectedBytes = protectedCanonicalRoot.ToArray();
        byte[]? clearBytes = null;
        try
        {
            clearBytes = Transform(protectedBytes, protect: false);
            if (clearBytes is not { Length: > 0 and <= MaximumClearBytes })
            {
                return UnprotectionFailure("root_unprotection.failed");
            }

            string canonicalRoot;
            try
            {
                canonicalRoot = StrictUtf8.GetString(clearBytes);
            }
            catch (DecoderFallbackException)
            {
                return UnprotectionFailure("root_unprotection.encoding_invalid");
            }

            return !string.IsNullOrWhiteSpace(canonicalRoot) &&
                canonicalRoot.Length <= MaximumCanonicalRootCharacters &&
                !canonicalRoot.Contains('\0', StringComparison.Ordinal)
                ? new RootUnprotectionResult(
                    true,
                    "root_unprotection.unprotected",
                    canonicalRoot)
                : UnprotectionFailure("root_unprotection.value_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (clearBytes is not null)
            {
                CryptographicOperations.ZeroMemory(clearBytes);
            }
        }
    }

    private static byte[]? Transform(byte[] inputBytes, bool protect)
    {
        IntPtr inputPointer = IntPtr.Zero;
        IntPtr entropyPointer = IntPtr.Zero;
        DataBlob output = default;
        try
        {
            inputPointer = Marshal.AllocHGlobal(inputBytes.Length);
            Marshal.Copy(inputBytes, 0, inputPointer, inputBytes.Length);
            entropyPointer = Marshal.AllocHGlobal(Entropy.Length);
            Marshal.Copy(Entropy, 0, entropyPointer, Entropy.Length);
            DataBlob input = new(inputBytes.Length, inputPointer);
            DataBlob entropy = new(Entropy.Length, entropyPointer);
            bool succeeded = protect
                ? CryptProtectData(
                    ref input,
                    null,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output)
                : CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output);
            if (!succeeded || output.Data == IntPtr.Zero || output.Length <= 0 ||
                output.Length > (protect ? MaximumProtectedBytes : MaximumClearBytes))
            {
                return null;
            }

            byte[] result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            ZeroAndFree(inputPointer, inputBytes.Length, localAlloc: false);
            ZeroAndFree(entropyPointer, Entropy.Length, localAlloc: false);
            ZeroAndFree(output.Data, output.Length, localAlloc: true);
        }
    }

    private static void ZeroAndFree(IntPtr pointer, int length, bool localAlloc)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        if (length is > 0 and <= MaximumClearBytes)
        {
            Marshal.Copy(new byte[length], 0, pointer, length);
        }

        if (localAlloc)
        {
            _ = LocalFree(pointer);
        }
        else
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static RootProtectionResult ProtectionFailure(string reasonCode) =>
        new(false, reasonCode, null);

    private static RootUnprotectionResult UnprotectionFailure(string reasonCode) =>
        new(false, reasonCode, null);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DataBlob(int length, IntPtr data)
    {
        public readonly int Length = length;

        public readonly IntPtr Data = data;
    }

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
