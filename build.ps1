param([string]$Destination = (Join-Path $PSScriptRoot 'dist\PasteLoop.exe'))
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework compiler was not found. Enable .NET Framework 4.8 in Windows.' }
$Destination = [System.IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
$source = Join-Path $PSScriptRoot 'src'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output "/out:$Destination" "/win32manifest:$source\app.manifest" "/resource:$source\MainWindow.xaml,PasteLoop.MainWindow.xaml" /reference:System.dll /reference:System.Core.dll /reference:System.Xaml.dll /reference:System.Windows.Forms.dll "/reference:$framework\WPF\WindowsBase.dll" "/reference:$framework\WPF\PresentationCore.dll" "/reference:$framework\WPF\PresentationFramework.dll" "$source\PasteLoop.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built: $Destination"