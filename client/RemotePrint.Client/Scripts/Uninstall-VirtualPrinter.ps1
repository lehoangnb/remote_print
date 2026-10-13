param(
    [Parameter(Mandatory = $true)]
    [string]$SpoolFile
)

$ErrorActionPreference = 'Stop'
$printerName = 'Remote Print'

if (Get-Printer -Name $printerName -ErrorAction SilentlyContinue) {
    Remove-Printer -Name $printerName
}
if (Get-PrinterPort -Name $SpoolFile -ErrorAction SilentlyContinue) {
    Remove-PrinterPort -Name $SpoolFile
}

