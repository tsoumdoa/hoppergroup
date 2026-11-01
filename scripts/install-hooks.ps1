$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    git config core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure core.hooksPath.' }
    Write-Output 'Installed local hooks: core.hooksPath=.githooks'
}
finally {
    Pop-Location
}
