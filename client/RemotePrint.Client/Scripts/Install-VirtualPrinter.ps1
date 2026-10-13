param(
    [Parameter(Mandatory = $true)]
    [string]$SpoolFile
)

$ErrorActionPreference = 'Stop'
$printerName = 'Remote Print'
$driverName = 'Microsoft Print To PDF'
$spoolDirectory = Split-Path -Parent $SpoolFile

New-Item -ItemType Directory -Force -Path $spoolDirectory | Out-Null
& icacls.exe $spoolDirectory /grant '*S-1-5-32-545:(OI)(CI)M' /T /C | Out-Null

if (-not (Get-PrinterDriver -Name $driverName -ErrorAction SilentlyContinue)) {
    throw "Không tìm thấy driver '$driverName'. Hãy bật Microsoft Print to PDF trong Windows Features."
}

$existing = Get-Printer -Name $printerName -ErrorAction SilentlyContinue
if ($existing -and $existing.PortName -ne $SpoolFile) {
    Remove-Printer -Name $printerName
    $existing = $null
}

if (-not (Get-PrinterPort -Name $SpoolFile -ErrorAction SilentlyContinue)) {
    Add-PrinterPort -Name $SpoolFile
}

if (-not $existing) {
    Add-Printer -Name $printerName -DriverName $driverName -PortName $SpoolFile
}

Set-Printer -Name $printerName -Comment 'Send print jobs to another Remote Print client'

