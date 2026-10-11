using System.Text;
using Installer;
using WixSharp;
using WixSharp.CommonTasks;
using WixSharp.Controls;

const string outputName = "RevitModelMcp";
const string productName = "Model MCP";
const string serverSourceFolder = @"output\server-exe\revit-model-mcp";

var versioning = Versioning.CreateFromVersionString(args[0]);
var wixEntities = Generator.GenerateWixEntities(args[1..]);

// INSTALLER_VARIANT=store builds only the SingleUser package of the Autodesk App Store build: the add-in carries
// UpdateChannel=store, so there is no updater and no update setting to write.
var store = string.Equals(Environment.GetEnvironmentVariable("INSTALLER_VARIANT"), "store", StringComparison.OrdinalIgnoreCase);

BuildSingleUserMsi();
if (!store) BuildMultiUserMsi();

Project CreateProject(InstallScope scope)
{
    var project = new Project
    {
        OutDir = "output",
        Name = productName,
        Scope = scope,
        Platform = Platform.x64,
        UI = WUI.WixUI_FeatureTree,
        MajorUpgrade = new MajorUpgrade
        {
            AllowSameVersionUpgrades = true,
            DowngradeErrorMessage = "A newer version of [ProductName] is already installed."
        },
        GUID = new Guid("75e1b812-23a4-45ce-9e7c-84d7d43b8c70"),
        ProductId = Guid.NewGuid(),
        BannerImage = @"build\install\Resources\Icons\BannerImage.png",
        BackgroundImage = @"build\install\Resources\Icons\BackgroundImage.png",
        Version = versioning.VersionPrefix,
        ControlPanelInfo =
        {
            Manufacturer = "Dinar Sharafutdinov",
            ProductIcon = @"build\install\Resources\Icons\ShellIcon.ico"
        }
    };
    project.WixSourceGenerated += Replacement.AddInstallTimeFileRemoval;
    project.RemoveDialogsBetween(NativeDialogs.WelcomeDlg, NativeDialogs.CustomizeDlg);
    return project;
}

void BuildSingleUserMsi()
{
    var project = CreateProject(InstallScope.perUser);
    project.Properties =
    [
        .. store ? Array.Empty<Property>() : new[]
        {
            new Property("UPDATECHECK", "1") { Secure = true },
            new Property("UPDATEMODE", "") { Secure = true }
        },
        .. OtherScopeProperties("Machine", year => $@"{MachineAddinsRoot(year).Search}\{year}")
    ];
    project.LaunchConditions =
    [
        OtherScopeLaunchCondition(
            "The Model MCP MultiUser (all users) installer is already installed on this computer. " +
            "Uninstall it from Apps > Installed apps, then run this SingleUser installer again.")
    ];
    project.RegValues = [];
    project.Actions = store ? [] : [.. UpdateSettingActions("LOCALAPPDATA", false)];
    project.OutFileName = $"{outputName}-{versioning.Version}-SingleUser{(store ? "-store" : "")}";
    project.Dirs =
    [
        new Dir(@"%AppDataFolder%\Autodesk\Revit\Addins\", [.. wixEntities.Select(entity => entity.Directory)]),
        ServerDir(@"%LocalAppDataFolder%\Programs")
    ];
    project.BuildMsi();
}

void BuildMultiUserMsi()
{
    var project = CreateProject(InstallScope.perMachine);
    project.LaunchConditions =
    [
        OtherScopeLaunchCondition(
            "The Model MCP SingleUser (current user) installer is already installed for this user. " +
            "Uninstall it from Apps > Installed apps, then run this MultiUser installer again.")
    ];
    project.Properties =
    [
        .. OtherScopeProperties("User", year => $@"[AppDataFolder]Autodesk\Revit\Addins\{year}"),
        new Property("HTTP_ENABLED", "0") { Secure = true },
        .. MachinePolicyValues.Properties(),
        new Property("UPDATECHECK", "1") { Secure = true },
        new Property("UPDATEMODE", "") { Secure = true },
        new Property("HTTP_URL_PREFIX", "http://127.0.0.1:53110/") { Secure = true },
        new RegValueProperty("HTTP_OWNED_PREFIX", RegistryHive.LocalMachine,
            @"Software\RevitModelMcp\HttpUrlAcl\[ProductCode]", "Prefix", "") { Secure = true }
    ];
    project.RegValues =
    [
        new RegValue(RegistryHive.LocalMachine, @"Software\RevitModelMcp\HttpUrlAcl\[ProductCode]",
            "Prefix", "[HTTP_OWNED_PREFIX]")
        {
            ComponentCondition = "HTTP_ENABLED=\"1\" AND NOT Installed"
        },
        .. MachinePolicyValues.RegValues()
    ];
    project.Include(WixExtension.Util);
    project.WixSourceGenerated += MachinePolicyValues.MarkReadOnlyAsDword;
    project.Actions =
    [
        new SetPropertyAction("HTTP_OWNED_PREFIX", "[HTTP_URL_PREFIX]", Return.check,
            When.Before, Step.CostFinalize,
            new Condition("HTTP_ENABLED=\"1\" AND NOT Installed AND NOT REMOVE~=\"ALL\"")),
        new PathFileAction(new Id("RegisterHttpUrlAcl"),
            @"[System64Folder]netsh.exe",
            "http add urlacl url=[HTTP_OWNED_PREFIX] sddl=\"D:(A;;GX;;;S-1-5-32-545)\"",
            "System64Folder", Return.check, When.Before, Step.WriteRegistryValues,
            new Condition("HTTP_ENABLED=\"1\" AND NOT Installed AND NOT REMOVE~=\"ALL\""))
        {
            Execute = Execute.deferred,
            Impersonate = false
        },
        new PathFileAction(new Id("RemoveHttpUrlAcl"),
            @"[System64Folder]netsh.exe",
            "http delete urlacl url=[HTTP_OWNED_PREFIX]",
            "System64Folder", Return.ignore, When.Before, Step.RemoveFiles,
            new Condition("REMOVE=\"ALL\" AND HTTP_OWNED_PREFIX"))
        {
            Execute = Execute.deferred,
            Impersonate = false
        },
        .. MachinePolicyValues.RememberActions(),
        CheckProgramDataFolderMessageAction(),
        CheckProgramDataFolderAction(),
        EnsureProgramDataFolderAction(),
        ClearMsiReadOnlyAction(),
        RemoveProgramDataFolderAction(),
        .. UpdateSettingActions("ProgramData", true)
    ];
    project.OutFileName = $"{outputName}-{versioning.Version}-MultiUser";

    project.Dirs =
    [
        .. wixEntities
            .GroupBy(entity => MachineAddinsRoot(entity.Version).Dir)
            .Select(root => new Dir(root.Key, [.. root.Select(entity => entity.Directory)])),
        ServerDir("%ProgramFiles%")
    ];

    project.BuildMsi();
}

// The self-contained server (PyInstaller onedir, built by build/server-exe/build.py): one copy per machine or user,
// shared by every Revit year, so the 228 files are not repeated six times. The Connect AI dialog finds it at
// <root>\Model MCP\server\revit-model-mcp.exe. It needs neither uv nor Python.
Dir ServerDir(string root)
{
    if (!Directory.Exists(serverSourceFolder) || !System.IO.File.Exists(Path.Combine(serverSourceFolder, "revit-model-mcp.exe")))
        throw new Exception($"The bundled server is missing: run `python build/server-exe/build.py` first ({serverSourceFolder}).");
    var feature = new Feature
    {
        Name = "Bundled MCP server",
        Description = "The Model MCP server (no Python or uv needed) that AI apps start",
        AllowChange = false
    };
    return new Dir($@"{root}\{productName}\server", new Files(feature, $@"{serverSourceFolder}\*.*"));
}

// The machine add-in roots: the Dir form for packaging and the property form for file searches.
(string Dir, string Search) MachineAddinsRoot(int year) => year switch
{
    >= 2027 => (@"%ProgramFiles%\Autodesk\Revit\Addins", @"[ProgramFiles64Folder]Autodesk\Revit\Addins"),
    _ => (@"%CommonAppDataFolder%\Autodesk\Revit\Addins", @"[CommonAppDataFolder]Autodesk\Revit\Addins")
};

// UpgradeCode and component GUIDs stay shared between the two packages on purpose, so 0.9.0 installs
// upgrade in place. The launch condition makes the shared components safe: the scopes can no longer coexist.
// "Installed" keeps uninstall, repair and maintenance of the product itself working on a conflicted machine.
IEnumerable<Property> OtherScopeProperties(string scope, Func<int, string> addinsFolder) =>
    wixEntities.Select(entity => new Property($"RMM_OTHER_SCOPE_{entity.Version}",
        new DirectorySearch(new Id($"OtherScope{scope}Dir{entity.Version}"), null!, false, 0,
            new FileSearch(new Id($"OtherScope{scope}File{entity.Version}"), "RevitModelMcp.addin"))
        {
            Path = addinsFolder(entity.Version)
        })
    {
        Secure = true
    });

LaunchCondition OtherScopeLaunchCondition(string message) =>
    new("Installed OR (" + string.Join(" AND ",
        wixEntities.Select(entity => $"RMM_OTHER_SCOPE_{entity.Version}=\"\"")) + ")", message);

// UPDATECHECK=0 is the legacy alias for UPDATEMODE=off. Both persist in settings.json so they survive upgrades.
// The mode value cannot be spliced into a deferred script, so each allowed value gets its own action.
IEnumerable<PathFileAction> UpdateSettingActions(string environmentFolder, bool elevated)
{
    yield return UpdateSettingAction(environmentFolder, elevated, "DisableUpdateCheck",
        "updateCheck", "$false", "UPDATECHECK=\"0\"");
    foreach (var mode in new[] { "off", "notify", "auto" })
        yield return UpdateSettingAction(environmentFolder, elevated, $"SetUpdateMode{mode}",
            "updateMode", $"'{mode}'", $"UPDATEMODE~=\"{mode}\"");
}

// Always creates %ProgramData%\RevitModelMcp with a protected ACL, so an administrator who later drops policy.json
// there gets a folder a standard user cannot have created or changed. Fails the install if the folder is untrustworthy.
PathFileAction EnsureProgramDataFolderAction()
{
    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(MachinePolicyValues.EnsureProgramDataFolderScript));
    return new PathFileAction(new Id("MachineEnsureProgramDataFolder"),
        @"[SystemFolder]WindowsPowerShell\v1.0\powershell.exe",
        $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
        "SystemFolder", Return.check, When.After, Step.InstallFiles,
        new Condition("NOT REMOVE~=\"ALL\""))
    {
        Execute = Execute.deferred,
        Impersonate = false
    };
}

// Pre-check of the same folder test, so a refusal comes with a sentence in the MSI log instead of a bare 1722/1603 from
// the ensure script. WixQuietExec copies the script output to the log. It runs as SYSTEM right after InstallInitialize,
// before any file or registry change, so the rollback has nothing to undo. Needs the WiX Util extension.
WixQuietExecAction CheckProgramDataFolderAction() =>
    new(new Id("MachineCheckProgramDataFolder"),
        @"[SystemFolder]WindowsPowerShell\v1.0\powershell.exe",
        $"-NoProfile -NonInteractive -EncodedCommand {InstallerScripts.Encode(InstallerScripts.CheckProgramDataFolderScript(false))}",
        Return.check, When.After, Step.InstallInitialize, new Condition("NOT REMOVE~=\"ALL\""))
    {
        Impersonate = false
    };

// The same check in the user's session before the install sequence, only with a UI: the reason is shown in a message box.
PathFileAction CheckProgramDataFolderMessageAction() =>
    new(new Id("MachineCheckProgramDataFolderMessage"),
        @"[SystemFolder]WindowsPowerShell\v1.0\powershell.exe",
        $"-NoProfile -NonInteractive -EncodedCommand {InstallerScripts.Encode(InstallerScripts.CheckProgramDataFolderScript(true))}",
        "SystemFolder", Return.check, When.Before, Step.InstallInitialize,
        new Condition("UILevel>=4 AND NOT REMOVE~=\"ALL\""));

// READONLY=0 on a maintenance run (REINSTALL=ALL): the ReadOnly component condition turns false, but Windows Installer
// keeps a value of an installed component, so this removes the value the installer wrote, as an upgrade does.
PathFileAction ClearMsiReadOnlyAction() =>
    new(new Id("MachineClearMsiReadOnly"),
        @"[SystemFolder]WindowsPowerShell\v1.0\powershell.exe",
        $"-NoProfile -NonInteractive -EncodedCommand {InstallerScripts.Encode(InstallerScripts.ClearMsiReadOnlyScript)}",
        "SystemFolder", Return.check, When.After, Step.WriteRegistryValues,
        new Condition(InstallerScripts.ClearMsiReadOnlyCondition))
    {
        Execute = Execute.deferred,
        Impersonate = false
    };

// Uninstall (not the removal step of an upgrade) deletes the MSI-written settings.json and then the empty folder.
// policy.json or any other file keeps the folder.
PathFileAction RemoveProgramDataFolderAction() =>
    new(new Id("MachineRemoveProgramDataFolder"),
        @"[SystemFolder]WindowsPowerShell\v1.0\powershell.exe",
        $"-NoProfile -NonInteractive -EncodedCommand {InstallerScripts.Encode(InstallerScripts.RemoveProgramDataFolderScript)}",
        "SystemFolder", Return.ignore, When.After, Step.RemoveFiles,
        new Condition("REMOVE~=\"ALL\" AND NOT UPGRADINGPRODUCTCODE"))
    {
        Execute = Execute.deferred,
        Impersonate = false
    };

PathFileAction UpdateSettingAction(string environmentFolder, bool elevated, string id, string key, string value, string condition)
{
    // The elevated script first makes the machine folder admin-only, so SYSTEM never writes into a folder a user owns.
    var script = (elevated ? MachinePolicyValues.EnsureProgramDataFolderScript : "") +
        $"$path = Join-Path $env:{environmentFolder} 'RevitModelMcp\\settings.json'; " +
        "$directory = Split-Path $path; New-Item -ItemType Directory -Path $directory -Force | Out-Null; " +
        "$settings = if (Test-Path $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }; " +
        $"$settings | Add-Member -NotePropertyName {key} -NotePropertyValue {value} -Force; " +
        "$settings | ConvertTo-Json -Compress | Set-Content -LiteralPath $path -Encoding UTF8";
    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    return new PathFileAction(new Id((elevated ? "Machine" : "User") + id),
        @"[SystemFolder]WindowsPowerShell\v1.0\powershell.exe",
        $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
        "SystemFolder", Return.check, When.After, Step.InstallFiles,
        new Condition($"{condition} AND NOT REMOVE~=\"ALL\""))
    {
        Execute = Execute.deferred,
        Impersonate = !elevated
    };
}
