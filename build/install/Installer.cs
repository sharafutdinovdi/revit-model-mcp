using System.Text;
using Installer;
using WixSharp;
using WixSharp.CommonTasks;
using WixSharp.Controls;

const string outputName = "RevitModelMcp";
const string projectName = "RevitModelMcp";

var versioning = Versioning.CreateFromVersionString(args[0]);
var wixEntities = Generator.GenerateWixEntities(args[1..]);

BuildSingleUserMsi();
BuildMultiUserMsi();

Project CreateProject(InstallScope scope)
{
    var project = new Project
    {
        OutDir = "output",
        Name = projectName,
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
    project.RemoveDialogsBetween(NativeDialogs.WelcomeDlg, NativeDialogs.CustomizeDlg);
    return project;
}

void BuildSingleUserMsi()
{
    var project = CreateProject(InstallScope.perUser);
    project.Properties =
    [
        new Property("UPDATECHECK", "1") { Secure = true },
        .. OtherScopeProperties("Machine", year => $@"{MachineAddinsRoot(year).Search}\{year}")
    ];
    project.LaunchConditions =
    [
        OtherScopeLaunchCondition(
            "The Revit Model MCP MultiUser (all users) installer is already installed on this computer. " +
            "Uninstall it from Apps > Installed apps, then run this SingleUser installer again.")
    ];
    project.RegValues = [];
    project.Actions = [UpdateSettingAction("LOCALAPPDATA", false)];
    project.OutFileName = $"{outputName}-{versioning.Version}-SingleUser";
    project.Dirs =
    [
        new Dir(@"%AppDataFolder%\Autodesk\Revit\Addins\", [.. wixEntities.Select(entity => entity.Directory)])
    ];
    project.BuildMsi();
}

void BuildMultiUserMsi()
{
    var project = CreateProject(InstallScope.perMachine);
    project.LaunchConditions =
    [
        OtherScopeLaunchCondition(
            "The Revit Model MCP SingleUser (current user) installer is already installed for this user. " +
            "Uninstall it from Apps > Installed apps, then run this MultiUser installer again.")
    ];
    project.Properties =
    [
        .. OtherScopeProperties("User", year => $@"[AppDataFolder]Autodesk\Revit\Addins\{year}"),
        new Property("HTTP_ENABLED", "0") { Secure = true },
        new Property("UPDATECHECK", "1") { Secure = true },
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
        }
    ];
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
        UpdateSettingAction("ProgramData", true)
    ];
    project.OutFileName = $"{outputName}-{versioning.Version}-MultiUser";

    project.Dirs = wixEntities
        .GroupBy(entity => MachineAddinsRoot(entity.Version).Dir)
        .Select(root => new Dir(root.Key, [.. root.Select(entity => entity.Directory)]))
        .ToArray();

    project.BuildMsi();
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
        new DirectorySearch(new Id($"OtherScope{scope}Dir{entity.Version}"), addinsFolder(entity.Version), false, 0,
            new FileSearch(new Id($"OtherScope{scope}File{entity.Version}"), "RevitModelMcp.addin")))
    {
        Secure = true
    });

LaunchCondition OtherScopeLaunchCondition(string message) =>
    new("Installed OR (" + string.Join(" AND ",
        wixEntities.Select(entity => $"RMM_OTHER_SCOPE_{entity.Version}=\"\"")) + ")", message);

PathFileAction UpdateSettingAction(string environmentFolder, bool elevated)
{
    var script = $"$path = Join-Path $env:{environmentFolder} 'RevitModelMcp\\settings.json'; " +
        "$directory = Split-Path $path; New-Item -ItemType Directory -Path $directory -Force | Out-Null; " +
        "$settings = if (Test-Path $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }; " +
        "$settings | Add-Member -NotePropertyName updateCheck -NotePropertyValue $false -Force; " +
        "$settings | ConvertTo-Json -Compress | Set-Content -LiteralPath $path -Encoding UTF8";
    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    return new PathFileAction(new Id(elevated ? "DisableMachineUpdateCheck" : "DisableUserUpdateCheck"),
        @"[SystemFolder]WindowsPowerShell\v1.0\powershell.exe",
        $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
        "SystemFolder", Return.check, When.After, Step.InstallFiles,
        new Condition("UPDATECHECK=\"0\" AND NOT REMOVE~=\"ALL\""))
    {
        Execute = Execute.deferred,
        Impersonate = !elevated
    };
}
