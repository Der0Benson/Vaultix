#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string] $RepositoryPath = (Split-Path -Parent $PSScriptRoot),
    [string] $Branch = 'main'
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath($RepositoryPath)
if (-not (Test-Path -LiteralPath (Join-Path $repository '.git'))) { throw "Not a Git repository: $repository" }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is required for updates.' }
if (& git -C $repository status --porcelain) { throw 'The repository has local changes. Commit, stash, or discard them before updating.' }
& git -C $repository pull --ff-only origin $Branch
if ($LASTEXITCODE -ne 0) { throw 'Git update failed.' }
& (Join-Path $PSScriptRoot 'Install-Vaultix.ps1') -RepositoryPath $repository
if ($LASTEXITCODE -ne 0) { throw 'Vaultix installation after the update failed.' }

$appExecutable = Join-Path $env:ProgramFiles 'Vaultix\app\Vaultix.App.exe'
if (-not (Test-Path -LiteralPath $appExecutable)) { throw "Updated Vaultix app was not found: $appExecutable" }
Start-Process -FilePath $appExecutable
