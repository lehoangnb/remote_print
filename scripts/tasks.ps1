<#
.SYNOPSIS
    Tac vu nhanh cho Remote Print: build exe client hoac chay server dev.

.DESCRIPTION
    -Task Exe: publish client WinForms + service thanh exe framework-dependent (win-x64/win-arm64)
    qua scripts\publish-client.ps1. Output nam trong dist\.
    -Task ServerDev: chay server Node o che do watch (node --watch src/index.js),
    tu dong npm install neu thieu node_modules. Nhan Ctrl+C de dung.

.EXAMPLE
    .\scripts\tasks.ps1 -Task ServerDev
    Chay server dev voi PORT trong server\.env.

.EXAMPLE
    .\scripts\tasks.ps1 -Task ServerDev -Port 7444
    Chay server dev voi PORT 7444 (ghi de server\.env).

.EXAMPLE
    .\scripts\tasks.ps1 -Task Exe
    Build exe framework-dependent win-x64.

.EXAMPLE
    .\scripts\tasks.ps1 -Task Exe -Runtime win-arm64
    Build exe cho Windows ARM64.
#>
param(
    [Parameter(Position = 0)]
    [ValidateSet('Exe', 'ServerDev')]
    [string]$Task = 'ServerDev',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [ValidateRange(0, 65535)]
    [int]$Port = 0,

    [switch]$SkipInstall
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$serverDir = Join-Path $projectRoot 'server'

function Assert-Command {
    param([string]$Name, [string]$InstallHint)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Khong tim thay '$Name'. $InstallHint"
    }
}

function Invoke-ServerDev {
    Assert-Command 'node' 'Cai Node.js 20+ tu https://nodejs.org roi chay lai.'
    Assert-Command 'npm' 'Cai Node.js 20+ tu https://nodejs.org roi chay lai.'

    $nodeMajor = (& node -v) -replace '^v(\d+)\..*', '$1'
    if ([int]$nodeMajor -lt 20) {
        throw "Server can Node.js 20+, hien tai la $(& node -v)."
    }

    if ((-not $SkipInstall) -and (-not (Test-Path -LiteralPath (Join-Path $serverDir 'node_modules')))) {
        Write-Host 'Thieu server\node_modules, chay npm install...'
        Push-Location -LiteralPath $serverDir
        try {
            npm install
            if ($LASTEXITCODE -ne 0) { throw 'npm install failed.' }
        }
        finally {
            Pop-Location
        }
    }

    if ($Port -gt 0) {
        $env:PORT = "$Port"
        Write-Host "Dung PORT=$Port (ghi de server\\.env)."
    }

    Write-Host 'Chay server dev (Ctrl+C de dung)...'
    Push-Location -LiteralPath $serverDir
    try {
        npm run dev
        if ($LASTEXITCODE -ne 0) { throw 'Server dev exited with errors.' }
    }
    finally {
        Pop-Location
    }
}

function Invoke-Exe {
    Assert-Command 'dotnet' 'Cai .NET 8 SDK tu https://dotnet.microsoft.com roi chay lai.'
    & (Join-Path $PSScriptRoot 'publish-client.ps1') -Runtime $Runtime
    if ($LASTEXITCODE -ne 0) { throw 'Build exe failed.' }
}

switch ($Task) {
    'Exe' { Invoke-Exe }
    'ServerDev' { Invoke-ServerDev }
}
