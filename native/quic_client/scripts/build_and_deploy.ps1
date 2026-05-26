# build_and_deploy.ps1 — Windows build + Unity Plugins deploy.
#
# Builds the dgsvshs_socket cdylib in release and copies the resulting .dll into the Unity
# project's plugin folder so the next Unity Play / Build picks it up automatically.

$ErrorActionPreference = "Stop"

$crateRoot   = Resolve-Path "$PSScriptRoot/.."
$repoRoot    = Resolve-Path "$crateRoot/../.."
$pluginDir   = "$repoRoot/DGSvsHS/Assets/Plugins/x86_64"
$dllSource   = "$crateRoot/target/release/dgsvshs_socket.dll"
$dllTarget   = "$pluginDir/dgsvshs_socket.dll"

Write-Host "[build] cargo build --release --lib in $crateRoot"
Push-Location $crateRoot
try {
    cargo build --release --lib
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build failed (exit $LASTEXITCODE)"
    }
} finally {
    Pop-Location
}

if (-not (Test-Path $dllSource)) {
    throw "expected output not found: $dllSource"
}

New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item -Force $dllSource $dllTarget

Write-Host "[deploy] copied to $dllTarget"
Write-Host "[done] Unity will reimport on next focus."
