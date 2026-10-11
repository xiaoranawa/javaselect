[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$IsccPath,
    [switch]$Test,
    [string]$OutputDirectory = 'artifacts/release'
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
$packageStage = Join-Path $artifactsRoot ('package-stage-' + [Guid]::NewGuid().ToString('N'))
Assert-NoReparseAncestor $packageStage
$null = New-Item -ItemType Directory -Path $packageStage
$appStage = Join-Path $packageStage 'JavaSelect'
$releaseStage = Join-Path $packageStage 'release'
$installerStage = Join-Path $packageStage 'installer'
try {
    $null = New-Item -ItemType Directory -Path $releaseStage
    $null = New-Item -ItemType Directory -Path $installerStage
    & (Join-Path $repositoryRoot 'build.ps1') -Version $Version -OutputDirectory $appStage -Test:$Test
    $requiredFiles = @(
        'JavaSelect.exe', 'JavaSelect.Repair.exe', 'JavaSelect.dll', 'JavaSelect.runtimeconfig.json',
        'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll',
        'JavaSelect.pri', 'App.xbf', 'MainWindow.xbf', 'LauncherWindow.xbf',
        'Assets/icon.png', 'Assets/icon.ico', 'Assets/avatar.png',
        'Microsoft.WindowsAppRuntime.Insights.Resource.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.md'
    )
    foreach ($relative in $requiredFiles) {
        $required = Join-Path $appStage $relative
        if (!(Test-Path -LiteralPath $required -PathType Leaf) -or (Get-Item -LiteralPath $required).Length -eq 0) {
            throw "Required release file is missing or empty: $relative"
        }
    }
    $stageItems = @(Get-SafeStageItems $appStage)
    foreach ($item in @($stageItems | Sort-Object { $_.FullName.Length } -Descending)) {
        if (($item.PSIsContainer -and ($item.Name -in @('cache', '.cache', 'logs') -or $item.Name -like 'smoke-config*')) -or
            (!$item.PSIsContainer -and ($item.Extension -in @('.pdb', '.log', '.tmp') -or $item.Name -like 'smoke-config*'))) {
            $resolved = [IO.Path]::GetFullPath($item.FullName)
            if (!$resolved.StartsWith($appStage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected path while cleaning release staging.' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    $zipPath = Join-Path $releaseStage $artifactNames[0]
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($appStage, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    & $IsccPath '/Qp' "/DAppVersion=$Version" "/DAppSource=$appStage" "/DOutputDir=$installerStage" $installerScript
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
}
finally { Remove-OwnedPackageStage $packageStage }
