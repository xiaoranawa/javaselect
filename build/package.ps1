[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$IsccPath,
    [switch]$Test,
    [string]$OutputDirectory = 'artifacts/release',
    # Release flavors. By default the portable zip bundles the .NET runtime (nothing to install first), while the
    # installer relies on the machine's .NET 10 runtime: when it is missing, the .NET apphost itself shows Microsoft's
    # standard "You must install .NET to run this application" dialog with a download button, and the app starts once
    # the runtime is installed. Flip either flavor with these switches; the Windows App SDK stays self-contained.
    [switch]$PortableFrameworkDependent,
    [switch]$InstallerSelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'

function Assert-NoReparseAncestor([string]$Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse paths are not supported: $candidate" }
        }
        $parent = [IO.Directory]::GetParent($candidate)
        $candidate = if ($parent) { $parent.FullName } else { $null }
    }
}

function Get-SafeStageItems([string]$Path) {
    Assert-NoReparseAncestor $Path
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)
    while ($pending.Count) {
        foreach ($item in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse entries are not permitted in a release: $($item.FullName)" }
            $item
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
}

function Remove-OwnedPackageStage([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $artifactsRoot -or [IO.Path]::GetFileName($resolved) -notmatch '^package-stage-[0-9a-f]{32}$') {
        throw "Refusing to remove an unexpected package directory: $resolved"
    }
    if (!(Test-Path -LiteralPath $resolved)) { return }
    $null = @(Get-SafeStageItems $resolved)
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

# The WinUI language folders that may ship; the single source of truth is the build's default.
function Get-KeptLanguages {
    [xml]$targets = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build/WinUILanguages.targets') -Raw
    $value = $targets.SelectSingleNode('/Project/PropertyGroup/JavaSelectKeepLanguages').InnerText
    $languages = @($value.Split(';', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() })
    if (!$languages.Count) { throw 'build/WinUILanguages.targets does not define JavaSelectKeepLanguages.' }
    $languages
}

# The installer removes obsolete WinUI language folders on upgrade and must not touch the ones the build still ships.
function Assert-InstallerKeepsSameLanguages([string]$ScriptPath) {
    $match = Select-String -LiteralPath $ScriptPath -Pattern '^\s*#define KeptLanguages "([^"]+)"' | Select-Object -First 1
    if (!$match) { throw 'installer/JavaSelect.iss does not define KeptLanguages.' }
    $installerLanguages = @($match.Matches[0].Groups[1].Value.Split(',') | Sort-Object)
    $buildLanguages = @(Get-KeptLanguages | Sort-Object)
    if (@(Compare-Object $installerLanguages $buildLanguages -CaseSensitive).Count) {
        throw "installer/JavaSelect.iss KeptLanguages ($($installerLanguages -join ',')) must match build/WinUILanguages.targets ($($buildLanguages -join ','))."
    }
}

# Publishes one flavor of the app (and its repair helper) into a stage directory.
function Publish-AppStage([string]$Stage, [bool]$FrameworkDependent, [bool]$RunTests) {
    $buildArguments = @{ Version = $Version; OutputDirectory = $Stage }
    if ($RunTests) { $buildArguments.Test = $true }
    if ($FrameworkDependent) { $buildArguments.FrameworkDependent = $true }
    & (Join-Path $repositoryRoot 'build.ps1') @buildArguments | Out-Host
}

# Verifies that a stage really is the flavor it claims to be and is complete, then strips build/test debris.
function Confirm-AppStage([string]$Stage, [bool]$FrameworkDependent, [string]$Label) {
    $requiredFiles = @(
        'JavaSelect.exe', 'JavaSelect.Repair.exe', 'JavaSelect.dll', 'JavaSelect.runtimeconfig.json',
        'Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll',
        'JavaSelect.pri', 'App.xbf', 'MainWindow.xbf', 'LauncherWindow.xbf',
        'Assets/icon.png', 'Assets/icon.ico', 'Assets/avatar.png',
        'Microsoft.WindowsAppRuntime.Insights.Resource.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.md'
    )
    $runtimeFiles = @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')
    if (!$FrameworkDependent) { $requiredFiles += $runtimeFiles }
    foreach ($relative in $requiredFiles) {
        $required = Join-Path $Stage $relative
        if (!(Test-Path -LiteralPath $required -PathType Leaf) -or (Get-Item -LiteralPath $required).Length -eq 0) {
            throw "Required $Label file is missing or empty: $relative"
        }
    }
    $runtimeConfig = (Get-Content -LiteralPath (Join-Path $Stage 'JavaSelect.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
    if ($FrameworkDependent) {
        # A bundled host would also be picked up by the apphost and break framework resolution, so none may ship.
        foreach ($name in $runtimeFiles) {
            if (Test-Path -LiteralPath (Join-Path $Stage $name)) { throw "The framework-dependent $Label stage must not bundle the .NET runtime: $name" }
        }
        # Any 10.0.x runtime must satisfy the app, so the minimum is x.0.0 rather than a high patch release.
        if ($runtimeConfig.PSObject.Properties.Name -contains 'includedFrameworks' -or $runtimeConfig.framework.name -ne 'Microsoft.NETCore.App' -or $runtimeConfig.framework.version -notmatch '^\d+\.0\.0$') {
            throw "The framework-dependent $Label stage must reference Microsoft.NETCore.App at its x.0.0 minimum."
        }
    }
    elseif (!($runtimeConfig.PSObject.Properties.Name -contains 'includedFrameworks')) {
        throw "The self-contained $Label stage does not include the .NET runtime."
    }
    $keptLanguages = Get-KeptLanguages
    foreach ($language in $keptLanguages) {
        if (!(Test-Path -LiteralPath (Join-Path $Stage $language) -PathType Container)) { throw "The $Label stage is missing the WinUI language folder: $language" }
    }
    foreach ($directory in @(Get-ChildItem -LiteralPath $Stage -Directory)) {
        if ($directory.Name -match '^[A-Za-z]{2,3}(-[A-Za-z0-9]+)+$' -and $directory.Name -notin $keptLanguages) {
            throw "The $Label stage contains an unexpected language folder: $($directory.Name)"
        }
    }
    $stageItems = @(Get-SafeStageItems $Stage)
    foreach ($item in @($stageItems | Sort-Object { $_.FullName.Length } -Descending)) {
        if (($item.PSIsContainer -and ($item.Name -in @('cache', '.cache', 'logs') -or $item.Name -like 'smoke-config*')) -or
            (!$item.PSIsContainer -and ($item.Extension -in @('.pdb', '.log', '.tmp') -or $item.Name -like 'smoke-config*'))) {
            $resolved = [IO.Path]::GetFullPath($item.FullName)
            if (!$resolved.StartsWith($Stage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected path while cleaning release staging.' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    $files = @(Get-ChildItem -LiteralPath $Stage -Recurse -File)
    $topLevelFiles = @(Get-ChildItem -LiteralPath $Stage -File).Count
    $topLevelDirectories = @(Get-ChildItem -LiteralPath $Stage -Directory).Count
    $megabytes = '{0:N1}' -f (($files | Measure-Object Length -Sum).Sum / 1MB)
    Write-Host "$Label stage ($(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' })): $topLevelFiles files and $topLevelDirectories folders at the top level, $($files.Count) files, $megabytes MB"
}

if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
    @($Version.Split('.') | Where-Object { $_.Length -gt 5 -or [int]$_ -gt 65535 }).Count) {
    throw 'Version must be x.y.z, with each number from 0 to 65535 and no leading zeroes.'
}
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory)) }
if (!$outputPath.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be a directory beneath this repository''s artifacts folder.'
}
Assert-NoReparseAncestor $outputPath
$artifactNames = @("JavaSelect-$Version-win-x64-portable.zip", "JavaSelect-$Version-win-x64-setup.exe", 'SHA256SUMS.txt')
foreach ($name in $artifactNames) {
    if (Test-Path -LiteralPath (Join-Path $outputPath $name)) { throw "Release output already exists: $name. Use a new -OutputDirectory." }
}
$installerScript = Join-Path $repositoryRoot 'installer/JavaSelect.iss'
if (!(Test-Path -LiteralPath $installerScript -PathType Leaf)) { throw "Installer script not found: $installerScript" }
Assert-InstallerKeepsSameLanguages $installerScript
if (!$IsccPath) { $IsccPath = $env:ISCC_PATH }
if (!$IsccPath) {
    $command = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { $IsccPath = $command.Source }
    foreach ($programRoot in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if (!$programRoot -or $IsccPath) { continue }
        $candidate = Join-Path $programRoot 'Inno Setup 7/ISCC.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $IsccPath = $candidate }
    }
}
if (!$IsccPath -or !(Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup 7 compiler was not found. Run build/Get-InnoSetup.ps1 or pass -IsccPath with the location of ISCC.exe.'
}
$IsccPath = (Resolve-Path -LiteralPath $IsccPath).ProviderPath
$portableFrameworkDependent = [bool]$PortableFrameworkDependent
$installerFrameworkDependent = -not [bool]$InstallerSelfContained
$packageStage = Join-Path $artifactsRoot ('package-stage-' + [Guid]::NewGuid().ToString('N'))
Assert-NoReparseAncestor $packageStage
$null = New-Item -ItemType Directory -Path $packageStage
$portableStage = Join-Path $packageStage 'JavaSelect'
# Identical flavors share one build; otherwise the installer payload is published separately.
$installerAppStage = if ($installerFrameworkDependent -eq $portableFrameworkDependent) { $portableStage } else { Join-Path $packageStage 'JavaSelect-installer' }
$releaseStage = Join-Path $packageStage 'release'
$installerStage = Join-Path $packageStage 'installer'
try {
    $null = New-Item -ItemType Directory -Path $releaseStage
    $null = New-Item -ItemType Directory -Path $installerStage
    # The regression tests are flavor independent, so they run once, with the first build.
    Publish-AppStage $portableStage $portableFrameworkDependent ([bool]$Test)
    Confirm-AppStage $portableStage $portableFrameworkDependent 'portable'
    if ($installerAppStage -ne $portableStage) {
        Publish-AppStage $installerAppStage $installerFrameworkDependent $false
        Confirm-AppStage $installerAppStage $installerFrameworkDependent 'installer'
    }
    $zipPath = Join-Path $releaseStage $artifactNames[0]
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($portableStage, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    & $IsccPath '/Qp' "/DAppVersion=$Version" "/DAppSource=$installerAppStage" "/DOutputDir=$installerStage" $installerScript
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
    $payloadPath = Join-Path $installerStage $artifactNames[1]
    if (!(Test-Path -LiteralPath $payloadPath -PathType Leaf) -or (Get-Item -LiteralPath $payloadPath).Length -eq 0) { throw 'Inno Setup did not create the expected installer.' }
    $setupPath = Join-Path $releaseStage $artifactNames[1]
    & (Join-Path $PSScriptRoot 'Build-InstallerBootstrap.ps1') -PayloadPath $payloadPath -OutputPath $setupPath -Version $Version -Test:$Test
    $checksums = foreach ($name in $artifactNames[0..1]) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $releaseStage $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $name"
    }
    [IO.File]::WriteAllText((Join-Path $releaseStage $artifactNames[2]), ($checksums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    Assert-NoReparseAncestor $outputPath
    $null = New-Item -ItemType Directory -Path $outputPath -Force
    foreach ($name in $artifactNames) {
        $destination = Join-Path $outputPath $name
        [IO.File]::Move((Join-Path $releaseStage $name), $destination)
        Write-Output $destination
    }
    foreach ($name in $artifactNames[0..1]) { Write-Host ('{0}: {1:N1} MB' -f $name, ((Get-Item -LiteralPath (Join-Path $outputPath $name)).Length / 1MB)) }
}
finally { Remove-OwnedPackageStage $packageStage }
