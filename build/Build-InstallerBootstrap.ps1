[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PayloadPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$Test
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'

function Assert-SafeArtifactPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (!$resolved.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Bootstrap inputs and output must be beneath this repository''s artifacts folder.'
    }
    for ($candidate = $resolved; $candidate; $candidate = [IO.Path]::GetDirectoryName($candidate)) {
        if ((Test-Path -LiteralPath $candidate) -and ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Reparse paths are not supported: $candidate"
        }
    }
    return $resolved
}

function Remove-BootstrapStage([string]$Path) {
    $resolved = Assert-SafeArtifactPath $Path
    if ([IO.Path]::GetDirectoryName($resolved) -ne $artifactsRoot -or [IO.Path]::GetFileName($resolved) -cnotmatch '^package-stage-[0-9a-f]{32}$') {
        throw "Unexpected bootstrap staging directory: $resolved"
    }
    if (!(Test-Path -LiteralPath $resolved)) { return }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($resolved)
    while ($pending.Count) {
        foreach ($item in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse entry found in bootstrap staging: $($item.FullName)" }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
    @($Version.Split('.') | Where-Object { $_.Length -gt 5 -or [int]$_ -gt 65535 }).Count) {
    throw 'Version must be x.y.z, with each number from 0 to 65535 and no leading zeroes.'
}
$payload = Assert-SafeArtifactPath $PayloadPath
$output = Assert-SafeArtifactPath $OutputPath
if (!(Test-Path -LiteralPath $payload -PathType Leaf)) { throw "Installer payload not found: $payload" }
if (Test-Path -LiteralPath $output) { throw "Bootstrap output already exists: $output" }
$compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw '.NET Framework 4 x64 C# compiler was not found.' }
$frameworkSystem = Join-Path ([IO.Path]::GetDirectoryName($compiler)) 'System.dll'
$source = Join-Path $repositoryRoot 'installer/Bootstrap.cs'
$manifest = Join-Path $repositoryRoot 'installer/Bootstrap.manifest'
$icon = Join-Path $repositoryRoot 'assets/icon.ico'
$length = (Get-Item -LiteralPath $payload).Length
if ($length -le 0) { throw 'Installer payload is empty.' }
$hash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
$stage = Join-Path $artifactsRoot ('package-stage-' + [Guid]::NewGuid().ToString('N'))
$null = Assert-SafeArtifactPath $stage
$null = New-Item -ItemType Directory -Path $stage
try {
    $metadata = Join-Path $stage 'BootstrapPayloadInfo.cs'
    $metadataSource = @"
using System.Reflection;
[assembly: AssemblyTitle("JavaSelect Setup")]
[assembly: AssemblyProduct("JavaSelect")]
[assembly: AssemblyDescription("JavaSelect installer")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: AssemblyInformationalVersion("$Version")]
internal static class BootstrapPayloadInfo
{
    internal const string Sha256 = "$hash";
    internal const long Length = ${length}L;
}
"@
    [IO.File]::WriteAllText($metadata, $metadataSource, [Text.UTF8Encoding]::new($false))
    $stagedOutput = Join-Path $stage 'JavaSelect-setup.exe'
    $compilerArgs = @('/nologo', '/noconfig', '/langversion:5', '/optimize+', '/platform:x64', '/target:winexe', "/reference:$frameworkSystem",
        "/out:$stagedOutput", "/win32manifest:$manifest", "/win32icon:$icon", "/resource:$payload,JavaSelect.SetupPayload", $source, $metadata)
    & $compiler @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw 'Installer bootstrap compilation failed.' }

    if ($Test) {
        $testSourcePath = Join-Path $stage 'BootstrapTests.cs'
        $testSource = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

internal static class BootstrapTests
{
    private static int assertions;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--raw-child")
        {
            Console.WriteLine(Bootstrap.EncodeArgument(Bootstrap.RawArguments(Bootstrap.RawArguments(Environment.CommandLine))));
            return 0;
        }
        if (args.Length > 0 && args[0] == "--javaselect-elevated")
        {
            if (args.Length != 4 || args[1] != "S-1-5-21-1-2-3-1001") return 10;
            Console.WriteLine(Bootstrap.EncodeArgument(Bootstrap.DecodeArgument(args[2])));
            Console.WriteLine(Bootstrap.EncodeArgument(Bootstrap.ValidateWorkingDirectory(Bootstrap.DecodeArgument(args[3]))));
            return 0;
        }
        try
        {
            var caller = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "caller directory");
            foreach (var tail in new[] { "", "/SILENT /NORESTART", "/DIR=\"D:\\My Java\\\" /SILENT /LOG=\"中文 日志.log\"", "/LOADINF=\"settings file.inf\"  /DIR=\"D:\\Java\" ", "/PASSWORD=\"quoted\\\"value\"\t/SILENT" })
            {
                Equal(tail, Bootstrap.RawArguments("\"C:\\installer folder\\setup.exe\" " + tail), "raw tail extraction");
                Equal(tail, Bootstrap.DecodeArgument(Bootstrap.EncodeArgument(tail)), "encoded tail round trip");
                var direct = RunChild("--raw-child " + tail, caller);
                Equal(tail, Bootstrap.DecodeArgument(direct[0]), "real child raw tail");
                var elevated = RunChild(Bootstrap.ElevationArguments("S-1-5-21-1-2-3-1001", tail, caller), caller);
                Equal(tail, Bootstrap.DecodeArgument(elevated[0]), "elevation argument round trip");
                Equal(caller, Bootstrap.DecodeArgument(elevated[1]), "caller directory round trip");
            }
            Equal("/SILENT", Bootstrap.RawArguments("C:\\setup.exe\t/SILENT"), "unquoted executable");
            RejectDirectory("relative");
            RejectDirectory("C:relative");
            RejectDirectory(caller + "\\..");

            var assembly = Assembly.LoadFile(Path.GetFullPath(args[0]));
            using (var resource = assembly.GetManifestResourceStream("JavaSelect.SetupPayload"))
            {
                Check(resource != null, "embedded payload exists");
                Check(Bootstrap.PayloadMatches(resource, BootstrapPayloadInfo.Length, BootstrapPayloadInfo.Sha256), "embedded payload hash");
                Check(!Bootstrap.PayloadMatches(resource, BootstrapPayloadInfo.Length + 1, BootstrapPayloadInfo.Sha256), "payload length mismatch");
                Check(!Bootstrap.PayloadMatches(new CorruptedStream(resource), BootstrapPayloadInfo.Length, BootstrapPayloadInfo.Sha256), "corrupted payload is rejected");
            }
            Console.WriteLine("Installer bootstrap: " + assertions + " checks passed; no installer or elevation was executed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static string[] RunChild(string arguments, string directory)
    {
        var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            WorkingDirectory = directory,
            CreateNoWindow = true
        };
        using (var child = Process.Start(start))
        {
            if (child == null) throw new Exception("Could not start test child.");
            if (!child.WaitForExit(10000)) { child.Kill(); throw new Exception("Test child timed out."); }
            if (child.ExitCode != 0) throw new Exception("Test child failed: " + child.ExitCode);
            return child.StandardOutput.ReadToEnd().Replace("\r", "").TrimEnd('\n').Split('\n');
        }
    }

    private static void RejectDirectory(string directory)
    {
        try { Bootstrap.ValidateWorkingDirectory(directory); }
        catch (IOException) { assertions++; return; }
        throw new Exception("Invalid working directory was accepted.");
    }

    private static void Equal(string expected, string actual, string name)
    {
        Check(string.Equals(expected, actual, StringComparison.Ordinal), name);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("Failed: " + name);
        assertions++;
    }

    private sealed class CorruptedStream : Stream
    {
        private readonly Stream inner;
        internal CorruptedStream(Stream value) { inner = value; }
        public override bool CanRead { get { return inner.CanRead; } }
        public override bool CanSeek { get { return inner.CanSeek; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return inner.Length; } }
        public override long Position { get { return inner.Position; } set { inner.Position = value; } }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var position = inner.Position;
            var read = inner.Read(buffer, offset, count);
            if (position == 0 && read > 0) buffer[offset] ^= 1;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) { return inner.Seek(offset, origin); }
        public override void Flush() { }
        public override void SetLength(long length) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}
'@
        [IO.File]::WriteAllText($testSourcePath, $testSource, [Text.UTF8Encoding]::new($false))
        $testExe = Join-Path $stage 'Bootstrap tests.exe'
        $null = New-Item -ItemType Directory -Path (Join-Path $stage 'caller directory')
        & $compiler '/nologo' '/noconfig' '/langversion:5' '/platform:x64' '/target:exe' '/main:BootstrapTests' "/reference:$frameworkSystem" "/out:$testExe" "/win32manifest:$manifest" $source $metadata $testSourcePath
        if ($LASTEXITCODE -ne 0) { throw 'Bootstrap regression harness compilation failed.' }
        & $testExe $stagedOutput
        if ($LASTEXITCODE -ne 0) { throw 'Bootstrap regression checks failed.' }
    }

    $null = Assert-SafeArtifactPath $output
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($output)) -Force
    [IO.File]::Move($stagedOutput, $output)
    Write-Output $output
}
finally { Remove-BootstrapStage $stage }
