#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string] $RepositoryPath = (Split-Path -Parent $PSScriptRoot),
    [string] $InstallPath = (Join-Path $env:ProgramFiles 'Vaultix')
)

$ErrorActionPreference = 'Stop'
$serviceName = 'Vaultix.Service'
$repository = [IO.Path]::GetFullPath($RepositoryPath)
$serviceProject = Join-Path $repository 'src\Vaultix.Service\Vaultix.Service.csproj'
$appProject = Join-Path $repository 'src\Vaultix.App\Vaultix.App.csproj'
$servicePath = Join-Path $InstallPath 'service'
$appPath = Join-Path $InstallPath 'app'

if (-not (Test-Path -LiteralPath $serviceProject) -or -not (Test-Path -LiteralPath $appProject)) { throw "RepositoryPath does not contain the Vaultix projects: $repository" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 10 SDK is required to install or update Vaultix.' }
if (Get-Process -Name 'Vaultix.App' -ErrorAction SilentlyContinue) { throw 'Close the Vaultix desktop app before installing or updating it.' }

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$wasRunning = $existingService -and $existingService.Status -eq 'Running'
if ($wasRunning) { Stop-Service -Name $serviceName -Force }

try {
    New-Item -ItemType Directory -Path $servicePath,$appPath -Force | Out-Null
    & dotnet publish $serviceProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $servicePath
    if ($LASTEXITCODE -ne 0) { throw 'Publishing Vaultix.Service failed.' }
    & dotnet publish $appProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $appPath
    if ($LASTEXITCODE -ne 0) { throw 'Publishing Vaultix.App failed.' }

    $serviceExecutable = Join-Path $servicePath 'Vaultix.Service.exe'
    if (-not (Test-Path -LiteralPath $serviceExecutable)) { throw "Service executable was not published: $serviceExecutable" }
    if (-not $existingService) {
        & sc.exe create $serviceName "binPath= `"$serviceExecutable`"" 'start= auto' 'DisplayName= Vaultix Service'
        if ($LASTEXITCODE -ne 0) { throw 'Could not register Vaultix Service.' }
    }
    else {
        & sc.exe config $serviceName "binPath= `"$serviceExecutable`"" 'start= auto'
        if ($LASTEXITCODE -ne 0) { throw 'Could not update Vaultix Service.' }
    }
    & sc.exe description $serviceName 'Vaultix continuous backup, snapshot and recovery service'
    & sc.exe failure $serviceName 'reset= 86400' 'actions= restart/5000/restart/15000/restart/30000'

    $startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $startMenu 'Vaultix.lnk'))
    $shortcut.TargetPath = Join-Path $appPath 'Vaultix.App.exe'
    $shortcut.WorkingDirectory = $appPath
    $shortcut.Description = 'Vaultix Backup Dashboard'
    $shortcut.Save()
    Start-Service -Name $serviceName
    Write-Host "Vaultix installed. Open it from the Start menu; data is stored in $env:ProgramData\Vaultix." -ForegroundColor Green
}
catch {
    if ($wasRunning -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status -ne 'Running') { Start-Service -Name $serviceName -ErrorAction SilentlyContinue }
    throw
}
