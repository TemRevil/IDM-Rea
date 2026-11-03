# Captures the real published WinUI window; fails if no usable desktop/window exists.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DesktopCapture {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd,IntPtr dc,uint flags);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
}
'@
$root = Split-Path $PSScriptRoot
$env:IDMREA_DATA_DIRECTORY = Join-Path $root 'artifacts/runtime-data'
$folder = Join-Path $root 'artifacts/test-downloads'
$captures = Join-Path $root 'artifacts/screenshots'
New-Item -ItemType Directory -Force $folder,$captures | Out-Null
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($env:IDMREA_DATA_DIRECTORY)))
$pipeName = 'IDMRea.' + $hash.Substring(0,20)
function Send-Engine($request) {
 $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
 try {
  $pipe.Connect(15000)
  $writer = [IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),4096,$true)
  $reader = [IO.StreamReader]::new($pipe)
  $writer.AutoFlush = $true
  $writer.WriteLine(($request | ConvertTo-Json -Compress))
  $response = $reader.ReadLine() | ConvertFrom-Json
  if (!$response.ok) { throw $response.error }
  return $response
 } finally { $pipe.Dispose() }
}
function Wait-For($check, $description) {
 $deadline = [DateTime]::UtcNow.AddSeconds(30)
 do { if (& $check) { return }; Start-Sleep -Milliseconds 250 } while ([DateTime]::UtcNow -lt $deadline)
 throw "Timed out waiting for $description"
}
function Capture($app, $name) {
 $app.Refresh(); $handle = $app.MainWindowHandle
 if ($handle -eq 0) { throw 'No WinUI window is available on this desktop.' }
 [DesktopCapture]::SetForegroundWindow($handle) | Out-Null
 Start-Sleep -Seconds 2
 $rect = [DesktopCapture+Rect]::new()
 if (![DesktopCapture]::GetWindowRect($handle,[ref]$rect)) { throw 'Window bounds unavailable.' }
 $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
 $graphics = [Drawing.Graphics]::FromImage($bitmap)
 $dc = $graphics.GetHdc()
 try { if (![DesktopCapture]::PrintWindow($handle,$dc,2)) { throw 'PrintWindow failed.' } }
 finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
 try {
  $colors = [Collections.Generic.HashSet[int]]::new()
  for ($x=0; $x -lt $bitmap.Width; $x+=7) { for ($y=0; $y -lt $bitmap.Height; $y+=7) { [void]$colors.Add($bitmap.GetPixel($x,$y).ToArgb()) } }
  if ($colors.Count -lt 20) { throw 'Capture appears blank; desktop rendering unavailable.' }
  $bitmap.Save((Join-Path $captures $name),[Drawing.Imaging.ImageFormat]::Png)
 } finally { $bitmap.Dispose() }
}
function Add-ThroughUI($app,$url) {
 $element = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
 $scope = [System.Windows.Automation.TreeScope]::Descendants
 $urlField = $element.FindFirst($scope,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Download URL'))
 $urlField.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($url)
 $button = $element.FindFirst($scope,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Start download'))
 Wait-For { $button.Current.IsEnabled } 'the ready Start download button'
 $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Test-RowControls($app,$filename,$pause,$resume,$cancel) {
 $element = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
 $scope = [System.Windows.Automation.TreeScope]::Descendants
 $row = $element.FindFirst($scope,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$filename))
 if (!$row) { return $false }
 while ($row -and $row.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) {
  $row = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($row)
 }
 if (!$row) { return $false }
 $expected = @{Pause=$pause;Resume=$resume;Cancel=$cancel}
 foreach ($name in $expected.Keys) {
  $button = $row.FindFirst($scope,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))
  if (!$button -or $button.Current.IsEnabled -ne $expected[$name]) { return $false }
 }
 return $true
}
$server = Start-Process python -ArgumentList (Join-Path $PSScriptRoot 'fixture-server.py') -PassThru
$app = $null
try {
 $app = Start-Process (Join-Path $root 'artifacts/windows/IDMRea.App.exe') -PassThru
 Wait-For { $app.Refresh(); if ($app.HasExited) { throw "WinUI process exited with code $($app.ExitCode)" }; $app.MainWindowHandle -ne 0 } 'the WinUI window'
 $element = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
 $folderField = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Save to'))
 $folderField.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($folder)
 Capture $app '01-empty-queue.png'
 Add-ThroughUI $app 'http://127.0.0.1:18764/complete.bin'
 Wait-For { (Send-Engine @{command='list'}).jobs.status -contains 'Completed' } 'completed fixture'
 Wait-For { Test-RowControls $app 'complete.bin' $false $false $false } 'completed row controls'
 Add-ThroughUI $app 'http://127.0.0.1:18764/paused.bin'
 Wait-For { (Send-Engine @{command='list'}).jobs.status -contains 'Downloading' } 'active transfer'
 $paused = (Send-Engine @{command='list'}).jobs | Where-Object status -eq 'Downloading' | Select-Object -First 1
 Send-Engine @{command='pause'; id=$paused.id} | Out-Null
 Send-Engine @{command='resume'; id=$paused.id} | Out-Null
 Send-Engine @{command='pause'; id=$paused.id} | Out-Null
 Add-ThroughUI $app 'http://127.0.0.1:18764/active.bin'
 Wait-For { (Send-Engine @{command='list'}).jobs.status -contains 'Downloading' } 'second active transfer'
 Wait-For { Test-RowControls $app 'paused.bin' $false $true $true } 'paused row controls'
 Wait-For { Test-RowControls $app 'active.bin' $true $false $true } 'active row controls'
 Capture $app '02-test-downloads.png'
 # Closing and reopening the actual UI must preserve engine ownership and jobs.
 [void]$app.CloseMainWindow(); $app.WaitForExit(10000) | Out-Null
 $before = (Send-Engine @{command='list'}).jobs | Where-Object status -eq 'Downloading' | Select-Object -First 1
 Start-Sleep -Seconds 1
 $after = (Send-Engine @{command='list'}).jobs | Where-Object id -eq $before.id
 if ($after.receivedBytes -le $before.receivedBytes) { throw 'Transfer did not continue after closing the UI.' }
 $app = Start-Process (Join-Path $root 'artifacts/windows/IDMRea.App.exe') -PassThru
 Wait-For { $app.Refresh(); $app.MainWindowHandle -ne 0 } 'reopened UI'
 Send-Engine @{command='cancel'; id=$before.id} | Out-Null
 (Send-Engine @{command='list'}) | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $captures 'runtime-verification.json')
 Send-Engine @{command='shutdown'} | Out-Null
 'Real WinUI captures using controlled localhost fixtures.' | Set-Content (Join-Path $captures 'README.txt')
} catch {
 $failure = $_
 "Interactive=$([Environment]::UserInteractive); capture session=$([Diagnostics.Process]::GetCurrentProcess().SessionId)" | Tee-Object -FilePath (Join-Path $captures 'diagnostics.txt')
 if ($app) { $app.Refresh(); $state = if ($app.HasExited) { "App exited with code $($app.ExitCode)" } else { "App session=$($app.SessionId); window=$($app.MainWindowHandle)" }; $state | Tee-Object -FilePath (Join-Path $captures 'diagnostics.txt') -Append }
 $startup = Join-Path $env:IDMREA_DATA_DIRECTORY 'app-startup.log'
 if (Test-Path $startup) { Get-Content $startup; Copy-Item $startup $captures }
 Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=(Get-Date).AddMinutes(-5)} -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -in '.NET Runtime','Application Error','Windows Error Reporting' } | Select-Object TimeCreated,ProviderName,Message | Format-List | Out-String | Tee-Object -FilePath (Join-Path $captures 'events.txt')
 throw $failure
} finally {
 if ($app -and !$app.HasExited) { Stop-Process -Id $app.Id -Force }
 if (!$server.HasExited) { Stop-Process -Id $server.Id -Force }
}
