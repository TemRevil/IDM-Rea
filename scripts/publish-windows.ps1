$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
 dotnet test tests/IDMRea.Core.Tests -c Release
 if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
 dotnet publish src/IDMRea.App -c Release -r win-x64 -p:Platform=x64 --self-contained true -o artifacts/windows
 if ($LASTEXITCODE -ne 0) { throw 'UI publish failed' }
 if (!(Test-Path artifacts/windows/resources.pri)) { throw 'Published WinUI resource index is missing.' }
 Get-ChildItem artifacts/windows -Recurse -File -Include *.pri,*.xbf | Select-Object FullName,Length | Format-Table
 dotnet publish src/IDMRea.Engine -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained true -o artifacts/windows/engine
 if ($LASTEXITCODE -ne 0) { throw 'Engine publish failed' }
 # The browser starts the native host from the engine folder, and the host starts the engine beside it.
 dotnet publish src/IDMRea.BrowserHost -c Release -r win-x64 --self-contained true -o artifacts/windows/engine
 if ($LASTEXITCODE -ne 0) { throw 'Browser host publish failed' }
 # The unpacked extension that Settings, Browser, "Add to browser" points the browser at.
 $extension = 'artifacts/windows/extension'
 if (Test-Path $extension) { Remove-Item $extension -Recurse -Force }
 New-Item -ItemType Directory $extension | Out-Null
 Copy-Item src/IDMRea.Extension/* $extension -Recurse -Exclude test,package.json
 if (!(Test-Path "$extension/manifest.json") -or (Test-Path "$extension/test")) { throw 'Extension copy is wrong.' }
} finally { Pop-Location }
