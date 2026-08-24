using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NSec.Cryptography;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

/// <summary>验证安装器定义、发布工作流和正式编译门禁的仓库级契约。</summary>
public sealed class InstallerDefinitionTests
{
    [Fact]
    public void ReleaseDefaults_TargetFormalV2Version()
    {
        var props = ReadRepositoryFile("Directory.Build.props");
        var buildScript = ReadRepositoryFile("installer", "build-installer.ps1");
        var innoSetup = ReadRepositoryFile("installer", "HephaestusWorkbench.iss");
        var workflow = ReadRepositoryFile(".github", "workflows", "release.yml");

        Assert.Contains("<Version>2.0.0</Version>", props);
        Assert.Contains("[string]$Version = '2.0.0'", buildScript);
        Assert.Contains("#define MyAppVersion \"2.0.0\"", innoSetup);
        Assert.Contains("default: \"2.0.0\"", workflow);
    }

    [Fact]
    public void InnoSetup_UsesStandardOfflineWizard()
    {
        var script = ReadRepositoryFile("installer", "HephaestusWorkbench.iss");

        Assert.Contains("WizardStyle=modern", script);
        Assert.Contains("LicenseFile=", script);
        Assert.Contains("AppName=Hephaestus工作台", script);
        Assert.Contains("AppVerName=Hephaestus工作台", script);
        Assert.Contains("VersionInfoProductName=Hephaestus工作台", script);
        Assert.Contains("OutputBaseFilename=HephaestusWorkbench_v{#MyAppVersion}", script);
        Assert.DoesNotContain("赫菲斯托斯工程工作台", script);
        Assert.Contains("VersionInfoProductVersion={#MyAppVersion}", script);
        Assert.Contains("Source: \"{#AppSource}\\*\"", script);
        Assert.DoesNotContain("PayloadPackage", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildScript_GeneratesOnlyOneApplicationInstaller()
    {
        var script = ReadRepositoryFile("installer", "build-installer.ps1");

        Assert.Contains("HephaestusWorkbench_v$Version.exe", script);
        Assert.DoesNotContain("HephaestusWorkbench_Update.exe", script);
        Assert.DoesNotContain("HephaestusWorkbench_Uninstall.exe", script);
        Assert.DoesNotContain("HephaestusWorkbench-v$Version-win-x64.zip", script);
    }

    [Fact]
    public void MainApplication_DoesNotPublishLegacyPluginSeed()
    {
        var project = ReadRepositoryFile("src", "HephaestusWorkbench.App", "HephaestusWorkbench.App.csproj");
        var pluginSeedDirectory = Path.Combine(
            FindRepositoryRoot(), "src", "HephaestusWorkbench.App", "PluginSeed");

        // 正式 v2 后续只能通过统一扩展安装事务携带离线扩展，本阶段禁止旧 PluginSeed 混入发布目录。
        Assert.DoesNotContain("PluginSeed", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PluginBinaryPath", project, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(pluginSeedDirectory));
    }

    [Fact]
    public void FormalInstaller_UsesOnlyLockedBundledExtensionManifest()
    {
        var script = ReadRepositoryFile("installer", "build-installer.ps1");
        var workflow = ReadRepositoryFile(".github", "workflows", "release.yml");

        Assert.Contains("distribution\\bundled-extensions.json", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BundledExtensions", script, StringComparison.Ordinal);
        Assert.Contains("RequireBundledExtensions=true", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", script, StringComparison.Ordinal);
        Assert.Contains("release.size", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PluginBinaryPath", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BundledExtensionsManifestPath", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PluginSeed", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("releases?per_page", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sort-Object Version", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("manifest.type", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Expand-Archive", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Expand-Archive", workflow, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("PLUGIN_REPO", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("releases?per_page", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sort-Object Version", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PluginBinaryPath", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppProject_EmbedsExternalTrustAnchorAndFailsFormalBuildWhenItsInputIsMissing()
    {
        var project = ReadRepositoryFile("src", "HephaestusWorkbench.App", "HephaestusWorkbench.App.csproj");

        // 信任锚只能由发布管线显式注入；正式 Bundle 构建不能退回到源码或占位锚。
        Assert.Contains("ExtensionTrustAnchorPath", project, StringComparison.Ordinal);
        Assert.Contains("<EmbeddedResource Include=\"$(ExtensionTrustAnchorPath)\">", project, StringComparison.Ordinal);
        Assert.Contains("<LogicalName>HephaestusWorkbench.ExtensionTrustAnchor.json</LogicalName>", project, StringComparison.Ordinal);
        Assert.Contains("'$(RequireBundledExtensions)' == 'true'", project, StringComparison.Ordinal);
        Assert.Contains("<Error", project, StringComparison.Ordinal);
        Assert.Contains("信任锚", project, StringComparison.Ordinal);
    }

    [Fact]
    public void FormalInstaller_RequiresExplicitTrustAnchorAndPassesItToPublish()
    {
        var script = ReadRepositoryFile("installer", "build-installer.ps1");

        Assert.Contains("[string]$ExtensionTrustAnchorPath", script, StringComparison.Ordinal);
        Assert.Contains("trustedPublishers", script, StringComparison.Ordinal);
        Assert.Contains("publicKey", script, StringComparison.Ordinal);
        Assert.Contains("allowedKinds", script, StringComparison.Ordinal);
        Assert.Contains("-p:ExtensionTrustAnchorPath=$ExtensionTrustAnchorPath", script, StringComparison.Ordinal);
        Assert.Contains("信任锚", script, StringComparison.Ordinal);
        Assert.DoesNotContain("默认信任锚", script, StringComparison.Ordinal);
        Assert.DoesNotContain("占位信任锚", script, StringComparison.Ordinal);
    }
    [Fact]
    public void FormalInstaller_VerifiesDownloadedZipWithPublicKeyResolvedFromTrustAnchor()
    {
        var buildScript = ReadRepositoryFile("installer", "build-installer.ps1");
        var verifier = ReadRepositoryFile("installer", "verify-ed25519.ps1");
        var downloadSection = buildScript[buildScript.IndexOf("正在下载并校验锁定的 Bundled Extension 资产", StringComparison.Ordinal)..];

        Assert.Contains("verify-ed25519.ps1", buildScript, StringComparison.Ordinal);
        Assert.Contains("$signatureVerifier", downloadSection, StringComparison.Ordinal);
        Assert.Contains("trustedPublishersByKeyId", downloadSection, StringComparison.Ordinal);
        Assert.Contains("publicKey", downloadSection, StringComparison.Ordinal);
        Assert.Contains("release.signature.signature", downloadSection, StringComparison.Ordinal);
        Assert.Contains("crypto_sign_verify_detached", verifier, StringComparison.Ordinal);
        Assert.Contains("project.assets.json", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("privateKey", verifier, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ed25519Verifier_AcceptsSignatureOverExactPackageBytes()
    {
        var result = await RunEd25519VerifierAsync(tamperPackage: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Ed25519 验签通过", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ed25519Verifier_RejectsTamperedPackageInChinese()
    {
        var result = await RunEd25519VerifierAsync(tamperPackage: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Ed25519 验签失败", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void FormalInstaller_FailsClosedUntilRealLockedManifestIsPublished()
    {
        var script = ReadRepositoryFile("installer", "build-installer.ps1");

        Assert.Contains("未找到 Bundled Extension 锁定清单", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion", script, StringComparison.Ordinal);
        Assert.Contains("SHA-256", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ed25519", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormalInstaller_ValidatesLockWithClientEquivalentCoreConstraints()
    {
        var script = ReadRepositoryFile("installer", "build-installer.ps1");
        var inno = ReadRepositoryFile("installer", "HephaestusWorkbench.iss");
        var rootReadme = ReadRepositoryFile("README.md");
        var publicReadme = ReadRepositoryFile("distribution", "public", "README.md");

        Assert.Contains("-isnot [long]", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WindowsReservedNames", script, StringComparison.Ordinal);
        Assert.Contains("minHostVersion", script, StringComparison.Ordinal);
        Assert.Contains("knownKinds", script, StringComparison.Ordinal);
        Assert.Contains("GetInvalidFileNameChars", script, StringComparison.Ordinal);
        Assert.Contains("recursesubdirs createallsubdirs", inno, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("PluginSeed", rootReadme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PluginBinaryPath", rootReadme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("最新正式版本", rootReadme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("尚未发布", publicReadme, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormalInstaller_ValidateOnlyAcceptsValidManifestWithoutBuilding()
    {
        var result = await RunInstallerValidationAsync(_ => { });

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("锁定清单契约校验通过", result.Output, StringComparison.Ordinal);
        Assert.False(result.StagingDirectoryCreated, "ValidateOnly 不得创建安装器暂存目录。");
        Assert.False(result.DistDirectoryCreated, "ValidateOnly 不得创建安装器输出目录。");
    }

    [Theory]
    [InlineData(67_108_865L, true)]
    [InlineData(209_715_201L, false)]
    public async Task FormalInstaller_ValidateOnlyEnforcesFrozenBundledPackageSizePrecheck(long size, bool accepted)
    {
        var result = await RunInstallerValidationAsync(manifest =>
        {
            var release = Assert.IsType<JsonObject>(GetFirstExtension(manifest)["release"]);
            release["size"] = size;
        });

        Assert.Equal(accepted, result.ExitCode == 0);
        Assert.False(result.StagingDirectoryCreated, "ValidateOnly 不得进入资产存在性或安装器构建步骤。");
        Assert.False(result.DistDirectoryCreated, "ValidateOnly 不得创建安装器输出目录。");
        if (!accepted)
            Assert.Contains("release.size", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormalInstaller_ValidateOnlyRejectsEmptyTrustAnchorPublishersInChinese()
    {
        var result = await RunInstallerValidationAsync(
            _ => { },
            anchor => anchor["trustedPublishers"] = new JsonArray());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("信任锚", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormalInstaller_ValidateOnlyRejectsBundleSignatureKeyOutsideTrustAnchorInChinese()
    {
        var result = await RunInstallerValidationAsync(manifest =>
        {
            var release = Assert.IsType<JsonObject>(GetFirstExtension(manifest)["release"]);
            var signature = Assert.IsType<JsonObject>(release["signature"]);
            signature["keyId"] = "untrusted-key";
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("信任锚", result.Output, StringComparison.Ordinal);
        Assert.Contains("keyId", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormalInstaller_ValidateOnlyRejectsBundlePublisherMismatchWithTrustAnchorInChinese()
    {
        var result = await RunInstallerValidationAsync(manifest => GetFirstExtension(manifest)["publisherId"] = "other-publisher");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("信任锚", result.Output, StringComparison.Ordinal);
        Assert.Contains("publisherId", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormalInstaller_ValidateOnlyRejectsBundleKindOutsideTrustAnchorScopeInChinese()
    {
        var result = await RunInstallerValidationAsync(manifest => GetFirstExtension(manifest)["kind"] = "maintenance");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("信任锚", result.Output, StringComparison.Ordinal);
        Assert.Contains("allowedKinds", result.Output, StringComparison.Ordinal);
    }
    [Fact]
    public async Task FormalInstaller_ValidateOnlyRejectsIncorrectFieldCasingInChinese()
    {
        var result = await RunInstallerValidationAsync(manifest =>
        {
            var release = Assert.IsType<JsonObject>(GetFirstExtension(manifest)["release"]);
            var signature = Assert.IsType<JsonObject>(release["signature"]);
            var keyId = signature["keyId"];
            signature.Remove("keyId");
            signature["KeyId"] = keyId;
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("字段不符合 schema v2", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormalInstaller_ValidateOnlyRejectsUppercaseIdInChinese()
    {
        var result = await RunInstallerValidationAsync(manifest => GetFirstExtension(manifest)["id"] = "Test-extension");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Bundled Extension ID 无效或重复", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormalInstaller_ValidateOnlyRejectsUppercasePublisherIdInChinese()
    {
        var result = await RunInstallerValidationAsync(manifest => GetFirstExtension(manifest)["publisherId"] = "Test-publisher");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("publisherId 无效", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void FormalRelease_WritesTrustAnchorSecretOnlyToRunnerTempAndPassesItsPathToInstaller()
    {
        var workflow = ReadRepositoryFile(".github", "workflows", "release.yml");
        var buildJob = ExtractYamlMapping(workflow, "build", 2);
        var installerStep = ExtractYamlStep(buildJob, "生成正式安装包");

        Assert.Contains("secrets.EXTENSION_TRUST_ANCHOR_BASE64", buildJob, StringComparison.Ordinal);
        Assert.Contains("RUNNER_TEMP", buildJob, StringComparison.Ordinal);
        Assert.Contains("FromBase64String", buildJob, StringComparison.Ordinal);
        Assert.Contains("-ExtensionTrustAnchorPath", installerStep, StringComparison.Ordinal);
        Assert.DoesNotContain("EXTENSION_TRUST_ANCHOR_BASE64", installerStep, StringComparison.Ordinal);
        Assert.DoesNotContain("extension-trust-anchor.json", ExtractYamlStep(buildJob, "上传候选安装包 Artifact"), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void FormalRelease_PublishJobUsesOnlyProtectedBuildArtifacts()
    {
        var script = ReadRepositoryFile("installer", "build-installer.ps1");
        var workflow = ReadRepositoryFile(".github", "workflows", "release.yml");
        var releaseReadme = ReadRepositoryFile("distribution", "releases", "README.md");
        var publishJob = ExtractYamlMapping(workflow, "publish", 2);
        var environment = ExtractYamlMapping(publishJob, "environment", 4);
        var publishUses = ReadYamlUses(publishJob);

        Assert.Contains("发布版本必须使用 X.Y.Z", script, StringComparison.Ordinal);
        Assert.Contains("最低宿主版本", script, StringComparison.Ordinal);
        Assert.Equal("build", ReadYamlScalar(publishJob, "needs", 4));
        Assert.Equal("github.event_name == 'push'", ReadYamlScalar(publishJob, "if", 4));
        Assert.Equal("workbench-production", ReadYamlScalar(environment, "name", 6));
        Assert.DoesNotContain(publishUses, value => value.StartsWith("actions/checkout@", StringComparison.Ordinal));
        Assert.Equal(2, publishUses.Count(value => value.StartsWith("actions/download-artifact@", StringComparison.Ordinal)));
        Assert.Contains("Windows 10/11", releaseReadme, StringComparison.Ordinal);
        Assert.Contains("required reviewers", releaseReadme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormalRelease_ManualDispatchCannotPublishWithoutImmutableSourceTagEvent()
    {
        var workflow = ReadRepositoryFile(".github", "workflows", "release.yml");
        var workflowDispatch = ExtractYamlMapping(workflow, "workflow_dispatch", 2);
        var inputs = ExtractYamlMapping(workflowDispatch, "inputs", 4);
        var publishJob = ExtractYamlMapping(workflow, "publish", 2);

        Assert.DoesNotContain("        publish:", inputs, StringComparison.Ordinal);
        Assert.Equal("github.event_name == 'push'", ReadYamlScalar(publishJob, "if", 4));
    }

    [Fact]
    public void FormalRelease_TriggerValuesAreScopedToVersionValidationStep()
    {
        var workflow = ReadRepositoryFile(".github", "workflows", "release.yml");
        var buildJob = ExtractYamlMapping(workflow, "build", 2);
        var versionStep = ExtractYamlStep(buildJob, "校验版本号");
        var environment = ExtractYamlMapping(versionStep, "env", 8);
        var run = ExtractYamlMapping(versionStep, "run", 8);

        Assert.Equal("${{ github.event_name }}", ReadYamlScalar(environment, "EVENT_NAME", 10));
        Assert.Equal("${{ github.ref_name }}", ReadYamlScalar(environment, "REF_NAME", 10));
        Assert.Equal("${{ inputs.version }}", ReadYamlScalar(environment, "INPUT_VERSION", 10));
        Assert.Contains("$env:EVENT_NAME", run, StringComparison.Ordinal);
        Assert.Contains("$env:REF_NAME", run, StringComparison.Ordinal);
        Assert.Contains("$env:INPUT_VERSION", run, StringComparison.Ordinal);
        Assert.DoesNotContain("${{", run, StringComparison.Ordinal);
    }

    [Fact]
    public void FormalRelease_AllActionsUseFullCommitSha()
    {
        var workflow = ReadRepositoryFile(".github", "workflows", "release.yml");
        var uses = ReadYamlUses(workflow);

        Assert.NotEmpty(uses);
        Assert.All(uses, value => Assert.Matches("^[^@\\s]+@[0-9a-f]{40}$", value));
    }

    [Fact]
    public void InnoSetup_UninstallDoesNotDeleteUserData()
    {
        var script = ReadRepositoryFile("installer", "HephaestusWorkbench.iss");

        Assert.DoesNotContain("[UninstallDelete]", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HephaestusWorkbenchData", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseMetadataImport_WritesBundledLockFromFinalZipManifestAndExplicitDescription()
    {
        const string reviewedDescription = "人工审核：支持本地日志筛选、归档和分析。";
        var result = await RunReleaseMetadataImportAsync(reviewedDescription: reviewedDescription);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.True(result.OutputExists, result.Output);
        var outputJson = Assert.IsType<string>(result.OutputJson);
        var document = BundledExtensionManifestParser.Parse(outputJson);
        var extension = Assert.Single(document.Extensions);
        Assert.Equal(2, document.SchemaVersion);
        Assert.Equal("log-analyzer", extension.Id);
        Assert.Equal("日志分析", extension.Name);
        Assert.Equal(reviewedDescription, extension.Description);
        Assert.Equal("thelinyue", extension.PublisherId);
        Assert.Equal("analysis", extension.Kind.ToString().ToLowerInvariant());
        Assert.Equal("log-analyzer-v2.0.0.zip", extension.Asset);
        Assert.Equal("2.0.0", extension.Release.Version);
        Assert.Equal("2.0.0", extension.Release.MinHostVersion);
        Assert.Equal("https://example.invalid/releases/log-analyzer-v2.0.0.zip", extension.Release.Url);
        Assert.NotEqual(0, extension.Release.Size);
        Assert.Equal(64, extension.Release.Sha256.Length);
        Assert.Equal("test-key", extension.Release.Signature.KeyId);
        Assert.Equal(Convert.ToBase64String(new byte[64]), extension.Release.Signature.Signature);
        Assert.DoesNotContain("publicKey", outputJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseMetadataImport_UsesFrozenPackageSizeLimit()
    {
        var script = ReadRepositoryFile("installer", "import-release-metadata.ps1");

        Assert.Contains("$maximumPackageBytes = 209715200", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseMetadataImport_RejectsNonDefaultHttpsPortWithoutWritingOutput()
    {
        var result = await RunReleaseMetadataImportAsync(metadata =>
        {
            var package = GetReleaseMetadataPackage(metadata);
            package["url"] = "https://example.invalid:444/releases/log-analyzer-v2.0.0.zip";
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("url", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.OutputExists);
    }

    [Fact]
    public async Task ReleaseMetadataImport_RejectsWindowsSuperscriptReservedAssetWithoutWritingOutput()
    {
        var result = await RunReleaseMetadataImportAsync(packageFileName: "COM¹.zip");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("file", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.OutputExists);
    }

    [Fact]
    public async Task ReleaseMetadataImport_RejectsMetadataSchemaOtherThanV2OrMissingCompleteManifest()
    {
        var invalidSchema = await RunReleaseMetadataImportAsync(metadata => metadata["schemaVersion"] = 1);
        var missingManifest = await RunReleaseMetadataImportAsync(metadata => GetReleaseMetadataPackage(metadata).Remove("manifest"));

        Assert.NotEqual(0, invalidSchema.ExitCode);
        Assert.Contains("schemaVersion", invalidSchema.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(invalidSchema.OutputExists);
        Assert.NotEqual(0, missingManifest.ExitCode);
        Assert.Contains("manifest", missingManifest.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(missingManifest.OutputExists);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ReleaseMetadataImport_RejectsMissingOrMultipleRootManifestJson(int rootManifestCopies)
    {
        var result = await RunReleaseMetadataImportAsync(rootManifestCopies: rootManifestCopies);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("根级 manifest.json", result.Output, StringComparison.Ordinal);
        Assert.False(result.OutputExists);
    }

    [Fact]
    public async Task ReleaseMetadataImport_RejectsZipManifestThatDiffersFromSelectedMetadataPackage()
    {
        var result = await RunReleaseMetadataImportAsync(
            mutateZipManifest: manifest => manifest["version"] = "2.0.1");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ZIP manifest", result.Output, StringComparison.Ordinal);
        Assert.False(result.OutputExists);
    }

    [Fact]
    public async Task ReleaseMetadataImport_RejectsMetadataFileSizeOrSha256ThatDoesNotMatchFinalZip()
    {
        var wrongFile = await RunReleaseMetadataImportAsync(metadata =>
        {
            var package = GetReleaseMetadataPackage(metadata);
            package["file"] = "other.zip";
            package["url"] = "https://example.invalid/releases/other.zip";
        });
        var wrongSize = await RunReleaseMetadataImportAsync(metadata => GetReleaseMetadataPackage(metadata)["size"] = 1);
        var wrongHash = await RunReleaseMetadataImportAsync(metadata => GetReleaseMetadataPackage(metadata)["sha256"] = new string('0', 64));

        Assert.NotEqual(0, wrongFile.ExitCode);
        Assert.Contains("file", wrongFile.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(wrongFile.OutputExists);
        Assert.NotEqual(0, wrongSize.ExitCode);
        Assert.Contains("size", wrongSize.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(wrongSize.OutputExists);
        Assert.NotEqual(0, wrongHash.ExitCode);
        Assert.Contains("SHA-256", wrongHash.Output, StringComparison.Ordinal);
        Assert.False(wrongHash.OutputExists);
    }

    [Fact]
    public async Task ReleaseMetadataImport_RejectsUnknownOrDuplicateSelectedExtensionId()
    {
        var unknown = await RunReleaseMetadataImportAsync(extensionId: "missing-extension");
        var duplicate = await RunReleaseMetadataImportAsync(metadata =>
        {
            var packages = Assert.IsType<JsonArray>(metadata["packages"]);
            var second = Assert.IsType<JsonObject>(GetReleaseMetadataPackage(metadata).DeepClone());
            var manifest = Assert.IsType<JsonObject>(second["manifest"]);
            manifest["version"] = "2.0.1";
            packages.Add(second);
        });

        Assert.NotEqual(0, unknown.ExitCode);
        Assert.Contains("未找到", unknown.Output, StringComparison.Ordinal);
        Assert.False(unknown.OutputExists);
        Assert.NotEqual(0, duplicate.ExitCode);
        Assert.Contains("重复", duplicate.Output, StringComparison.Ordinal);
        Assert.False(duplicate.OutputExists);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("capabilities")]
    [InlineData("dependencies")]
    public async Task ReleaseMetadataImport_RejectsDeepZipManifestDifferences(string field)
    {
        var result = await RunReleaseMetadataImportAsync(mutateZipManifest: manifest =>
        {
            switch (field)
            {
                case "runtime":
                    Assert.IsType<JsonObject>(manifest["runtime"])["entry"] = "updated-tool.exe";
                    break;
                case "capabilities":
                    manifest["capabilities"] = new JsonArray("analysis.engine", "analysis.scope.storage");
                    break;
                case "dependencies":
                    manifest["dependencies"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "shared-rules",
                            ["version"] = "1.0.0"
                        }
                    };
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(field), field, "未知的 manifest 深层字段。");
            }
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ZIP manifest", result.Output, StringComparison.Ordinal);
        Assert.False(result.OutputExists);
    }

    [Fact]
    public async Task ReleaseMetadataImport_RejectsRuntimeEntryContainingNulWithoutWritingOutput()
    {
        const string entry = "tool\0.exe";
        var result = await RunReleaseMetadataImportAsync(
            mutateMetadata: metadata =>
            {
                var runtime = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(GetReleaseMetadataPackage(metadata)["manifest"])["runtime"]);
                runtime["entry"] = entry;
            },
            mutateZipManifest: manifest =>
            {
                var runtime = Assert.IsType<JsonObject>(manifest["runtime"]);
                runtime["entry"] = entry;
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.OutputExists);
    }

    [Fact]
    public async Task ReleaseMetadataImport_RejectsBlankReviewedDescriptionInsteadOfDerivingItFromMetadata()
    {
        var result = await RunReleaseMetadataImportAsync(reviewedDescription: " \t ");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ReviewedDescription", result.Output, StringComparison.Ordinal);
        Assert.False(result.OutputExists);
    }

    /// <summary>
    /// 按 YAML 缩进提取映射块，只在目标 job/step 的结构范围内做发布安全断言。
    /// </summary>
    private static string ExtractYamlMapping(string yaml, string key, int indent)
    {
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var prefix = new string(' ', indent) + key + ":";
        var start = Array.FindIndex(lines, line => line.StartsWith(prefix, StringComparison.Ordinal));
        Assert.True(start >= 0, $"YAML 中缺少缩进为 {indent} 的映射：{key}。");

        var end = lines.Length;
        for (var index = start + 1; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            if (CountLeadingSpaces(lines[index]) <= indent)
            {
                end = index;
                break;
            }
        }

        return string.Join("\n", lines[start..end]);
    }

    private static string ExtractYamlStep(string job, string name)
    {
        var lines = job.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var header = "      - name: " + name;
        var start = Array.FindIndex(lines, line => string.Equals(line, header, StringComparison.Ordinal));
        Assert.True(start >= 0, $"YAML job 中缺少 step：{name}。");

        var end = lines.Length;
        for (var index = start + 1; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            if (CountLeadingSpaces(lines[index]) <= 6)
            {
                end = index;
                break;
            }
        }

        return string.Join("\n", lines[start..end]);
    }

    private static string ReadYamlScalar(string block, string key, int indent)
    {
        var prefix = new string(' ', indent) + key + ":";
        var line = block.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .SingleOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        Assert.NotNull(line);
        return line![prefix.Length..].Trim();
    }

    private static string[] ReadYamlUses(string block)
        => Regex.Matches(block, @"(?m)^\s*uses:\s*(?<value>\S+)\s*$")
            .Select(match => match.Groups["value"].Value)
            .ToArray();

    private static int CountLeadingSpaces(string value)
    {
        var count = 0;
        while (count < value.Length && value[count] == ' ') count++;
        return count;
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(segments).ToArray()));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    /// <summary>
    /// 以真实 pwsh 进程执行离线 handoff；ZIP 与 metadata 都只在系统临时目录创建，避免任何测试资产进入仓库。
    /// </summary>
    private static async Task<ReleaseMetadataImportResult> RunReleaseMetadataImportAsync(
        Action<JsonObject>? mutateMetadata = null,
        Action<JsonObject>? mutateZipManifest = null,
        int rootManifestCopies = 1,
        string extensionId = "log-analyzer",
        string reviewedDescription = "人工审核：日志分析扩展。",
        string packageFileName = "log-analyzer-v2.0.0.zip")
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"hephaestus-release-metadata-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandbox);
        try
        {
            var metadataPath = Path.Combine(sandbox, "release-metadata.json");
            var packagePath = Path.Combine(sandbox, packageFileName);
            var outputPath = Path.Combine(sandbox, "bundled-extensions.json");
            var metadata = CreateReleaseMetadata();
            var package = GetReleaseMetadataPackage(metadata);
            package["file"] = packageFileName;
            package["url"] = $"https://example.invalid/releases/{Uri.EscapeDataString(packageFileName)}";
            var zipManifest = Assert.IsType<JsonObject>(package["manifest"]).DeepClone().AsObject();
            mutateZipManifest?.Invoke(zipManifest);
            await WriteTemporaryExtensionZipAsync(packagePath, zipManifest, rootManifestCopies);
            Assert.True(File.Exists(packagePath), $"测试 helper 未能创建指定 ZIP 文件：{packagePath}");

            package["size"] = new FileInfo(packagePath).Length;
            package["sha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(packagePath))).ToLowerInvariant();
            mutateMetadata?.Invoke(metadata);
            await File.WriteAllTextAsync(
                metadataPath,
                metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            var utf8RunnerPath = await WriteUtf8PowerShellRunnerAsync(sandbox);
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // PowerShell 7 向重定向管道写入 UTF-8；显式指定解码方式，避免英文 Windows runner 按 OEM 代码页读取中文错误。
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(utf8RunnerPath);
            startInfo.ArgumentList.Add(Path.Combine(FindRepositoryRoot(), "installer", "import-release-metadata.ps1"));
            startInfo.ArgumentList.Add("-ReleaseMetadataPath");
            startInfo.ArgumentList.Add(metadataPath);
            startInfo.ArgumentList.Add("-PackagePath");
            startInfo.ArgumentList.Add(packagePath);
            startInfo.ArgumentList.Add("-ExtensionId");
            startInfo.ArgumentList.Add(extensionId);
            startInfo.ArgumentList.Add("-ReviewedDescription");
            startInfo.ArgumentList.Add(reviewedDescription);
            startInfo.ArgumentList.Add("-OutputPath");
            startInfo.ArgumentList.Add(outputPath);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var standardOutput = process!.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await standardOutput + await standardError;
            return new ReleaseMetadataImportResult(
                process.ExitCode,
                output,
                File.Exists(outputPath),
                File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath) : null);
        }
        finally
        {
            DeleteValidationSandbox(sandbox);
        }
    }

    /// <summary>
    /// 创建 UTF-8 PowerShell 包装器，使英文 Windows runner 与本地系统代码页无关地保留中文发布错误语义。
    /// </summary>
    private static async Task<string> WriteUtf8PowerShellRunnerAsync(string sandbox)
    {
        var runnerPath = Path.Combine(sandbox, "invoke-utf8-powershell.ps1");
        await File.WriteAllTextAsync(
            runnerPath,
            """
            [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $OutputEncoding = [Console]::OutputEncoding
            $targetScript = $args[0]
            $targetArguments = @($args | Select-Object -Skip 1)
            & $targetScript @targetArguments
            exit $LASTEXITCODE
            """,
            new UTF8Encoding(false));
        return runnerPath;
    }

    /// <summary>创建只用于进程级测试的最终 ZIP；其中根级 manifest.json 是脚本必须读取的唯一身份源。</summary>
    private static async Task WriteTemporaryExtensionZipAsync(string packagePath, JsonObject manifest, int rootManifestCopies)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        for (var index = 0; index < rootManifestCopies; index++)
        {
            var entry = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression);
            await using var stream = entry.Open();
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(manifest.ToJsonString());
        }

        var payload = archive.CreateEntry("tool.exe", CompressionLevel.NoCompression);
        await using var payloadStream = payload.Open();
        await payloadStream.WriteAsync(RandomNumberGenerator.GetBytes(32).AsMemory());
    }

    /// <summary>构造符合既有 metadata v2 基础契约的临时交接记录；测试值不含私钥或公钥，也不会写入仓库。</summary>
    private static JsonObject CreateReleaseMetadata()
        => new()
        {
            ["schemaVersion"] = 2,
            ["generatedAtUtc"] = "2026-08-24T00:00:00Z",
            ["packages"] = new JsonArray
            {
                new JsonObject
                {
                    ["manifest"] = new JsonObject
                    {
                        ["schemaVersion"] = 2,
                        ["id"] = "log-analyzer",
                        ["name"] = "日志分析",
                        ["version"] = "2.0.0",
                        ["kind"] = "analysis",
                        ["publisherId"] = "thelinyue",
                        ["hostApiVersion"] = "1.0",
                        ["minHostVersion"] = "2.0.0",
                        ["runtime"] = new JsonObject
                        {
                            ["kind"] = "process",
                            ["protocol"] = "analysis-process-v1",
                            ["entry"] = "tool.exe"
                        },
                        ["capabilities"] = new JsonArray("analysis.engine"),
                        ["permissions"] = new JsonArray(),
                        ["dependencies"] = new JsonArray()
                    },
                    ["file"] = "log-analyzer-v2.0.0.zip",
                    ["url"] = "https://example.invalid/releases/log-analyzer-v2.0.0.zip",
                    ["size"] = 1,
                    ["sha256"] = new string('0', 64),
                    ["keyId"] = "test-key",
                    ["signature"] = Convert.ToBase64String(new byte[64])
                }
            }
        };

    private static JsonObject GetReleaseMetadataPackage(JsonObject metadata)
        => Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(metadata["packages"])[0]);

    /// <summary>
    /// 在临时仓库布局中启动真实 pwsh 进程，确保脚本仍只从自身仓库的 distribution 目录读取清单。
    /// </summary>
    private static async Task<InstallerValidationResult> RunInstallerValidationAsync(
        Action<JsonObject> mutateManifest,
        Action<JsonObject>? mutateTrustAnchor = null)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"hephaestus-installer-validation-{Guid.NewGuid():N}");
        var installerDirectory = Path.Combine(sandbox, "installer");
        var distributionDirectory = Path.Combine(sandbox, "distribution");
        var sandboxScript = Path.Combine(installerDirectory, "build-installer.ps1");
        var validationScript = Path.Combine(installerDirectory, "validate-installer.ps1");
        var trustAnchorPath = Path.Combine(sandbox, "extension-trust-anchor.json");
        Directory.CreateDirectory(installerDirectory);
        Directory.CreateDirectory(distributionDirectory);
        File.Copy(Path.Combine(FindRepositoryRoot(), "installer", "build-installer.ps1"), sandboxScript);
        await File.WriteAllTextAsync(
            validationScript,
            """
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $trustAnchorPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'extension-trust-anchor.json'
            & (Join-Path $PSScriptRoot 'build-installer.ps1') -ValidateOnly -ExtensionTrustAnchorPath $trustAnchorPath
            exit $LASTEXITCODE
            """,
            new UTF8Encoding(false));

        var manifest = CreateValidBundledExtensionManifest();
        var trustAnchor = CreateTrustAnchorFor(manifest);
        mutateManifest(manifest);
        mutateTrustAnchor?.Invoke(trustAnchor);
        await File.WriteAllTextAsync(
            Path.Combine(distributionDirectory, "bundled-extensions.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            trustAnchorPath,
            trustAnchor.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(validationScript);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        await using var standardOutput = new MemoryStream();
        await using var standardError = new MemoryStream();
        var outputCopy = process!.StandardOutput.BaseStream.CopyToAsync(standardOutput);
        var errorCopy = process.StandardError.BaseStream.CopyToAsync(standardError);
        await process.WaitForExitAsync();
        await Task.WhenAll(outputCopy, errorCopy);

        var result = new InstallerValidationResult(
            process.ExitCode,
            Encoding.UTF8.GetString(standardOutput.ToArray()) + Encoding.UTF8.GetString(standardError.ToArray()),
            Directory.Exists(Path.Combine(installerDirectory, ".staging")),
            Directory.Exists(Path.Combine(installerDirectory, "dist")));
        DeleteValidationSandbox(sandbox);
        return result;
    }

    /// <summary>使用真实 NSec 测试密钥签名临时包，并在独立 PowerShell 进程中验证发布脚本的原始字节验签行为。</summary>
    private static async Task<SignatureVerificationResult> RunEd25519VerifierAsync(bool tamperPackage)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"hephaestus-ed25519-verifier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandbox);
        try
        {
            var packagePath = Path.Combine(sandbox, "package.zip");
            var packageBytes = RandomNumberGenerator.GetBytes(256);
            byte[] publicKey;
            byte[] signature;
            using (var key = Key.Create(SignatureAlgorithm.Ed25519))
            {
                publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
                signature = SignatureAlgorithm.Ed25519.Sign(key, packageBytes);
            }

            if (tamperPackage) packageBytes[0] ^= 0x5a;
            await File.WriteAllBytesAsync(packagePath, packageBytes);

            var utf8RunnerPath = await WriteUtf8PowerShellRunnerAsync(sandbox);
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                WorkingDirectory = FindRepositoryRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 验签失败信息包含中文安全语义，必须按照 pwsh 的 UTF-8 管道字节解码。
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(utf8RunnerPath);
            startInfo.ArgumentList.Add(Path.Combine(FindRepositoryRoot(), "installer", "verify-ed25519.ps1"));
            startInfo.ArgumentList.Add("-PackagePath");
            startInfo.ArgumentList.Add(packagePath);
            startInfo.ArgumentList.Add("-PublicKeyBase64");
            startInfo.ArgumentList.Add(Convert.ToBase64String(publicKey));
            startInfo.ArgumentList.Add("-SignatureBase64");
            startInfo.ArgumentList.Add(Convert.ToBase64String(signature));
            startInfo.ArgumentList.Add("-ProjectAssetsPath");
            startInfo.ArgumentList.Add(Path.Combine(
                FindRepositoryRoot(),
                "src",
                "HephaestusWorkbench.Services",
                "obj",
                "project.assets.json"));

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var standardOutput = process!.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new SignatureVerificationResult(
                process.ExitCode,
                await standardOutput + await standardError);
        }
        finally
        {
            DeleteValidationSandbox(sandbox);
        }
    }

    /// <summary>仅清理当前测试在系统临时目录下创建的隔离仓库。</summary>
    private static void DeleteValidationSandbox(string sandbox)
    {
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandbox));
        if (!target.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"拒绝清理临时仓库范围之外的目录：{target}");

        new DirectoryInfo(target).Delete(recursive: true);
    }

    private static JsonObject GetFirstExtension(JsonObject manifest)
        => Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(manifest["extensions"])[0]);

    private static JsonObject CreateValidBundledExtensionManifest()
        => new()
        {
            ["schemaVersion"] = 2,
            ["extensions"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "test-extension",
                    ["name"] = "测试扩展",
                    ["description"] = "仅用于安装脚本契约测试",
                    ["publisherId"] = "test-publisher",
                    ["kind"] = "analysis",
                    ["asset"] = "test-extension.zip",
                    ["release"] = new JsonObject
                    {
                        ["version"] = "2.0.0",
                        ["minHostVersion"] = "2.0.0",
                        ["url"] = "https://example.invalid/test-extension.zip",
                        ["size"] = 1,
                        ["sha256"] = new string('0', 64),
                        ["signature"] = new JsonObject
                        {
                            ["keyId"] = Guid.NewGuid().ToString("N"),
                            ["signature"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
                        }
                    }
                }
            }
        };

    /// <summary>为临时 Bundle 生成匹配的测试锚；其中公钥仅是确定性的 32 字节测试数据。</summary>
    private static JsonObject CreateTrustAnchorFor(JsonObject manifest)
    {
        var extension = GetFirstExtension(manifest);
        var release = Assert.IsType<JsonObject>(extension["release"]);
        var signature = Assert.IsType<JsonObject>(release["signature"]);
        return new JsonObject
        {
            ["schemaVersion"] = 2,
            ["trustedPublishers"] = new JsonArray
            {
                new JsonObject
                {
                    ["keyId"] = Assert.IsAssignableFrom<JsonValue>(signature["keyId"]).GetValue<string>(),
                    ["publisherId"] = Assert.IsAssignableFrom<JsonValue>(extension["publisherId"]).GetValue<string>(),
                    ["publicKey"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()),
                    ["scope"] = new JsonObject
                    {
                        ["allowedKinds"] = new JsonArray(Assert.IsAssignableFrom<JsonValue>(extension["kind"]).GetValue<string>()),
                        ["permissions"] = new JsonArray("workspace.readText")
                    }
                }
            }
        };
    }
    private sealed record ReleaseMetadataImportResult(
        int ExitCode,
        string Output,
        bool OutputExists,
        string? OutputJson);

    private sealed record InstallerValidationResult(
        int ExitCode,
        string Output,
        bool StagingDirectoryCreated,
        bool DistDirectoryCreated);

    private sealed record SignatureVerificationResult(int ExitCode, string Output);
}
