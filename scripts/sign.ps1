#!/usr/bin/env pwsh
# Authenticode-sign a file with the release code-signing certificate.
#
# Used by .github/workflows/release.yml. Reads the PFX (base64) + password from the
# CERT / CERT_PASS environment variables (wired from repo secrets WINDOWS_CERT_BASE64
# and WINDOWS_CERT_PASSWORD) so the certificate never lands on disk in the repo.
#
# No-op-friendly: the workflow only invokes this when a cert secret is configured.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$File
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $File)) { throw "File to sign not found: $File" }
if (-not $env:CERT) { throw "CERT env var (base64 PFX) is not set" }

# Locate the newest signtool.exe from the installed Windows SDKs.
$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found in the Windows 10 SDK" }

$pfx = Join-Path ([System.IO.Path]::GetTempPath()) "chll-release-cert.pfx"
try {
    [System.IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:CERT))

    $args = @('sign', '/fd', 'sha256', '/f', $pfx)
    if ($env:CERT_PASS) { $args += @('/p', $env:CERT_PASS) }
    # RFC-3161 timestamp so signatures stay valid after the cert expires.
    $args += @('/tr', 'http://timestamp.digicert.com', '/td', 'sha256', $File)

    & $signtool.FullName @args
    if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }
    Write-Host "Signed $File"
}
finally {
    if (Test-Path $pfx) { Remove-Item $pfx -Force }
}
