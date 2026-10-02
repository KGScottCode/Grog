# Builds the Windows installer locally, the same way release.yml does in CI: publish app and CLI into
# ONE folder, stage the license files, then hand that folder to ISCC.
#
#   powershell -ExecutionPolicy Bypass -File packaging\windows\build-installer.ps1
#
# Output: dist\Grog-win-x64-setup.exe

[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# Repo root is two levels up from this script, so the script works from any working directory.
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $repo
try {
    # ISCC is not on PATH by default, and winget may install per-machine OR per-user. Ask the uninstall
    # registry key first (authoritative wherever it landed), then fall back to the usual roots.
    $candidates = @()

    foreach ($hive in 'HKLM:', 'HKCU:') {
        foreach ($wow in '\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                         '\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
            $key = Join-Path "$hive$wow" 'Inno Setup 6_is1'
            $loc = (Get-ItemProperty -Path $key -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation
            if ($loc) { $candidates += (Join-Path $loc 'ISCC.exe') }
        }
    }

    $candidates += @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )

    $onPath = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    if ($onPath) { $candidates += $onPath }

    $iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if (-not $iscc) {
        throw ("Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php " +
               "(or: winget install JRSoftware.InnoSetup).`nLooked in:`n  " + ($candidates -join "`n  "))
    }
    Write-Host "using $iscc" -ForegroundColor DarkGray

    $publish = Join-Path $repo 'publish'
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    # Both projects into the SAME folder: one shared copy of the runtime, and the two halves can never
    # ship at different versions.
    foreach ($proj in 'src\Grog.App\Grog.App.csproj', 'src\Grog.Cli\Grog.Cli.csproj') {
        Write-Host "publish $proj" -ForegroundColor Cyan
        dotnet publish $proj -c $Configuration -r win-x64 --self-contained true -p:Version=$Version -o $publish
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj" }
    }

    # The About page opens these from beside the executable, so a build without them has dead buttons.
    Copy-Item LICENSE, THIRD-PARTY-NOTICES.txt, README.md $publish -Force
    Remove-Item (Join-Path $publish '*.pdb') -Force -ErrorAction SilentlyContinue

    Write-Host "compile installer" -ForegroundColor Cyan
    & $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" (Join-Path $PSScriptRoot 'Grog.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed with $LASTEXITCODE" }

    $out = Join-Path $repo 'dist\Grog-win-x64-setup.exe'
    Write-Host "`nBuilt $out" -ForegroundColor Green
    Get-FileHash $out -Algorithm SHA256 | Format-List Algorithm, Hash
}
finally {
    Pop-Location
}
