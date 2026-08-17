param(
    [Parameter(Mandatory = $true)]
    [string] $PublishedServicePath
)

$ErrorActionPreference = 'Stop'
$serviceExecutable = [System.IO.Path]::GetFullPath($PublishedServicePath)
if (-not [System.IO.File]::Exists($serviceExecutable)) {
    throw "Vaultix.Service executable not found: $serviceExecutable"
}
if ([System.IO.Path]::GetFileName($serviceExecutable) -ne 'Vaultix.Service.exe') {
    throw 'PublishedServicePath must point to Vaultix.Service.exe.'
}

& sc.exe create 'Vaultix.Service' "binPath= `"$serviceExecutable`"" 'start= auto' 'DisplayName= Vaultix Service'
if ($LASTEXITCODE -ne 0) { throw 'Could not register Vaultix Service.' }
& sc.exe description 'Vaultix.Service' 'Vaultix continuous backup, snapshot and recovery service'
& sc.exe failure 'Vaultix.Service' 'reset= 86400' 'actions= restart/5000/restart/15000/restart/30000'
& sc.exe start 'Vaultix.Service'
