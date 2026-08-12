using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace HephaestusWorkbench.Services;

/// <summary>使用当前 Windows 用户的 DPAPI 保护短秘密值，令牌文件只保存密文。</summary>
internal static class DpapiSecretStore
{
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptUnprotectData(ref DataBlob dataIn, out IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static void ProtectToFile(string path, string secret)
    {
        var input = Encoding.UTF8.GetBytes(secret);
        var handle = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            var source = new DataBlob { Size = input.Length, Data = handle.AddrOfPinnedObject() };
            if (!CryptProtectData(ref source, "Hephaestus Workbench rule publisher token", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output)) throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI 保护令牌失败");
            try
            {
                var protectedBytes = new byte[output.Size]; Marshal.Copy(output.Data, protectedBytes, 0, protectedBytes.Length);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, protectedBytes);
            }
            finally { LocalFree(output.Data); }
        }
        finally { handle.Free(); }
    }

    public static string ReadFromFile(string path)
    {
        var protectedBytes = File.ReadAllBytes(path); var handle = GCHandle.Alloc(protectedBytes, GCHandleType.Pinned);
        try
        {
            var source = new DataBlob { Size = protectedBytes.Length, Data = handle.AddrOfPinnedObject() };
            if (!CryptUnprotectData(ref source, out var description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output)) throw new Win32Exception(Marshal.GetLastWin32Error(), "DPAPI 解密令牌失败");
            try { var secretBytes = new byte[output.Size]; Marshal.Copy(output.Data, secretBytes, 0, secretBytes.Length); return Encoding.UTF8.GetString(secretBytes); }
            finally { if (description != IntPtr.Zero) LocalFree(description); LocalFree(output.Data); }
        }
        finally { handle.Free(); }
    }
}
