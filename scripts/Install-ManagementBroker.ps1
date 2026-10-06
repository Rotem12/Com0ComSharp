param(
    [Parameter(Mandatory = $true)] [string] $DriverPackageDirectory,
    [Parameter(Mandatory = $true)] [string] $ToolPath,
    [Parameter(Mandatory = $true)] [string] $BrokerPath
)

$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this setup script once with administrator approval. It installs the driver and privileged broker service.'
}

$source = (Resolve-Path -LiteralPath $DriverPackageDirectory).Path
$tool = (Resolve-Path -LiteralPath $ToolPath).Path
$broker = (Resolve-Path -LiteralPath $BrokerPath).Path
foreach ($name in @('setupc.exe', 'setup.dll', 'com0com.sys', 'com0com.cat', 'com0com.inf', 'cncport.inf', 'comport.inf')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf)) { throw "Driver package is missing $name" }
}

# Stage the signed package using the same validation path as the C# API.
& $tool install-driver --package $source --allow-legacy --require-admin
if ($LASTEXITCODE -ne 0) { throw "Driver installation failed with exit code $LASTEXITCODE" }

$root = Join-Path $env:ProgramFiles 'Com0ComSharp'
$securePackage = Join-Path $root 'Driver'
$secureBroker = Join-Path $root 'Com0ComSharp.Broker.exe'
$existing = Get-Service -Name 'Com0ComSharpBroker' -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name 'Com0ComSharpBroker' -Force }
    & sc.exe delete Com0ComSharpBroker | Out-Null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Service -Name 'Com0ComSharpBroker' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (Get-Service -Name 'Com0ComSharpBroker' -ErrorAction SilentlyContinue) { throw 'The previous broker service is still being removed. Retry setup shortly.' }
}
New-Item -ItemType Directory -Path $securePackage -Force | Out-Null
foreach ($name in @('setupc.exe', 'setup.dll', 'com0com.sys', 'com0com.cat', 'com0com.inf', 'cncport.inf', 'comport.inf')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $securePackage $name) -Force
}
Copy-Item -LiteralPath $broker -Destination $secureBroker -Force

$binaryPath = '"{0}" --service "{1}"' -f $secureBroker, $securePackage
& sc.exe create Com0ComSharpBroker binPath= $binaryPath start= auto obj= LocalSystem DisplayName= 'Com0ComSharp Pair Management Broker'
if ($LASTEXITCODE -ne 0) { throw "Windows could not register the broker service (sc.exe exit $LASTEXITCODE)." }
& sc.exe description Com0ComSharpBroker 'Performs allowlisted com0com pair creation and removal for local applications.' | Out-Null
Start-Service -Name 'Com0ComSharpBroker'
Write-Host "Installed driver and started Com0ComSharpBroker using the protected package at $securePackage"
