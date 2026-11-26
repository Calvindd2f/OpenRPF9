<#
.SYNOPSIS
  Checks that the stream scanner gives identical results across thread counts and chunk sizes, and times it.

.DESCRIPTION
  Runs `OpenRPF9.exe --dump-streams <archive>`.
  Writes results.md. The dumps list offsets and hashes of the archive's contents.

.EXAMPLE
  # Large archive
  ./tools/verify-scan.ps1 -Exe ... -Archive C:\samples\big.rpf -Runs 1 -StressChunkKB @() -OutDir C:\temp\scan-verify
#>
param(
  [Parameter(Mandatory)] [string] $Exe,
  [Parameter(Mandatory)] [string[]] $Archive,
  [Parameter(Mandatory)] [string] $OutDir,
  [string] $ReferenceExe,
  # How builds are named in results.md
  [string] $ExeLabel = 'working tree',
  [string] $ReferenceLabel = 'previous scanner',
  [int[]] $Threads = @(1, 4, 8, 16, [Environment]::ProcessorCount),
  [int[]] $StressChunkKB = @(16, 64),
  [int] $Runs = 3
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
if ($ReferenceExe) { $ReferenceExe = (Resolve-Path $ReferenceExe).Path }
New-Item -ItemType Directory -Force $OutDir | Out-Null

function Invoke-Dump([string] $exe, [string] $archive, [string] $outFile, [int] $threads, [int] $chunkKB, [int] $runs) {
  foreach ($n in 'OPENRPF9_SCAN_THREADS', 'OPENRPF9_SCAN_CHUNK_KB', 'OPENRPF9_DUMP_RUNS', 'OPENRPF9_SCAN_MAX') { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
  if ($threads -gt 0) { $env:OPENRPF9_SCAN_THREADS = "$threads" }
  if ($chunkKB -gt 0) { $env:OPENRPF9_SCAN_CHUNK_KB = "$chunkKB" }
  $env:OPENRPF9_DUMP_RUNS = "$runs"
  $errFile = "$outFile.err"
  $p = Start-Process $exe -ArgumentList '--dump-streams', "`"$archive`"" -RedirectStandardOutput $outFile -RedirectStandardError $errFile -PassThru -NoNewWindow
  $peak = 0
  while (-not $p.HasExited) {
    try { $p.Refresh(); if ($p.PeakWorkingSet64 -gt $peak) { $peak = $p.PeakWorkingSet64 } } catch { }
    Start-Sleep -Milliseconds 250
  }
  $p.WaitForExit()
  foreach ($n in 'OPENRPF9_SCAN_THREADS', 'OPENRPF9_SCAN_CHUNK_KB', 'OPENRPF9_DUMP_RUNS') { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
  $err = (Get-Content $errFile -Raw).Trim()
  if ($err -notmatch 'streams=(\d+) ms=([\d,]+)') { throw "Unexpected output from ${exe}: $err" }
  $ms = $Matches[2].Split(',') | ForEach-Object { [int]$_ }
  $warm = if ($ms.Count -gt 1) { ($ms | Select-Object -Skip 1 | Measure-Object -Minimum).Minimum } else { $ms[0] }
  [pscustomobject]@{ Streams = [int]$Matches[1]; ColdMs = $ms[0]; WarmMs = $warm; PeakGB = $peak / 1GB; Hash = (Get-FileHash $outFile).Hash }
}

$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
$lines = @("# Scanner verification", "", "CPU: $cpu ($([Environment]::ProcessorCount) logical processors)  ", "Build under test: $ExeLabel  ")
if ($ReferenceExe) { $lines += "Reference build: $ReferenceLabel  " }
$lines += "Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm')", ""
$allMatch = $true

foreach ($a in $Archive) {
  $a = (Resolve-Path $a).Path
  $name = [IO.Path]::GetFileNameWithoutExtension($a)
  $dir = Join-Path $OutDir $name
  New-Item -ItemType Directory -Force $dir | Out-Null
  $sizeMB = [math]::Round((Get-Item $a).Length / 1MB)
  Write-Host "== $name ($sizeMB MB)"

  $configs = @()
  if ($ReferenceExe) { $configs += [pscustomobject]@{ Label = "reference ($ReferenceLabel)"; Exe = $ReferenceExe; Threads = 0; Chunk = 0 } }
  foreach ($t in $Threads) { $configs += [pscustomobject]@{ Label = "$t thread(s)"; Exe = $Exe; Threads = $t; Chunk = 0 } }
  foreach ($c in $StressChunkKB) { $configs += [pscustomobject]@{ Label = "$([Environment]::ProcessorCount) threads, $c KB chunks (stress)"; Exe = $Exe; Threads = [Environment]::ProcessorCount; Chunk = $c } }

  $rows = @(); $ref = $null; $baseMs = $null
  foreach ($cfg in $configs) {
    $file = Join-Path $dir (($cfg.Label -replace '[^\w]+', '_').Trim('_') + '.txt')
    Write-Host "   $($cfg.Label) ..." -NoNewline
    $r = Invoke-Dump $cfg.Exe $a $file $cfg.Threads $cfg.Chunk $Runs
    if (-not $ref) { $ref = $r }
    $match = $r.Hash -eq $ref.Hash
    if (-not $match) { $allMatch = $false }
    if ($cfg.Threads -eq 1 -and $cfg.Chunk -eq 0) { $baseMs = $r.WarmMs }
    Write-Host (" {0} streams, warm {1} ms, {2}" -f $r.Streams, $r.WarmMs, $(if ($match) { 'identical' } else { 'DIFFERS' }))
    $rows += [pscustomobject]@{ Cfg = $cfg; R = $r; Match = $match }
  }

  $lines += "## $name.rpf ($sizeMB MB)", "", "| Configuration | Streams | Matches reference | Cold ms | Warm ms | Speedup vs 1 thread | Peak RAM |", "|---|---|---|---|---|---|---|"
  foreach ($row in $rows) {
    $speed = if ($baseMs -and $row.R.WarmMs -gt 0 -and $row.Cfg.Exe -eq $Exe) { '{0:N1}x' -f ($baseMs / $row.R.WarmMs) } else { '' }
    $lines += "| {0} | {1} | {2} | {3} | {4} | {5} | {6:N2} GB |" -f $row.Cfg.Label, $row.R.Streams, $(if ($row.Match) { 'yes' } else { '**NO**' }), $row.R.ColdMs, $row.R.WarmMs, $speed, $row.R.PeakGB
  }
  $lines += ""
}

$lines += $(if ($allMatch) { "all configs produced identical dumps." } else { "**Some configs differed from the ref; see dumps**" })
$report = Join-Path $OutDir 'results.md'
Set-Content -Path $report -Value $lines -Encoding utf8
Write-Host "Report: $report"
if (-not $allMatch) { exit 1 }
