# Drives the real published WinUI app against localhost fixtures, checks the UI through UI Automation and saves screenshots.
# Fails if no usable desktop or window exists. Works on Windows PowerShell 5.1 and PowerShell 7.
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
$env:IDMREA_DATA_DIRECTORY = Join-Path $root 'artifacts\runtime-data'
$folder = Join-Path $root 'artifacts\test-downloads'
$captures = Join-Path $root 'artifacts\screenshots'
New-Item -ItemType Directory -Force $folder,$captures | Out-Null
$sha = [Security.Cryptography.SHA256]::Create()
$hash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($env:IDMREA_DATA_DIRECTORY))))).Replace('-','')
$pipeName = 'IDMRea.' + $hash.Substring(0,20)
$auto = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]::Descendants

function Send-Engine($request, $wait = 15000) {
 $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
 try {
  $pipe.Connect($wait)
  $writer = New-Object IO.StreamWriter($pipe,(New-Object Text.UTF8Encoding($false)),4096,$true)
  $reader = New-Object IO.StreamReader($pipe)
  $writer.AutoFlush = $true
  $writer.WriteLine(($request | ConvertTo-Json -Compress -Depth 5))
  $response = $reader.ReadLine() | ConvertFrom-Json
  if (!$response.ok) { throw $response.error }
  return $response
 } finally { $pipe.Dispose() }
}
function Wait-For($check, $description, $seconds = 30) {
 $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
 do { if (& $check) { return }; Start-Sleep -Milliseconds 250 } while ([DateTime]::UtcNow -lt $deadline)
 throw "Timed out waiting for $description"
}
function Window($app) { $app.Refresh(); return $auto::FromHandle($app.MainWindowHandle) }
function Find-Named($app, $name) {
 return (Window $app).FindFirst($scope,(New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty,$name)))
}
function Capture($app, $name) {
 $app.Refresh(); $handle = $app.MainWindowHandle
 if ($handle -eq 0) { throw 'No WinUI window is available on this desktop.' }
 [DesktopCapture]::SetForegroundWindow($handle) | Out-Null
 Start-Sleep -Seconds 2
 $rect = New-Object DesktopCapture+Rect
 if (![DesktopCapture]::GetWindowRect($handle,[ref]$rect)) { throw 'Window bounds unavailable.' }
 $bitmap = New-Object Drawing.Bitmap(($rect.Right-$rect.Left),($rect.Bottom-$rect.Top))
 $graphics = [Drawing.Graphics]::FromImage($bitmap)
 $dc = $graphics.GetHdc()
 try { if (![DesktopCapture]::PrintWindow($handle,$dc,2)) { throw 'PrintWindow failed.' } }
 finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
 try {
  $colors = New-Object 'Collections.Generic.HashSet[int]'
  for ($x=0; $x -lt $bitmap.Width; $x+=7) { for ($y=0; $y -lt $bitmap.Height; $y+=7) { [void]$colors.Add($bitmap.GetPixel($x,$y).ToArgb()) } }
  if ($colors.Count -lt 20) { throw 'Capture appears blank; desktop rendering unavailable.' }
  $bitmap.Save((Join-Path $captures $name),[Drawing.Imaging.ImageFormat]::Png)
 } finally { $bitmap.Dispose() }
}
function Set-Theme($theme) {
 $settings = (Send-Engine @{command='getSettings'; version=2}).settings
 $settings.theme = $theme
 Send-Engine @{command='setSettings'; version=2; settings=$settings} | Out-Null
}

# Shell: every navigation item and the title-bar search must exist.
function Test-Shell($app) {
 $names = 'All downloads','Downloading','Queued and paused','Completed','Failed','Video','Music','Documents','Programs','Archives','Images','Other','Settings','Search downloads'
 $missing = @($names | Where-Object { -not (Find-Named $app $_) })
 if ($missing.Count -gt 0) { throw "Shell is missing: $($missing -join ', ')" }
}

# Downloads list: the list item holding a file name, and its Pause/Resume toggle.
function Row($app, $filename) {
 $text = Find-Named $app $filename
 while ($text -and $text.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) { $text = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($text) }
 return $text
}
function Toggle($app, $filename) {
 $row = Row $app $filename
 if (!$row) { return 'missing' }
 foreach ($name in 'Pause','Resume') {
  $button = $row.FindFirst($scope,(New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty,$name)))
  if ($button -and $button.Current.IsEnabled) { return $name }
 }
 return 'none'
}
function Select-Nav($app, $name) {
 $item = Find-Named $app $name
 $pattern = $null
 if ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select() }
 else { $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
}
function Listed($app) {
 $list = Find-Named $app 'Downloads list'
 $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)))
 return @($items | ForEach-Object { $_.Current.Name })
}
function Test-Downloads($app) {
 Send-Engine @{command='add'; version=2; url='http://127.0.0.1:18764/complete.bin'; folder=$folder} | Out-Null
 Send-Engine @{command='add'; version=2; url='http://127.0.0.1:18764/paused.bin'; folder=$folder; startPaused=$true} | Out-Null
 Send-Engine @{command='add'; version=2; url='http://127.0.0.1:18764/active.bin'; folder=$folder} | Out-Null
 Wait-For { (Toggle $app 'complete.bin') -eq 'none' -and (Send-Engine @{command='list'}).jobs.status -contains 'Completed' } 'complete.bin with no enabled toggle'
 Wait-For { (Toggle $app 'paused.bin') -eq 'Resume' } 'paused.bin offering Resume'
 Wait-For { (Toggle $app 'active.bin') -eq 'Pause' } 'active.bin offering Pause'
 (Row $app 'active.bin').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Wait-For { $pane = Find-Named $app 'Download details'; $pane -and $pane.FindFirst($scope,(New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty,'Copy link'))) -and $pane.FindFirst($scope,(New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty,'Show in folder'))) } 'the details pane with Copy link and Show in folder'
 Capture $app '02-downloads-details.png'
 Select-Nav $app 'Completed'
 try { Wait-For { $names = @(Listed $app); $names.Count -eq 1 -and $names[0] -eq 'complete.bin' } 'Completed showing only complete.bin' }
 catch { throw "$_ (listed: $((Listed $app) -join ', '))" }
 Capture $app '03-completed.png'
 Select-Nav $app 'All downloads'
}

# Add dialog: a link in the clipboard, Ctrl+N, the probed name and category, Start download.
function Test-AddDialog($app) {
 Add-Type -AssemblyName System.Windows.Forms
 $text = 'http://127.0.0.1:18764/named'
 Set-Clipboard -Value $text
 [DesktopCapture]::SetForegroundWindow($app.MainWindowHandle) | Out-Null
 Start-Sleep -Milliseconds 500
 [System.Windows.Forms.SendKeys]::SendWait('^n')
 try { Wait-For { $field = Find-Named $app 'File name'; $field -and $field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'report.pdf' } 'File name report.pdf' }
 catch { Capture $app 'failed-add-dialog.png'; $field = Find-Named $app 'File name'; $value = if ($field) { $field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } else { 'no field' }; throw "$_ (File name: $value)" }
 Wait-For { $texts = (Window $app).FindAll($scope,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text))); @($texts | Where-Object { $_.Current.Name -like '*Documents*' }).Count -gt 0 } 'the Documents line'
 Capture $app '04-add-dialog.png'
 $start = Find-Named $app 'Start download'
 Wait-For { $start.Current.IsEnabled } 'Start download enabled'
 $start.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-For { @((Send-Engine @{command='list'}).jobs | Where-Object { $_.destination -like '*report.pdf' -and $_.status -eq 'Completed' -and $_.category -eq 'Documents' }).Count -eq 1 } 'report.pdf completed under Documents'
}

# Start from an empty list: stop any engine on the test data folder and clear it.
function Stop-Engine {
 try { Send-Engine @{command='shutdown'} 500 | Out-Null } catch { return }
 Wait-For { try { Send-Engine @{command='list'} 300 | Out-Null; $false } catch { $true } } 'the engine to stop' 20
 Start-Sleep -Milliseconds 500
}
Stop-Engine
Wait-For { Remove-Item -Recurse -Force $env:IDMREA_DATA_DIRECTORY,$folder -ErrorAction SilentlyContinue; !(Test-Path $env:IDMREA_DATA_DIRECTORY) -and !(Test-Path $folder) } 'the test data folder to clear' 15
New-Item -ItemType Directory -Force $folder | Out-Null

$server = Start-Process python -ArgumentList (Join-Path $PSScriptRoot 'fixture-server.py') -PassThru
$app = $null
try {
 $app = Start-Process (Join-Path $root 'artifacts\windows\IDMRea.App.exe') -PassThru
 Wait-For { $app.Refresh(); if ($app.HasExited) { throw "WinUI process exited with code $($app.ExitCode)" }; $app.MainWindowHandle -ne 0 } 'the WinUI window'
 try { Wait-For { try { Test-Shell $app; $true } catch { $false } } 'the app shell' 15 } catch { }
 Test-Shell $app
 Set-Theme 'Dark'; Capture $app '01-shell-dark.png'
 Set-Theme 'Light'; Capture $app '01-shell-light.png'
 Set-Theme 'System'
 Test-Downloads $app
 Test-AddDialog $app

 # Closing the window must leave the engine downloading.
 Wait-For { @((Send-Engine @{command='list'}).jobs | Where-Object { $_.status -eq 'Downloading' }).Count -gt 0 } 'an active transfer'
 [void]$app.CloseMainWindow(); $app.WaitForExit(10000) | Out-Null
 $before = (Send-Engine @{command='list'}).jobs | Where-Object { $_.status -eq 'Downloading' } | Select-Object -First 1
 Start-Sleep -Seconds 1
 $after = (Send-Engine @{command='list'}).jobs | Where-Object { $_.id -eq $before.id }
 if ($after.receivedBytes -le $before.receivedBytes) { throw 'Transfer did not continue after closing the UI.' }
 $app = Start-Process (Join-Path $root 'artifacts\windows\IDMRea.App.exe') -PassThru
 Wait-For { $app.Refresh(); $app.MainWindowHandle -ne 0 } 'reopened UI'
 Send-Engine @{command='cancel'; id=$before.id} | Out-Null
 (Send-Engine @{command='list'}) | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $captures 'runtime-verification.json')
 Send-Engine @{command='shutdown'} | Out-Null
 'Real WinUI captures using controlled localhost fixtures.' | Set-Content (Join-Path $captures 'README.txt')
 'Capture checks passed.'
} catch {
 $failure = $_
 "Interactive=$([Environment]::UserInteractive); capture session=$([Diagnostics.Process]::GetCurrentProcess().SessionId)" | Tee-Object -FilePath (Join-Path $captures 'diagnostics.txt')
 if ($app) { $app.Refresh(); $state = if ($app.HasExited) { "App exited with code $($app.ExitCode)" } else { "App session=$($app.SessionId); window=$($app.MainWindowHandle)" }; $state | Tee-Object -FilePath (Join-Path $captures 'diagnostics.txt') -Append }
 $startup = Join-Path $env:IDMREA_DATA_DIRECTORY 'app-startup.log'
 if (Test-Path $startup) { Get-Content $startup -Tail 40; Copy-Item $startup $captures }
 throw $failure
} finally {
 if ($app -and !$app.HasExited) { Stop-Process -Id $app.Id -Force }
 Stop-Engine
 if (!$server.HasExited) { Stop-Process -Id $server.Id -Force }
}
