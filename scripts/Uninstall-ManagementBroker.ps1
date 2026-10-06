param([Parameter(Mandatory = $true)] [string] $ToolPath)

$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script with administrator approval to remove the driver and management service.'
}

$tool = (Resolve-Path -LiteralPath $ToolPath).Path
$root = Join-Path $env:ProgramFiles 'Com0ComSharp'
$package = Join-Path $root 'Driver'
if (Test-Path -LiteralPath $package -PathType Container) {
    & $tool uninstall-driver --package $package --all-pairs --allow-legacy --require-admin
    if ($LASTEXITCODE -ne 0) { throw "Driver uninstall failed with exit code $LASTEXITCODE" }
}

$service = Get-Service -Name 'Com0ComSharpBroker' -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name 'Com0ComSharpBroker' -Force }
    & sc.exe delete Com0ComSharpBroker | Out-Null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Service -Name 'Com0ComSharpBroker' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (Get-Service -Name 'Com0ComSharpBroker' -ErrorAction SilentlyContinue) { throw 'The broker service is still being removed. Retry shortly.' }
}

$resolvedRoot = [System.IO.Path]::GetFullPath($root) + [System.IO.Path]::DirectorySeparatorChar
$programFiles = [System.IO.Path]::GetFullPath($env:ProgramFiles)
if (-not $resolvedRoot.StartsWith($programFiles, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to remove a path outside Program Files.' }
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
Write-Host 'Removed the com0com driver and Com0ComSharp broker.'
