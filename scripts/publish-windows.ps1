$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
 dotnet test tests/IDMRea.Core.Tests -c Release
 if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
 dotnet publish src/IDMRea.App -c Release -r win-x64 -p:Platform=x64 --self-contained true -o artifacts/windows
 if ($LASTEXITCODE -ne 0) { throw 'UI publish failed' }
 if (!(Test-Path artifacts/windows/resources.pri)) { throw 'Published WinUI resource index is missing.' }
 Get-ChildItem artifacts/windows -Recurse -File -Include *.pri,*.xbf | Select-Object FullName,Length | Format-Table
 dotnet publish src/IDMRea.Engine -c Release -r win-x64 --self-contained true -o artifacts/windows/engine
 if ($LASTEXITCODE -ne 0) { throw 'Engine publish failed' }
} finally { Pop-Location }
