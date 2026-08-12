using System.Text.Json;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;
using HephaestusWorkbench.Services;

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
}
