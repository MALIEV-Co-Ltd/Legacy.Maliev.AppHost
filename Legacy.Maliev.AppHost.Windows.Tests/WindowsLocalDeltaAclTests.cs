using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Xunit;

namespace Legacy.Maliev.AppHost.Windows.Tests;

public sealed class WindowsLocalDeltaAclTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "owned-windows-acl-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProtectedFileHasCurrentOwnerAndNoForeignAllowedIdentity()
    {
        string config = CreateConfig();
        LocalDeltaExecution.ValidateOwnerProtected(config);
        FileSecurity security = new FileInfo(config).GetAccessControl();
        AssertOwnerOnly(security);
        Assert.True(security.AreAccessRulesProtected);
    }

    [Fact]
    public void ForeignReadGrantIsRefusedWithoutChangingOwnedFixture()
    {
        string config = CreateConfig();
        byte[] before = File.ReadAllBytes(config);
        FileSecurity security = new FileInfo(config).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(config).SetAccessControl(security);
        var refusal = Assert.Throws<InvalidOperationException>(() => LocalDeltaExecution.ValidateOwnerProtected(config));
        Assert.Contains("not owner protected", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(config));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalApplyCreatesOwnerOnlyFileAndParentThenCleansOnlyItsFile(bool fail)
    {
        string config = CreateConfig();
        string target = Path.Combine(root, "run", "connection.txt");
        string sentinel = Path.Combine(root, "keep.bin");
        File.WriteAllBytes(sentinel, [1, 3, 255]);
        bool called = false;
        Task<int> run = LocalDeltaExecution.RunAsync(name => name switch
        {
            "LEGACY_LOCAL_DELTA_CONFIG" => config,
            "ConnectionStrings__legacy-postgres-main" => "synthetic-owned-no-database",
            _ => null
        }, root, _ => Task.CompletedTask, (arguments, _, _, environment, _) =>
        {
            called = true;
            Assert.Equal(["apply-delta-local", "--config", config], arguments);
            Assert.Equal("apphost", environment("LEGACY_MIGRATION_CALLER"));
            Assert.Equal("false", environment("LEGACY_DEPLOY_ENABLED"));
            Assert.Equal("synthetic-owned-no-database", File.ReadAllText(target));
            AssertOwnerOnly(new FileInfo(target).GetAccessControl());
            AssertOwnerOnly(new DirectoryInfo(Path.GetDirectoryName(target)!).GetAccessControl());
            LocalDeltaExecution.ValidateOwnerProtected(target);
            return fail ? Task.FromException<int>(new InvalidOperationException("owned synthetic failure")) : Task.FromResult(17);
        });
        if (fail)
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
            Assert.Equal("owned synthetic failure", failure.Message);
        }
        else Assert.Equal(17, await run);
        Assert.True(called);
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(config));
        Assert.Equal(new byte[] { 1, 3, 255 }, File.ReadAllBytes(sentinel));
    }

    private string CreateConfig()
    {
        Assert.True(OperatingSystem.IsWindows(), "This genuine ACL fixture must execute on the reviewed Windows runner.");
        Directory.CreateDirectory(root);
        string config = Path.Combine(root, "config.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new
        {
            delta = new { targetConnectionFile = Path.Combine(root, "run", "connection.txt") }
        }));
        LocalDeltaExecution.ProtectFile(config);
        return config;
    }

    private static void AssertOwnerOnly(FileSystemSecurity security)
    {
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User!;
        Assert.Equal(owner, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        var allowed = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow).ToArray();
        Assert.NotEmpty(allowed);
        Assert.All(allowed, rule => Assert.Equal(owner, rule.IdentityReference));
    }

    public void Dispose()
    {
        string owned = Path.GetFullPath(root);
        string temp = Path.GetFullPath(Path.GetTempPath());
        if (!owned.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(owned).StartsWith("owned-windows-acl-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside the owned fixture root.");
        if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
    }
}
