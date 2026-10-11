using global::Installer;

namespace RevitModelMcp.Core.Tests.InstallerLogic;

public sealed class InstallerScriptsTests
{
    [Test]
    public async Task ReadOnlyClearRemovesBothInstallerOwnedValues()
    {
        var script = InstallerScripts.ClearMsiReadOnlyScript;
        await Assert.That(script).Contains(@"Software\Policies\RevitModelMcp");
        await Assert.That(script).Contains(@"Software\RevitModelMcp\MsiPolicy");
        await Assert.That(script).Contains("DeleteValue('ReadOnly'");
        await Assert.That(script).DoesNotContain("ExecuteCode");
    }

    [Test]
    public async Task ReadOnlyClearOpensTheNativeRegistryViewAndFailsLoudly()
    {
        var script = InstallerScripts.ClearMsiReadOnlyScript;
        await Assert.That(script).Contains("OpenBaseKey('LocalMachine', 'Registry64')");
        await Assert.That(script).Contains("$ErrorActionPreference = 'Stop'");
        await Assert.That(script).DoesNotContain("SilentlyContinue");
        await Assert.That(script).Contains("Model MCP: ");
        await Assert.That(script).Contains("exit 1");
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
    [Arguments(22, "could not be checked (error 22)", "run the installer again")]
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task CheckScriptPrintsTheReasonAndExitsWithItsCode(bool showMessage)
    {
        var script = InstallerScripts.CheckProgramDataFolderScript(showMessage);
        await Assert.That(script).Contains($"exit {InstallerScripts.FolderIsLink}");
        await Assert.That(script).Contains($"exit {InstallerScripts.FolderWrongOwner}");
        await Assert.That(script).Contains($"exit {InstallerScripts.FolderUnchecked}");
        await Assert.That(script).Contains("Write-Output ('Model MCP: ' + $reason)");
        await Assert.That(script).Contains("link or not a folder");
        await Assert.That(script).Contains("owned by an account other than Administrators or SYSTEM");
        await Assert.That(script).Contains("takeown /a /f \"%ProgramData%\\RevitModelMcp\"");
        await Assert.That(script).Contains("ReparsePoint");
        await Assert.That(script).Contains("S-1-5-32-544");
        await Assert.That(script).Contains("S-1-5-18");
        await Assert.That(script).DoesNotContain("Set-Acl");
        await Assert.That(script).DoesNotContain("Remove-Item");
        await Assert.That(script).DoesNotContain("vbscript");
    }

    [Test]
    public async Task OnlyTheInteractiveCheckShowsAMessageBox()
    {
        await Assert.That(InstallerScripts.CheckProgramDataFolderScript(true)).Contains("MessageBox]::Show($reason");
        await Assert.That(InstallerScripts.CheckProgramDataFolderScript(false)).DoesNotContain("MessageBox");
    }

    [Test]
    public async Task CheckScriptSurvivesEncodingForTheCommandLine()
    {
        var script = InstallerScripts.CheckProgramDataFolderScript(false);
        var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(InstallerScripts.Encode(script)));
        await Assert.That(decoded).IsEqualTo(script);
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
