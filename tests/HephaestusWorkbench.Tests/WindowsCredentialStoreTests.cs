using HephaestusWorkbench.Core.Services;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public async Task WriteReadDeleteAsync_RoundTripsWithoutPersistingThroughApplicationStorage()
    {
        var store = new WindowsCredentialStore();
        var target = $"HephaestusWorkbench.Tests/{Guid.NewGuid():N}";
        var secret = $"测试密码-{Guid.NewGuid():N}";

        try
        {
            await store.WriteAsync(target, "test-user", new SshCredentialSecret(secret));

            var restored = await store.ReadAsync(target);

            Assert.NotNull(restored);
            Assert.Equal("test-user", restored.UserName);
            Assert.Equal(secret, restored.Secret.Value);
            Assert.DoesNotContain(secret, restored.ToString(), StringComparison.Ordinal);
            Assert.True(await store.DeleteAsync(target));
            Assert.Null(await store.ReadAsync(target));
            Assert.False(await store.DeleteAsync(target));
        }
        finally
        {
            await store.DeleteAsync(target);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OperationsAsync_RejectEmptyTargetWithChineseError(string target)
    {
        var store = new WindowsCredentialStore();

        var write = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.WriteAsync(target, "user", new SshCredentialSecret("secret")));
        var read = await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(target));
        var delete = await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(target));

        Assert.Contains("凭据目标", write.Message, StringComparison.Ordinal);
        Assert.Contains("凭据目标", read.Message, StringComparison.Ordinal);
        Assert.Contains("凭据目标", delete.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_HonorsPreCanceledTokenBeforeCallingWindows()
    {
        var store = new WindowsCredentialStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WriteAsync(
                $"HephaestusWorkbench.Tests/{Guid.NewGuid():N}",
                "user",
                new SshCredentialSecret("secret"),
                cancellation.Token));
    }
}
