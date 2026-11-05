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
# Its own data folder, wiped at the start: an IDM-Rea already running on another folder is left alone.
$env:IDMREA_DATA_DIRECTORY = Join-Path $root 'artifacts\capture-data'
$folder = Join-Path $root 'artifacts\test-downloads'
$captures = Join-Path $root 'artifacts\screenshots'
New-Item -ItemType Directory -Force $folder,$captures | Out-Null
Remove-Item (Join-Path $captures '*') -Force
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
Add-Type -Namespace Pipe -Name Native -MemberDefinition '[DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint id);'
# The engine serving this run's pipe, never another copy running on its own data folder.
function Get-Engine {
 $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
 try {
  $pipe.Connect(5000); $id = [uint32]0
  if (![Pipe.Native]::GetNamedPipeServerProcessId($pipe.SafePipeHandle, [ref]$id)) { throw 'Could not tell which process serves the engine pipe.' }
  return Get-Process -Id $id
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
# Every category folder points into artifacts\test-downloads so no test file lands in the real Downloads folder.
function Use-TestFolders {
 $settings = (Send-Engine @{command='getSettings'; version=2}).settings
 $folders = @{}
 foreach ($c in 'Video','Music','Documents','Programs','Archives','Images','Other') { $folders[$c] = Join-Path $folder $c }
 $settings | Add-Member -NotePropertyName folders -NotePropertyValue $folders -Force
 Send-Engine @{command='setSettings'; version=2; settings=$settings} | Out-Null
 $back = (Send-Engine @{command='getSettings'; version=2}).settings.folders
 if ($back.Documents -ne (Join-Path $folder 'Documents')) { throw 'Test folders were not applied.' }
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
 # Switching sections rebuilds the list: for a moment there is none, and the caller's Wait-For simply asks again.
 if (!$list) { return @() }
 $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)))
 return @($items | ForEach-Object { $_.Current.Name })
}
function Test-Downloads($app) {
 Send-Engine @{command='add'; version=2; url='http://127.0.0.1:18764/complete.bin'; folder=$folder} | Out-Null
 Send-Engine @{command='add'; version=2; url='http://127.0.0.1:18764/paused.bin'; folder=$folder; startPaused=$true} | Out-Null
 Send-Engine @{command='add'; version=2; url='http://127.0.0.1:18764/active.bin'; folder=$folder} | Out-Null
 try { Send-Engine @{command='add'; version=2; url='http://127.0.0.1:18764/missing.bin'; folder=$folder} | Out-Null; throw 'A 404 link was accepted.' }
 catch { if ("$_" -notlike '*no longer has this file*') { throw "A 404 link gave the wrong error: $_" } }
 Send-Engine @{command='add'; version=2; url='http://127.0.0.1:18764/vanish.bin'; folder=$folder} | Out-Null
 Send-Engine @{command='add'; version=2; url='http://127.0.0.1:18764/drop.bin'; folder=$folder} | Out-Null
 # The server cuts drop.bin off once: an automatic retry must finish it whole.
 Wait-For { $drop = @((Send-Engine @{command='list'}).jobs | Where-Object { $_.url -like '*/drop.bin' })[0]; $drop.status -eq 'Completed' -and (Get-Item $drop.destination).Length -eq 1048576 } 'drop.bin completed whole after a retry'
 Wait-For { @((Send-Engine @{command='list'}).jobs | Where-Object { $_.url -like '*/vanish.bin' })[0].status -eq 'Failed' } 'vanish.bin failed (404 after the probe)'
 Wait-For { $active = @((Send-Engine @{command='list'}).jobs | Where-Object { $_.url -like '*/active.bin' })[0]; $active.resumable -and @($active.parts).Count -gt 1 } 'active.bin resumable and split into parts'
 Wait-For { (Toggle $app 'complete.bin') -eq 'none' -and (Send-Engine @{command='list'}).jobs.status -contains 'Completed' } 'complete.bin with no enabled toggle'
 Wait-For { (Toggle $app 'paused.bin') -eq 'Resume' } 'paused.bin offering Resume'
 Wait-For { (Toggle $app 'active.bin') -eq 'Pause' } 'active.bin offering Pause'
 (Row $app 'active.bin').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Wait-For { $pane = Find-Named $app 'Download details'; $pane -and $pane.FindFirst($scope,(New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty,'Copy link'))) -and $pane.FindFirst($scope,(New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty,'Show in folder'))) } 'the details pane with Copy link and Show in folder'
 Capture $app '02-downloads-details.png'
 Test-Section $app 'Downloading' 'active.bin' '03-downloading.png'
 Test-Section $app 'Queued and paused' 'paused.bin' '03-queued.png'
 Test-Section $app 'Completed' 'complete.bin,drop.bin' '03-completed.png'
 Test-Section $app 'Failed' 'vanish.bin' '03-failed.png'
 Select-Nav $app 'All downloads'
}
# A list section shows exactly these files (in any order).
function Test-Section($app, $nav, $expected, $capture) {
 Select-Nav $app $nav
 $want = @($expected -split ',' | Sort-Object)
 try { Wait-For { $names = @(Listed $app | Sort-Object); ($names -join ',') -eq ($want -join ',') } "$nav showing $expected" }
 catch { throw "$_ (listed: $((Listed $app) -join ', '))" }
 Capture $app $capture
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
 try { Wait-For { @((Send-Engine @{command='list'}).jobs | Where-Object { $_.destination -like (Join-Path $folder 'Documents\report.pdf') -and $_.status -eq 'Completed' -and $_.category -eq 'Documents' }).Count -eq 1 } 'report.pdf completed under Documents' }
 catch { Capture $app 'failed-add.png'; throw "$_ (jobs: $(((Send-Engine @{command='list'}).jobs | ForEach-Object { "$($_.destination) $($_.status) $($_.category) $($_.error)" }) -join '; '))" }
 Test-Section $app 'Documents' 'report.pdf' '04-category-documents.png'
 Select-Nav $app 'All downloads'
}

# Settings: digits typed on an Arabic keyboard, clamping, Ultra locking parts, theme and downloads at once reaching the engine.
function Value($element) { return $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Commit-Field($app, $name, $text) {
 $field = Find-Named $app $name
 if (!$field) { Capture $app 'failed-settings.png'; throw "No field named $name" }
 $field.SetFocus()
 $field.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
 $other = if ($name -eq 'Auto-retry attempts') { 'Downloads at once' } else { 'Auto-retry attempts' }
 $away = Find-Named $app $other
 if (!$away) { Capture $app 'failed-settings.png'; throw "No field named $other" }
 $away.SetFocus()
}
function Choose($app, $combo, $item) {
 $box = Find-Named $app $combo
 $box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 Wait-For { Find-Named $app $item } "the $item option"
 (Find-Named $app $item).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 $box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
 Start-Sleep -Milliseconds 400
}
# Settings shows one section at a time, chosen from the tabs at the top.
function Select-Section($app, $name) {
 Wait-For { Find-Named $app $name } "the $name settings tab"
 (Find-Named $app $name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Wait-For { Find-Named $app "$name settings" } "the $name settings section"
}
function Test-Settings($app) {
 Select-Nav $app 'Settings'
 Select-Section $app 'Downloads'
 Wait-For { Find-Named $app 'Parts per file' } 'the Settings page'
 $arabic = [string][char]0x0662 + [char]0x0660
 Commit-Field $app 'Parts per file' $arabic
 Wait-For { (Value (Find-Named $app 'Parts per file')) -eq '16' -and (Send-Engine @{command='getSettings'}).settings.partsPerFile -eq 16 } 'Parts per file clamped to 16'
 $ultra = Find-Named $app 'Ultra performance'
 if ($ultra.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne 'On') { $ultra.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
 Wait-For { (Find-Named $app 'Parts per file: 16') -and -not (Find-Named $app 'Parts per file') -and (Send-Engine @{command='getSettings'}).settings.ultraPerformance } 'Parts per file shown as 16 and not editable under Ultra performance'
 Commit-Field $app 'Downloads at once' '1'
 Wait-For { (Send-Engine @{command='getSettings'}).settings.downloadsAtOnce -eq 1 } 'Downloads at once = 1 in the engine'
 Select-Section $app 'General'
 Choose $app 'Theme' 'Light'
 Wait-For { (Send-Engine @{command='getSettings'}).settings.theme -eq 'Light' } 'Theme Light in the engine'
 Capture $app '05-settings-light.png'
 Choose $app 'Theme' 'Dark'
 Wait-For { (Send-Engine @{command='getSettings'}).settings.theme -eq 'Dark' } 'Theme Dark in the engine'
 Capture $app '05-settings-dark.png'
 Select-Section $app 'Downloads'
 $ultra = Find-Named $app 'Ultra performance'
 $ultra.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
 Wait-For { $parts = Find-Named $app 'Parts per file'; $parts.Current.IsEnabled -and (Value $parts) -eq '16' } 'Parts per file back to 16 and editable'
 Select-Section $app 'General'
 Choose $app 'Theme' 'System'
 Select-Section $app 'Downloads'
 Commit-Field $app 'Downloads at once' '3'
 Select-Nav $app 'All downloads'
}

# Video downloads: a fresh data folder has no video tools; the card says so and offers to set them up.
function Test-VideoSettings($app) {
 Select-Nav $app 'Settings'
 Select-Section $app 'Video downloads'
 Wait-For { $texts = (Window $app).FindAll($scope,(New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text))); @($texts | Where-Object { $_.Current.Name -like 'Not set up yet*' }).Count -gt 0 } 'the video tools status'
 Wait-For { Find-Named $app 'Set up video tools' } 'the Set up now button'
 Capture $app '06-settings-video.png'
 Select-Section $app 'General'
 Select-Nav $app 'All downloads'
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
 Use-TestFolders
 Set-Theme 'Dark'; Capture $app '01-shell-dark.png'
 Set-Theme 'Light'; Capture $app '01-shell-light.png'
 Set-Theme 'System'
 Test-Downloads $app
 Test-AddDialog $app
 Test-Settings $app
 Test-VideoSettings $app

 # Closing the window must leave the engine downloading.
 Wait-For { @((Send-Engine @{command='list'}).jobs | Where-Object { $_.status -eq 'Downloading' }).Count -gt 0 } 'an active transfer'
 [void]$app.CloseMainWindow(); $app.WaitForExit(10000) | Out-Null
 if (!$app.HasExited) { throw 'The app did not exit when its window closed.' }
 try { $engine = Get-Engine } catch { throw 'The engine (and its tray icon) stopped when the window closed.' }
 # The tray icon and its menu are drawn at the screen's real scale only when the engine is DPI aware.
 Add-Type -Namespace Dpi -Name Shcore -MemberDefinition '[DllImport("shcore.dll")] public static extern int GetProcessDpiAwareness(IntPtr process, out int value);'
 $awareness = -1; [void][Dpi.Shcore]::GetProcessDpiAwareness($engine.Handle, [ref]$awareness)
 if ($awareness -ne 2) { throw "The engine is not per-monitor DPI aware (value $awareness)." }
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
