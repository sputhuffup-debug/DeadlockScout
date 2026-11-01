# Builds the installer from a `dotnet publish` folder: installer\build.ps1 -Publish publish -Out installer-out
# The version comes from Directory.Build.props, so the installer always says what the app says.
param(
    [Parameter(Mandatory)] [string] $Publish,
    [Parameter(Mandatory)] [string] $Out
)
$ErrorActionPreference = 'Stop'
$version = (dotnet msbuild src/DeadlockScout.App -getProperty:Version -p:Configuration=Release).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Unexpected version '$version'" }

# Inno Setup 6 installs machine-wide under Program Files (x86), or per user under %LOCALAPPDATA%\Programs (winget
# without administrator rights). Chocolatey installs it only where Chocolatey exists, which is CI's Windows runner.
$candidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
function Find-Iscc { $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1 }
$iscc = Find-Iscc
if (-not $iscc -and (Get-Command choco -ErrorAction SilentlyContinue)) {
    choco install innosetup --no-progress -y | Out-Null
    $iscc = Find-Iscc
}
if (-not $iscc) { throw 'Inno Setup 6 is not installed. Install it with: winget install --id JRSoftware.InnoSetup -e' }

New-Item -ItemType Directory -Force -Path $Out | Out-Null
& $iscc "/DAppVersion=$version" "/DSourceDir=$((Resolve-Path $Publish).Path)" "/O$((Resolve-Path $Out).Path)" installer\DeadlockScout.iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with $LASTEXITCODE" }

$setup = Join-Path $Out "DeadlockScout-Setup-$version.exe"
$hash = (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $setup)" | Set-Content -Encoding ascii (Join-Path $Out 'SHA256SUMS.txt')
Write-Host "Built $setup ($hash)"
