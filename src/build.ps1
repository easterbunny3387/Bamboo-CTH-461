# Baut Bamboo-Win11-Installer.exe: packt den entpackten Wacom-Treiber 5.3.5-3 als ZIP in die exe
# und übersetzt Installer.cs mit dem csc.exe, der bei jedem Windows (.NET Framework 4.x) dabei ist.
# Ohne -Paket wird der Ordner pentablet_5.3.5-3 im Repository oder daneben gesucht.
param(
    [string]$Paket    = '',
    [string]$Ziel     = (Join-Path $PSScriptRoot '..\Bamboo-Win11-Installer.exe'),
    [string]$Manifest = (Join-Path $PSScriptRoot 'app.manifest')
)
$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw  = Split-Path $csc
if (-not $Paket) {
    $Paket = @('..\pentablet_5.3.5-3', '..\..\pentablet_5.3.5-3') |
        ForEach-Object { Join-Path $PSScriptRoot $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Paket) { throw 'Treiberordner pentablet_5.3.5-3 nicht gefunden. Bitte mit -Paket <Pfad> angeben.' }
}
$paketPfad = (Resolve-Path $Paket).ProviderPath

# Sicherstellen, dass es wirklich das entpackte Wacom-Paket 5.3.5-3 ist.
$setup = Join-Path $paketPfad 'setup.exe'
if (-not (Test-Path $setup)) { throw "Kein Wacom-Setup in $paketPfad gefunden." }
if ((Get-Item $setup).VersionInfo.FileVersion -ne '5.3.5-3') { throw "$setup ist nicht Version 5.3.5-3." }
if (-not (Test-Path (Join-Path $paketPfad 'System32\64\Drivers\wachidrouter_con.inf'))) { throw "Treiberdateien fehlen in $paketPfad." }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $env:TEMP ('bamboo-paket-{0}.zip' -f [guid]::NewGuid())
Write-Host "Packe $paketPfad ..."
[IO.Compression.ZipFile]::CreateFromDirectory($paketPfad, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

try {
    $argumente = @(
        '/nologo', '/target:winexe', '/optimize+', '/codepage:65001', '/platform:anycpu',
        "/out:$Ziel",
        "/win32manifest:$Manifest",
        "/resource:$zip,paket.zip",
        ('/resource:{0},LIESMICH.txt' -f (Join-Path $PSScriptRoot '..\LIESMICH.txt')),
        '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
        '/r:System.Management.dll', '/r:System.ServiceProcess.dll',
        "/r:$fw\System.IO.Compression.dll", "/r:$fw\System.IO.Compression.FileSystem.dll",
        (Join-Path $PSScriptRoot 'Installer.cs')
    )
    & $csc @argumente
    if ($LASTEXITCODE -ne 0) { throw "Compiler-Fehler (Code $LASTEXITCODE)" }
}
finally {
    Remove-Item $zip -ErrorAction SilentlyContinue
}
Get-Item $Ziel | Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
