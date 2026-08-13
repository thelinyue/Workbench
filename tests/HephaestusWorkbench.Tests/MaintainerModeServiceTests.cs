using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class MaintainerModeServiceTests
{
    [Fact]
    public void SettingsStore_RoundTripsThroughDpapiWithoutPlaintextFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"), "maintainer.dat");
        var settings = new MaintainerSettings("maintainer-key", "owner", "rules", "main", "rules/log-analyzer/versions");
        var store = new MaintainerSettingsStore(path);

        store.Save(settings);

        var restored = store.Load();
        Assert.Equal(settings, restored);
        Assert.DoesNotContain("maintainer-key", File.ReadAllText(path));
    }

    [Fact]
    public void SettingsStore_RejectsAbsoluteRulesPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"), "maintainer.dat");
        var store = new MaintainerSettingsStore(path);

        Assert.Throws<InvalidDataException>(() => store.Save(new MaintainerSettings("maintainer-key", "owner", "rules", "main", "C:\\rules")));
    }

    [Fact]
    public void Unlock_RequiresConfiguredKeyAndClearsAfterExit()
    {
        var service = new MaintainerModeService("maintainer-key");

        Assert.False(service.TryUnlock("wrong-key"));
        Assert.True(service.TryUnlock("maintainer-key"));
        Assert.True(service.IsUnlocked);

        service.Clear();

        Assert.False(service.IsUnlocked);
    }

    [Fact]
    public void Unlock_LocksAfterThreeFailures()
    {
        var service = new MaintainerModeService("maintainer-key");

        Assert.False(service.TryUnlock("wrong-1"));
        Assert.False(service.TryUnlock("wrong-2"));
        Assert.False(service.TryUnlock("wrong-3"));
        Assert.False(service.TryUnlock("maintainer-key"));
        Assert.True(service.GetLockoutRemaining() > TimeSpan.Zero);
    }

    [Fact]
    public void UnconfiguredService_CannotUnlock()
    {
        var service = new MaintainerModeService(null);

        Assert.False(service.IsConfigured);
        Assert.False(service.TryUnlock("anything"));
    }
}
