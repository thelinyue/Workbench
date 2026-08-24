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
    public async Task SaveUserRules_PersistsCategoryBeforeAnyRuleExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        var service = new RuleSetService(paths, new WorkbenchLogger(root));

        await service.SaveUserAsync(new UserRuleSet
        {
            Categories = new() { "磁盘健康" },
            Rules = new()
        });

        var saved = await service.ReadUserAsync();
        Assert.Equal(new[] { "磁盘健康" }, saved.Categories);
        Assert.Empty(saved.Rules);
    }

    [Fact]
    public void ValidateUserRules_RejectsRuleOutsideCreatedCategory()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var service = new RuleSetService(new DataPaths(root), new WorkbenchLogger(root));

        var issues = service.ValidateUserRules(new UserRuleSet
        {
            Categories = new() { "系统日志" },
            Rules = new()
            {
                new UserRuleRecord
                {
                    LocalId = "unknown-category",
                    File = "syslog",
                    Category = "网络连接",
                    Rule = new RuleDefinition { Term = "ERROR", Result = "错误" }
                }
            }
        });

        var issue = Assert.Single(issues);
        Assert.Equal("category", issue.Field);
        Assert.Equal(new[] { "unknown-category" }, issue.LocalIds);
        Assert.Contains("请先创建分类", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateUserRules_MapsFieldsAndDuplicateRuleIds()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var service = new RuleSetService(new DataPaths(root), new WorkbenchLogger(root));
        var issues = service.ValidateUserRules(new UserRuleSet
        {
            BaseVersion = "2026.08.15",
            Rules = new()
            {
                new UserRuleRecord { LocalId = "invalid", File = "syslog", Category = "系统", Rule = new RuleDefinition { Result = "缺少关键词" } },
                new UserRuleRecord { LocalId = "duplicate-a", File = "syslog", Category = "系统", Rule = new RuleDefinition { Term = "ERROR", Result = "错误" } },
                new UserRuleRecord { LocalId = "duplicate-b", File = "syslog", Category = "系统", Rule = new RuleDefinition { Term = "ERROR", Result = "错误" } }
            }
        });

        var termIssue = Assert.Single(issues, x => x.Message.Contains("关键词不能为空", StringComparison.Ordinal));
        Assert.Equal("term", termIssue.Field);
        Assert.Equal(new[] { "invalid" }, termIssue.LocalIds);

        var duplicateIssue = Assert.Single(issues, x => x.Message.Contains("规则重复", StringComparison.Ordinal));
        Assert.Equal(new[] { "duplicate-a", "duplicate-b" }, duplicateIssue.LocalIds);
    }

    [Fact]
    public async Task SaveUserRules_InvalidCurrentStateDoesNotWriteLocalFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(root);
        var service = new RuleSetService(paths, new WorkbenchLogger(root));
        var invalid = new UserRuleSet
        {
            Rules = new()
            {
                new UserRuleRecord { LocalId = "invalid", File = "syslog", Category = "系统", Rule = new RuleDefinition() }
            }
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveUserAsync(invalid));
        Assert.False(File.Exists(paths.LocalAdditionsFile));
    }


}
