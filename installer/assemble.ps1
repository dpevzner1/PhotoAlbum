<#
  assemble.ps1 - reassemble the chunked Photo Album installer.

  GitHub rejects any single file larger than 100 MB, so the installer
  executable is committed as a set of parts under 95 MB each
  (PhotoAlbum-Setup.exe.001, .002, ...). This script concatenates them back
  into PhotoAlbum-Setup.exe and verifies the result against the SHA-256 in
  PhotoAlbum-Setup.exe.sha256.

  Usage:
      powershell -ExecutionPolicy Bypass -File assemble.ps1
#>
$ErrorActionPreference = "Stop"
$here   = $PSScriptRoot
$target = Join-Path $here "PhotoAlbum-Setup.exe"
$parts  = Get-ChildItem -Path (Join-Path $here "PhotoAlbum-Setup.exe.[0-9][0-9][0-9]") | Sort-Object Name

if ($parts.Count -eq 0) { throw "No PhotoAlbum-Setup.exe.### parts found in $here." }

Write-Host "Reassembling $($parts.Count) part(s) into PhotoAlbum-Setup.exe ..."
$out = [System.IO.File]::Create($target)
try {
    foreach ($p in $parts) {
        Write-Host "  + $($p.Name)"
        $bytes = [System.IO.File]::ReadAllBytes($p.FullName)
        $out.Write($bytes, 0, $bytes.Length)
    }
} finally { $out.Close() }

$shaFile = Join-Path $here "PhotoAlbum-Setup.exe.sha256"
if (Test-Path $shaFile) {
    $expected = (Get-Content $shaFile -Raw).Split(' ')[0].Trim().ToLower()
    $actual   = (Get-FileHash $target -Algorithm SHA256).Hash.ToLower()
    if ($expected -eq $actual) {
        Write-Host "SHA-256 OK - installer is intact." -ForegroundColor Green
    } else {
        Remove-Item $target -Force
        throw "SHA-256 MISMATCH. Expected $expected, got $actual. Download may be corrupt."
    }
} else {
    Write-Warning "No .sha256 file found - skipping integrity check."
}

Write-Host "Done. Run PhotoAlbum-Setup.exe to install."
