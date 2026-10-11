param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolDirectory = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $toolDirectory) { throw "Tool destination must be new: $toolDirectory" }
if ($toolDirectory.Contains('"')) { throw 'The tool path cannot contain a quote.' }
$toolInstaller = Join-Path ([IO.Path]::GetDirectoryName($toolDirectory)) ('inno-download-' + [Guid]::NewGuid().ToString('N') + '.exe')
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($toolDirectory)) -Force | Out-Null
try {
    Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' -OutFile $toolInstaller
    $expected = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
    if ((Get-FileHash -LiteralPath $toolInstaller -Algorithm SHA256).Hash -ne $expected) { throw 'Inno Setup download checksum mismatch.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $toolInstaller
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B\.V\.') { throw 'Inno Setup publisher signature verification failed.' }
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', '/PORTABLE=1', '/NOICONS', '/TASKS=""', ('/DIR="' + $toolDirectory + '"'))
    $process = Start-Process -FilePath $toolInstaller -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Inno Setup portable extraction failed: $($process.ExitCode)" }
    $compiler = Join-Path $toolDirectory 'ISCC.exe'
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Inno Setup compiler was not extracted.' }
    $compiler
}
finally {
    if (Test-Path -LiteralPath $toolInstaller) { Remove-Item -LiteralPath $toolInstaller -Force }
}
