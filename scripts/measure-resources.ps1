# Measures how light the published engine and app are: CPU (% of one core) and working set, sampled every 500 ms.
# Phases: engine idle 20 s; three 512 MiB downloads from a local fixture (Balanced, no Ultra); idle 20 s after they
# finish (the engine trims its memory 10 s after going quiet); the app window open and idle 20 s.
# Prints a table, writes it to artifacts\screenshots\resources.txt and exits 1 if a target is missed:
#   engine idle: under 1 % CPU and under 40 MB at the end of each idle phase
#   engine with 3 downloads: under 10 % of one core
#   app idle: under 1 % CPU
# All data and downloads stay in artifacts\measure-data and are deleted at the end. Works on Windows PowerShell 5.1 and PowerShell 7.
param([int]$Port = 18765)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$data = Join-Path $root 'artifacts\measure-data'
$downloads = Join-Path $data 'downloads'
$out = Join-Path $root 'artifacts\screenshots'
$env:IDMREA_DATA_DIRECTORY = $data
$sha = [Security.Cryptography.SHA256]::Create()
$hash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($data))))).Replace('-','')
$pipeName = 'IDMRea.' + $hash.Substring(0,20)
$cores = [Environment]::ProcessorCount

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
function Stop-Engine {
 try { Send-Engine @{command='shutdown'} 500 | Out-Null } catch { return }
 Wait-For { try { Send-Engine @{command='list'} 300 | Out-Null; $false } catch { $true } } 'the engine to stop' 20
 Start-Sleep -Milliseconds 500
}

# Samples each named process every 500 ms for $seconds (or until $until returns true), and returns one row per process.
function Measure-Phase($phase, $processes, $seconds, $until = $null) {
 $samples = @{}
 $last = @{}
 foreach ($name in $processes.Keys) { $p = $processes[$name]; $p.Refresh(); $last[$name] = @($p.TotalProcessorTime.TotalMilliseconds, [Diagnostics.Stopwatch]::GetTimestamp(), $p.PrivilegedProcessorTime.TotalMilliseconds); $samples[$name] = New-Object Collections.ArrayList }
 $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
 while ([DateTime]::UtcNow -lt $deadline) {
  Start-Sleep -Milliseconds 500
  foreach ($name in $processes.Keys) {
   $p = $processes[$name]; $p.Refresh()
   $now = [Diagnostics.Stopwatch]::GetTimestamp(); $cpu = $p.TotalProcessorTime.TotalMilliseconds; $kernel = $p.PrivilegedProcessorTime.TotalMilliseconds
   $wall = ($now - $last[$name][1]) * 1000.0 / [Diagnostics.Stopwatch]::Frequency
   [void]$samples[$name].Add(@((($cpu - $last[$name][0]) / $wall * 100), ($p.WorkingSet64 / 1MB), (($kernel - $last[$name][2]) / $wall * 100)))
   $last[$name] = @($cpu, $now, $kernel)
  }
  if ($until -and (& $until)) { break }
 }
 foreach ($name in $processes.Keys) {
  $rows = $samples[$name]
  $cpuValues = @($rows | ForEach-Object { $_[0] }); $memory = @($rows | ForEach-Object { $_[1] }); $kernelValues = @($rows | ForEach-Object { $_[2] })
  [pscustomobject]@{
   Phase = $phase; Process = $name; Seconds = [math]::Round($rows.Count / 2.0, 1)
   CpuAvg = [math]::Round(($cpuValues | Measure-Object -Average).Average, 2); KernelAvg = [math]::Round(($kernelValues | Measure-Object -Average).Average, 2); CpuMax = [math]::Round(($cpuValues | Measure-Object -Maximum).Maximum, 1)
   MbAvg = [math]::Round(($memory | Measure-Object -Average).Average, 1); MbEnd = [math]::Round($memory[-1], 1)
  }
 }
}

Stop-Engine
Wait-For { Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue; !(Test-Path $data) } 'the measure data folder to clear' 15
New-Item -ItemType Directory -Force $downloads,$out | Out-Null
$server = Start-Process python -ArgumentList (Join-Path $PSScriptRoot 'fixture-server.py'),$Port -PassThru -WindowStyle Hidden
$engine = $null; $app = $null
$results = @()
try {
 $engine = Start-Process (Join-Path $root 'artifacts\windows\engine\IDMRea.Engine.exe') -PassThru
 Wait-For { try { Send-Engine @{command='list'} 300 | Out-Null; $true } catch { $false } } 'the engine'
 $settings = (Send-Engine @{command='getSettings'; version=2}).settings
 $folders = @{}
 foreach ($c in 'Video','Music','Documents','Programs','Archives','Images','Other') { $folders[$c] = $downloads }
 $settings | Add-Member -NotePropertyName folders -NotePropertyValue $folders -Force
 $settings.ultraPerformance = $false; $settings.downloadsAtOnce = 3; $settings.speedLimitBytesPerSecond = 0
 Send-Engine @{command='setSettings'; version=2; settings=$settings} | Out-Null
 Start-Sleep -Seconds 12 # past start-up and the first memory trim

 $results += Measure-Phase 'Engine idle' @{ Engine = $engine } 20
 foreach ($n in 1..3) { Send-Engine @{command='add'; version=2; url="http://127.0.0.1:$Port/big.bin"; folder=$downloads} | Out-Null }
 $finished = { @((Send-Engine @{command='list'} 2000).jobs | Where-Object { $_.status -ne 'Completed' }).Count -eq 0 }
 $results += Measure-Phase '3 downloads' @{ Engine = $engine } 300 $finished
 if (!(& $finished)) { throw 'The three downloads did not finish within 5 minutes.' }
 $jobs = (Send-Engine @{command='list'}).jobs
 $bytes = ($jobs | Measure-Object -Property receivedBytes -Sum).Sum
 $results += Measure-Phase 'Engine idle after' @{ Engine = $engine } 20

 $app = Start-Process (Join-Path $root 'artifacts\windows\IDMRea.App.exe') -PassThru
 Wait-For { $app.Refresh(); $app.MainWindowHandle -ne 0 } 'the app window'
 Start-Sleep -Seconds 30 # start-up: first layout, the engine subscription and .NET's background re-compilation
 $results += Measure-Phase 'App open, idle' @{ App = $app; Engine = $engine } 20

 $seconds = ($results | Where-Object { $_.Phase -eq '3 downloads' }).Seconds
 $misses = @()
 foreach ($r in $results) {
  if ($r.Process -eq 'Engine' -and $r.Phase -like 'Engine idle*') {
   if ($r.CpuAvg -ge 1) { $misses += "$($r.Phase): engine CPU $($r.CpuAvg) % (target under 1 %)" }
   if ($r.MbEnd -ge 40) { $misses += "$($r.Phase): engine memory $($r.MbEnd) MB (target under 40 MB)" }
  }
  if ($r.Phase -eq '3 downloads' -and $r.CpuAvg -ge 10) { $misses += "3 downloads: engine CPU $($r.CpuAvg) % of one core (target under 10 %)" }
  if ($r.Phase -eq 'App open, idle' -and $r.Process -eq 'App' -and $r.CpuAvg -ge 1) { $misses += "App idle: CPU $($r.CpuAvg) % (target under 1 %)" }
 }
 $table = ($results | Format-Table Phase, Process, Seconds, @{n='CPU avg %';e={$_.CpuAvg}}, @{n='of it kernel %';e={$_.KernelAvg}}, @{n='CPU max %';e={$_.CpuMax}}, @{n='MB avg';e={$_.MbAvg}}, @{n='MB end';e={$_.MbEnd}} -AutoSize | Out-String).TrimEnd()
 $busy = $results | Where-Object { $_.Phase -eq '3 downloads' }
 $perMb = [math]::Round($busy.CpuAvg * 10 * $seconds / ($bytes / 1MB), 2)
 $summary = @($table, '', "CPU is % of one core ($cores logical cores). 3 downloads: $([math]::Round($bytes / 1MB)) MB in $seconds s ($([math]::Round($bytes / 1MB / $seconds)) MB/s), $perMb ms of CPU per MB.",
  "At a 100 Mbit/s line (12.5 MB/s) that is about $([math]::Round($perMb * 12.5 / 10, 1)) % of one core.")
 $summary += if ($misses.Count) { @('', 'Missed:') + ($misses | ForEach-Object { " - $_" }) } else { @('', 'All targets met.') }
 $summary | Tee-Object -FilePath (Join-Path $out 'resources.txt')
 if ($misses.Count) { exit 1 }
} finally {
 if ($app -and !$app.HasExited) { Stop-Process -Id $app.Id -Force }
 Stop-Engine
 if (!$server.HasExited) { Stop-Process -Id $server.Id -Force }
 Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
}
