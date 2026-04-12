$ErrorActionPreference = 'Stop'

$output = Join-Path $PSScriptRoot 'deploy' 'release'

if (Test-Path $output) { Remove-Item $output -Recurse -Force }

dotnet publish $PSScriptRoot\MacKeys.csproj -c Release -r win-x64 -o $output

Write-Host "`nPublished to: $output" -ForegroundColor Green
