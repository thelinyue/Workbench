using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 为交互终端和独立命令创建一次性 SSH.NET 连接。连接前只接受仓储中已经显式确认的 Host Key，
/// 未知或变化的密钥均拒绝本次连接，且本服务绝不自动写入 TOFU 记录。
/// </summary>
internal sealed class SshNetConnectionOpener(
    ISshHostKeyRepository hostKeys,
    ICredentialStore credentials,
    ISshNetClientFactory clients)
{
    public async Task<ISshNetClient> OpenAsync(
        SshConnectionRequest request,
        SshCredentialSecret? suppliedCredential,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var secret = await ResolveCredentialAsync(request, suppliedCredential, cancellationToken).ConfigureAwait(false);
        var knownHostKey = await hostKeys.GetAsync(request.Host, request.Port, cancellationToken).ConfigureAwait(false);
        var client = clients.Create(new SshClientConfiguration(request, secret?.Value));
        SshHostKeyObservation? rejectedObservation = null;
        SshHostKeyFailureReason? rejectedReason = null;

        try
        {
            await client.ConnectAsync(candidate =>
            {
                var observation = new SshHostKeyObservation(
                    request.Host,
                    request.Port,
                    candidate.KeyAlgorithm,
                    NormalizeFingerprint(candidate.Fingerprint));

                if (knownHostKey is null)
                {
                    rejectedObservation = observation;
                    rejectedReason = SshHostKeyFailureReason.Unknown;
                    return false;
                }

                if (!string.Equals(knownHostKey.KeyAlgorithm, observation.KeyAlgorithm, StringComparison.Ordinal) ||
                    !string.Equals(NormalizeFingerprint(knownHostKey.Fingerprint), observation.Fingerprint, StringComparison.Ordinal))
                {
                    rejectedObservation = observation;
                    rejectedReason = SshHostKeyFailureReason.Changed;
                    return false;
                }

                return true;
            }, cancellationToken).ConfigureAwait(false);

            return client;
        }
        catch (Exception) when (rejectedObservation is not null && rejectedReason is not null)
        {
            await DisposeFailedClientAsync(client).ConfigureAwait(false);
            throw new SshHostKeyValidationException(
                rejectedReason.Value,
                rejectedObservation,
                knownHostKey?.KeyAlgorithm,
                knownHostKey?.Fingerprint);
        }
        catch
        {
            await DisposeFailedClientAsync(client).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<SshCredentialSecret?> ResolveCredentialAsync(
        SshConnectionRequest request,
        SshCredentialSecret? suppliedCredential,
        CancellationToken cancellationToken)
    {
        if (suppliedCredential is not null)
            return suppliedCredential;

        if (!string.IsNullOrWhiteSpace(request.CredentialTarget))
        {
            var stored = await credentials.ReadAsync(request.CredentialTarget, cancellationToken).ConfigureAwait(false);
            if (stored is null)
                throw new InvalidOperationException($"未找到 SSH 凭据目标“{request.CredentialTarget}”，请重新输入凭据。");
            return stored.Secret;
        }

        if (request.AuthenticationMethod == SshAuthenticationMethod.Password)
            throw new InvalidOperationException("密码认证需要提供密码或可读取的凭据目标。");

        return null;
    }

    private static void ValidateRequest(SshConnectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Host))
            throw new ArgumentException("SSH 主机不能为空。", nameof(request));
        if (request.Port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(request), "SSH 端口必须在 1 到 65535 之间。");
        if (string.IsNullOrWhiteSpace(request.Username))
            throw new ArgumentException("SSH 用户名不能为空。", nameof(request));
        if (request.AuthenticationMethod == SshAuthenticationMethod.PrivateKey && string.IsNullOrWhiteSpace(request.PrivateKeyPath))
            throw new ArgumentException("私钥认证需要提供私钥文件路径。", nameof(request));
    }

    private static string NormalizeFingerprint(string fingerprint) =>
        fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) ? fingerprint : $"SHA256:{fingerprint}";

    private static async ValueTask DisposeFailedClientAsync(ISshNetClient client)
    {
        try
        {
            client.Disconnect();
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
