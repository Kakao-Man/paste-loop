param(
    [string]$Executable = (Join-Path $PSScriptRoot 'dist\PasteLoop.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '.checks')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Executable)) { throw 'Build the app first with .\build.ps1.' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$run = Start-Process -FilePath $Executable -ArgumentList '--verify', ('"' + $OutputDirectory + '"') -WindowStyle Hidden -Wait -PassThru
$report = Join-Path $OutputDirectory 'verify.txt'
if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
if ($run.ExitCode -ne 0) { throw "Verification failed (exit code $($run.ExitCode))." }