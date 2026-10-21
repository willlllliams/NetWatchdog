#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes a GitHub release that the tray panel's update check picks up.

.DESCRIPTION
  Builds the project, computes the SHA-256 of the setup package and creates the release v<VERSION>
  in the repository named in REPOSITORY, with the setup package attached and its SHA-256 written
  into the release notes. Requires the GitHub CLI (gh), signed in with push access.
  Commit and push the code for this version before publishing; the tag is created on the
  default branch of the repository.

.PARAMETER Notes
  Release notes. The SHA-256 line is appended automatically.
#>
param([string]$Notes = '')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

& (Join-Path $root 'build.ps1')

$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
$repo = (Get-Content -LiteralPath (Join-Path $root 'REPOSITORY') -Raw).Trim()
$exe = Join-Path $root "bin\NetWatchdog-Setup-$version.exe"
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()

& gh release view "v$version" --repo $repo *> $null
if ($LASTEXITCODE -eq 0) { throw "Release v$version already exists in $repo; raise VERSION first" }

$body = (($Notes.Trim(), "SHA-256: ``$hash``") | Where-Object { $_ }) -join "`n`n"
& gh release create "v$version" $exe --repo $repo --title "NetWatchdog $version" --notes $body
if ($LASTEXITCODE -ne 0) { throw "gh release create failed (exit code $LASTEXITCODE)" }
Write-Host "Published v$version to https://github.com/$repo/releases (SHA-256 $hash)"
