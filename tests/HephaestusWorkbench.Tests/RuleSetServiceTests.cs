using System.Text.Json;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;
using NSec.Cryptography;

namespace HephaestusWorkbench.Tests;

public sealed class RuleSetServiceTests
{
    [Fact]
    public async Task ImportAndActivate_StoresValidatedRulesInUserData()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        var service = new RuleSetService(paths, new WorkbenchLogger(root));
        var source = Path.Combine(root, "incoming.json");
        var rules = new RuleSet
        {
            Version = "1",
            Files = new() { new RuleFile { Name = "system.log", Category = "system", Keywords = new() { new RuleDefinition { Term = "ERROR", Result = "error", Severity = "warning" } } } }
        };
        await File.WriteAllTextAsync(source, JsonSerializer.Serialize(rules));

        var local = await service.ImportAsync(source);
        await service.ActivateAsync(local.Path);

        Assert.True(File.Exists(paths.ActiveRulesFile));
        Assert.Single(await service.ListLocalAsync());
        Assert.Equal("ERROR", (await service.ReadActiveAsync())!.Files[0].Keywords[0].Term);
    }

    [Fact]
    public void Validate_RejectsMalformedRuleFieldsAndDuplicates()
    {
        var service = new RuleSetService(new DataPaths(Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"))), new WorkbenchLogger(Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"))));
        var rules = new RuleSet
        {
            Version = "1",
            Files = new() { new RuleFile { Name = "system.log", Keywords = new()
            {
                new RuleDefinition { Term = "[", Regex = true, ContextLines = -1, ContextDirection = "sideways", SearchDirection = "sideways", Severity = "fatal" },
                new RuleDefinition { Term = "[", Regex = true }
            } } }
        };

        var issues = service.Validate(rules);

        Assert.True(issues.Count(issue => issue.IsError) >= 5, "应同时报告非法规则字段。");
    }

    [Fact]
    public async Task SaveUserRules_MergesNonConflictingRulesAndKeepsConflictsInactive()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        var service = new RuleSetService(paths, new WorkbenchLogger(root));
        await service.ApplyOfficialAsync(new RuleSet
        {
            Version = "2026.08.12",
            Files = new() { new RuleFile { Name = "syslog", Category = "系统日志", Keywords = new()
            {
                new RuleDefinition { Term = "same", Result = "维护者描述", ContextDirection = "down", SearchDirection = "down" }
            } } }
        });

        await service.SaveUserAsync(new UserRuleSet
        {
            BaseVersion = "2026.08.12",
            Rules = new()
            {
                new UserRuleRecord { LocalId = "local-add", File = "syslog", Category = "系统日志", Rule = new RuleDefinition { Term = "new", Result = "用户描述", ContextDirection = "down", SearchDirection = "down" } },
                new UserRuleRecord { LocalId = "local-conflict", File = "syslog", Category = "系统日志", Rule = new RuleDefinition { Term = "same", Result = "用户冲突描述", ContextDirection = "down", SearchDirection = "down" } }
            }
        });

        var state = await service.ReadEditorStateAsync();
        Assert.Contains(state.User.Rules, item => item.LocalId == "local-conflict" && item.Status == "conflict");
        Assert.Contains(state.User.Rules, item => item.LocalId == "local-add" && item.Status == "draft");
        var active = await service.ReadActiveAsync();
        Assert.NotNull(active);
        Assert.Contains(active!.Files.Single().Keywords, rule => rule.Term == "new");
        Assert.DoesNotContain(active.Files.Single().Keywords, rule => rule.Result == "用户冲突描述");
    }

    [Fact]
    public void SignedRulePackage_VerifiesAndRejectsOneByteTamper()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        var rulesService = new RuleSetService(paths, new WorkbenchLogger(root));
        var rules = new RuleSet
        {
            Version = "2026.08.13",
            Files = new()
            {
                new RuleFile
                {
                    Name = "system.log",
                    Category = "system",
                    Keywords = new() { new RuleDefinition { Term = "ERROR", Result = "error", Severity = "warning" } }
                }
            }
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(rules);
        var catalog = new RuleCatalogEntry
        {
            SchemaVersion = 1,
            RuleSetId = "log-analyzer",
            PluginId = "log-analyzer",
            Version = rules.Version,
            MinimumPluginVersion = "1.0.0",
            SignatureAlgorithm = Ed25519RulePackageVerifier.Algorithm,
            PackageUrl = "https://example.test/rules.json",
            PackageSize = payload.Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant(),
            KeyId = "test"
        };

        using var key = Key.Create(SignatureAlgorithm.Ed25519);
        catalog.Signature = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(
            key,
            Ed25519RulePackageVerifier.BuildSignedBytes(payload, catalog)));
        var verifier = new Ed25519RulePackageVerifier(
            rulesService,
            Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));

        Assert.Equal(rules.Version, verifier.VerifyAndRead(payload, catalog).Version);

        payload[0] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => verifier.VerifyAndRead(payload, catalog));
    }
}
