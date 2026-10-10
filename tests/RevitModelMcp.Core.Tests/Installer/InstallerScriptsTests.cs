using global::Installer;

namespace RevitModelMcp.Core.Tests.InstallerLogic;

public sealed class InstallerScriptsTests
{
    [Test]
    public async Task ReadOnlyClearRemovesBothInstallerOwnedValues()
    {
        await Assert.That(InstallerScripts.ClearMsiReadOnlyScript).Contains(@"HKLM:\Software\Policies\RevitModelMcp");
        await Assert.That(InstallerScripts.ClearMsiReadOnlyScript).Contains(@"HKLM:\Software\RevitModelMcp\MsiPolicy");
        await Assert.That(InstallerScripts.ClearMsiReadOnlyScript).Contains("Remove-ItemProperty");
        await Assert.That(InstallerScripts.ClearMsiReadOnlyScript).Contains("-Name 'ReadOnly'");
        await Assert.That(InstallerScripts.ClearMsiReadOnlyScript).DoesNotContain("ExecuteCode");
    }

    [Test]
    public async Task ReadOnlyClearOnlyRunsOnMaintenanceForAValueTheInstallerOwns()
    {
        var condition = InstallerScripts.ClearMsiReadOnlyCondition;
        await Assert.That(condition).Contains("READONLY=\"0\"");
        await Assert.That(condition).Contains("RMM_REMEMBERED_READONLY<>\"\"");
        await Assert.That(condition).Contains("Installed");
        await Assert.That(condition).Contains("NOT REMOVE~=\"ALL\"");
    }

    [Test]
    [Arguments(InstallerScripts.FolderIsLink, "link or not a folder", "rd /s /q")]
    [Arguments(InstallerScripts.FolderWrongOwner, "owned by an account other than Administrators or SYSTEM", "takeown /a /f")]
    [Arguments(22, "could not be checked (PowerShell exit code 22)", "run the installer again")]
    public async Task FolderRefusalNamesTheReasonAndTheRemedy(int exitCode, string reason, string remedy)
    {
        var text = InstallerScripts.FolderRefusal(exitCode);
        await Assert.That(text).IsNotNull();
        await Assert.That(text!).Contains(@"%ProgramData%\RevitModelMcp");
        await Assert.That(text).Contains(reason);
        await Assert.That(text).Contains(remedy);
    }

    [Test]
    public async Task GoodFolderHasNoRefusal() =>
        await Assert.That(InstallerScripts.FolderRefusal(InstallerScripts.FolderOk)).IsNull();

    [Test]
    public async Task CheckScriptReportsEachRefusalAsItsExitCode()
    {
        var script = InstallerScripts.CheckProgramDataFolderScript;
        await Assert.That(script).Contains($"exit {InstallerScripts.FolderIsLink} ");
        await Assert.That(script).Contains($"exit {InstallerScripts.FolderWrongOwner} ");
        await Assert.That(script).Contains("ReparsePoint");
        await Assert.That(script).Contains("S-1-5-32-544");
        await Assert.That(script).Contains("S-1-5-18");
        await Assert.That(script).DoesNotContain("Set-Acl");
        await Assert.That(script).DoesNotContain("Remove-Item");
    }

    [Test]
    public async Task CheckActionLogsTheReasonShowsItAndFailsWithIt()
    {
        var script = InstallerScripts.CheckProgramDataFolderVbScript();
        await Assert.That(script).Contains("Session.Log");
        await Assert.That(script).Contains("Session.Message &H01000000");
        await Assert.That(script).Contains("Err.Raise");
        await Assert.That(script).Contains("link or not a folder");
        await Assert.That(script).Contains("-EncodedCommand " + InstallerScripts.Encode(InstallerScripts.CheckProgramDataFolderScript));
        await Assert.That(script).Contains("exitCode = shell.Run(");
        // The quotes in the remedy text are doubled for VBScript.
        await Assert.That(script).Contains("takeown /a /f \"\"%ProgramData%\\RevitModelMcp\"\"");
    }

    [Test]
    public async Task UninstallCleanupKeepsAnythingTheInstallerDidNotWrite()
    {
        var script = InstallerScripts.RemoveProgramDataFolderScript;
        await Assert.That(script).Contains("'updateCheck', 'updateMode'");
        await Assert.That(script).Contains("[System.IO.Directory]::Delete($directory)");
        await Assert.That(script).Contains("ReparsePoint");
        await Assert.That(script).DoesNotContain("-Recurse");
        await Assert.That(script).DoesNotContain("policy.json");
    }
}
