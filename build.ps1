[CmdletBinding()]
param(
    [switch]$Test,
    [string]$Version,
    [string]$OutputDirectory = 'artifacts/JavaSelect'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot)
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

function Assert-OutputNotRunning([string]$Directory) {
    $prefix = $Directory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($process in @(Get-Process -Name JavaSelect, JavaSelect.Repair -ErrorAction SilentlyContinue)) {
        try { $executable = $process.Path } catch { $executable = $null }
        if (!$executable) { throw 'Cannot inspect a running JavaSelect process. Close it before building, or use a separate machine.' }
        if ($executable.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "JavaSelect is running from $Directory. Close it or pass -OutputDirectory artifacts/another-build."
        }
    }
}

function Assert-OutputTree([string]$Path) {
    Assert-NoReparseAncestor $Path
    if (!(Test-Path -LiteralPath $Path)) { return }
    if (!(Test-Path -LiteralPath $Path -PathType Container)) { throw "OutputDirectory is not a directory: $Path" }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)
    while ($pending.Count) {
        foreach ($item in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse entries are not supported in build output: $($item.FullName)" }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
}

function Remove-OwnedRepairStage([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $artifactsRoot -or [IO.Path]::GetFileName($resolved) -notmatch '^repair-stage-[0-9a-f]{32}$') {
        throw "Refusing to remove an unexpected build directory: $resolved"
    }
    if (!(Test-Path -LiteralPath $resolved)) { return }
    Assert-NoReparseAncestor $resolved
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($resolved)
    while ($pending.Count) {
        foreach ($item in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing to remove a reparse entry: $($item.FullName)" }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

if (!$Version) {
    [xml]$properties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
    $Version = $properties.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
}
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
    @($Version.Split('.') | Where-Object { $_.Length -gt 5 -or [int]$_ -gt 65535 }).Count) {
    throw 'Version must be x.y.z, with each number from 0 to 65535 and no leading zeroes.'
}
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory)) }
if (!$outputPath.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be a directory beneath this repository''s artifacts folder.'
}
Assert-OutputTree $outputPath
Assert-OutputNotRunning $outputPath
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$repairStage = Join-Path $artifactsRoot ('repair-stage-' + [Guid]::NewGuid().ToString('N'))
$repairStageCreated = $false
$savedEnvironment = @{}
foreach ($name in @('MSBuildEnableWorkloadResolver', 'MSBUILDDISABLENODEREUSE', 'DOTNET_CLI_USE_MSBUILD_SERVER')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$env:MSBuildEnableWorkloadResolver = 'false'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$buildArguments = @('-c', 'Release', "-p:Version=$Version", '-p:UseSharedCompilation=false', '-nodeReuse:false')
Push-Location $repositoryRoot
try {
    if ($Test) {
        foreach ($testProject in @('tests/JavaSelect.Core.Tests/JavaSelect.Core.Tests.csproj', 'tests/JavaSelect.Platform.Tests/JavaSelect.Platform.Tests.csproj')) {
            & $dotnet build $testProject @buildArguments
            if ($LASTEXITCODE -ne 0) { throw "Test build failed: $testProject" }
            & $dotnet run --project $testProject -c Release --no-build --no-restore
            if ($LASTEXITCODE -ne 0) { throw "Tests failed: $testProject" }
        }
    }
    Assert-OutputTree $outputPath
    Assert-OutputNotRunning $outputPath
    & $dotnet publish 'src/JavaSelect.App/JavaSelect.App.csproj' @buildArguments -r win-x64 --self-contained true -o $outputPath
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    Assert-NoReparseAncestor $repairStage
    $null = New-Item -ItemType Directory -Path $repairStage
    $repairStageCreated = $true
    & $dotnet publish 'src/JavaSelect.Repair/JavaSelect.Repair.csproj' @buildArguments -r win-x64 --self-contained true -o $repairStage
    if ($LASTEXITCODE -ne 0) { throw 'Repair component build failed.' }
    Assert-OutputTree $outputPath
    Assert-OutputNotRunning $outputPath
    Copy-Item -LiteralPath (Join-Path $repairStage 'JavaSelect.Repair.exe') -Destination (Join-Path $outputPath 'JavaSelect.Repair.exe') -Force
    Write-Output (Join-Path $outputPath 'JavaSelect.exe')
}
finally {
    Pop-Location
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    if ($repairStageCreated) { Remove-OwnedRepairStage $repairStage }
}
