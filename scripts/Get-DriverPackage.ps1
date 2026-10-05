[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$SevenZipPath = '7z.exe',
    [string]$InstallerPath
)
$ErrorActionPreference = 'Stop'
$taskCommit = 'dca5e709afa498433777b36d3608a088231917ce'
$taskHash = 'AE0DD19472F92BAB3165D370C3713616A3FFB708FAD785684759B04653705A71'
$taskDestination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskDestination) { throw 'OutputDirectory must not exist; choose a new directory.' }
if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { throw 'Upstream does not provide ARM64 drivers.' }
$taskMachine = if ([Environment]::Is64BitOperatingSystem) { 0x8664 } else { 0x014c }
$taskSevenZip = (Get-Command $SevenZipPath -ErrorAction Stop).Source
$taskScratch = Join-Path ([IO.Path]::GetTempPath()) ('Com0ComSharp-extract-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskScratch) | Out-Null
$taskScratchResolved = [IO.Path]::GetFullPath($taskScratch)
$taskArchive = if ($InstallerPath) { (Resolve-Path -LiteralPath $InstallerPath).Path } else { Join-Path $taskScratch 'installer.exe' }

function Get-PeMachine([string]$Path) {
    $taskReader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($taskReader.BaseStream.Length -lt 64 -or $taskReader.ReadUInt16() -ne 0x5A4D) { throw "Invalid PE: $Path" }
        $taskReader.BaseStream.Position = 60
        $taskOffset = $taskReader.ReadInt32()
        if ($taskOffset -lt 64 -or $taskOffset -gt $taskReader.BaseStream.Length - 6) { throw "Invalid PE header: $Path" }
        $taskReader.BaseStream.Position = $taskOffset
        if ($taskReader.ReadUInt32() -ne 0x00004550) { throw "Invalid PE signature: $Path" }
        return $taskReader.ReadUInt16()
    } finally { $taskReader.Dispose() }
}

try {
    if (-not $InstallerPath) {
        $taskUrl = "https://raw.githubusercontent.com/vovsoft/com0com/$taskCommit/com0com%20v.3.0.0%20setup%2032%2B64-bit%20signed.exe"
        Invoke-WebRequest -Uri $taskUrl -OutFile $taskArchive -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash -ne $taskHash) { throw 'Installer SHA-256 mismatch; refusing extraction.' }
    $taskRaw = Join-Path $taskScratch 'raw'
    & $taskSevenZip x $taskArchive "-o$taskRaw" -aou -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip extraction failed: $LASTEXITCODE" }
    $taskFiles = Get-ChildItem -LiteralPath $taskRaw -File
    $taskSelections = @{}
    foreach ($taskName in @('com0com.sys', 'setupc.exe', 'setup.dll')) {
        $taskBase = [IO.Path]::GetFileNameWithoutExtension($taskName)
        $taskExtension = [IO.Path]::GetExtension($taskName)
        $taskPattern = '^' + [regex]::Escape($taskBase) + '(_\d+)?' + [regex]::Escape($taskExtension) + '$'
        $taskMatches = @($taskFiles | Where-Object { $_.Name -match $taskPattern -and (Get-PeMachine $_.FullName) -eq $taskMachine })
        if ($taskMatches.Count -ne 1) { throw "Expected one matching-architecture $taskName; found $($taskMatches.Count)." }
        $taskSelections[$taskName] = $taskMatches[0].FullName
    }
    $taskSuffix = [IO.Path]::GetFileNameWithoutExtension($taskSelections['com0com.sys']).Substring('com0com'.Length)
    $taskSelections['com0com.cat'] = Join-Path $taskRaw ("com0com$taskSuffix.cat")
    foreach ($taskName in @('com0com.inf', 'cncport.inf', 'comport.inf')) { $taskSelections[$taskName] = Join-Path $taskRaw $taskName }
    foreach ($taskPath in $taskSelections.Values) { if (-not (Test-Path -LiteralPath $taskPath -PathType Leaf)) { throw "Missing $taskPath" } }
    [IO.Directory]::CreateDirectory($taskDestination) | Out-Null
    foreach ($taskName in $taskSelections.Keys) { Copy-Item -LiteralPath $taskSelections[$taskName] -Destination (Join-Path $taskDestination $taskName) }
    Write-Output "Extracted matching driver package: $taskDestination"
    Write-Output 'Legacy signed package: Windows 11 load acceptance remains conditional. No installer was executed.'
} finally {
    # Delete only the exact task-created scratch directory after checking the resolved boundary.
    $taskCurrent = [IO.Path]::GetFullPath($taskScratch)
    $taskTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($taskCurrent -eq $taskScratchResolved -and $taskCurrent.StartsWith($taskTempRoot, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($taskCurrent).StartsWith('Com0ComSharp-extract-')) {
        Remove-Item -LiteralPath $taskCurrent -Recurse -Force
    } else { throw 'Scratch cleanup boundary check failed.' }
}
