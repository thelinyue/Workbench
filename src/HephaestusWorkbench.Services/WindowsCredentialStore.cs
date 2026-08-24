using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 使用 Windows Credential Manager 保存 SSH 密码或私钥口令。
/// 应用数据库和 JSON 仅保存 target；敏感内容由 Windows 按当前用户安全上下文管理。
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public Task WriteAsync(
        string target,
        string userName,
        SshCredentialSecret secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        ValidateTarget(target);
        ArgumentNullException.ThrowIfNull(secret);

        var secretBytes = Encoding.UTF8.GetBytes(secret.Value);
        var blob = secretBytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            if (secretBytes.Length > 0)
                Marshal.Copy(secretBytes, 0, blob, secretBytes.Length);

            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = secretBytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = userName ?? string.Empty
            };

            if (!CredWrite(ref credential, 0))
                throw CreateNativeException($"无法写入 Windows 凭据“{target}”");
        }
        finally
        {
            if (blob != IntPtr.Zero)
            {
                Marshal.Copy(new byte[secretBytes.Length], 0, blob, secretBytes.Length);
                Marshal.FreeHGlobal(blob);
            }
            CryptographicOperations.ZeroMemory(secretBytes);
        }

        return Task.CompletedTask;
    }

    public Task<SshStoredCredential?> ReadAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        ValidateTarget(target);

        if (!CredRead(target, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
                return Task.FromResult<SshStoredCredential?>(null);
            throw CreateNativeException($"无法读取 Windows 凭据“{target}”", error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            var secretBytes = new byte[credential.CredentialBlobSize];
            try
            {
                if (secretBytes.Length > 0)
                    Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
                var secret = Encoding.UTF8.GetString(secretBytes);
                return Task.FromResult<SshStoredCredential?>(new SshStoredCredential(
                    credential.UserName ?? string.Empty,
                    new SshCredentialSecret(secret)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public Task<bool> DeleteAsync(string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        ValidateTarget(target);

        if (CredDelete(target, CredentialTypeGeneric, 0))
            return Task.FromResult(true);

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound)
            return Task.FromResult(false);
        throw CreateNativeException($"无法删除 Windows 凭据“{target}”", error);
    }

    private static void ValidateTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Windows 凭据目标不能为空。", nameof(target));
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("SSH 凭据存储仅支持 Windows Credential Manager。");
    }

    private static Win32Exception CreateNativeException(string message, int? error = null)
    {
        var errorCode = error ?? Marshal.GetLastWin32Error();
        return new Win32Exception(errorCode, $"{message}，Windows 错误码：{errorCode}。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
