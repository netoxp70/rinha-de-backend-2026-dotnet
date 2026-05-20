# Snapshot cpu.stat before and after a benchmark to compute the delta.
param(
    [int]$Total = 5000,
    [int]$Concurrency = 50,
    [int]$Warmup = 500
)

function Get-Cgroup {
    param([string]$Container)
    $raw = docker exec $Container cat /sys/fs/cgroup/cpu.stat
    $h = @{}
    foreach ($line in $raw) {
        $parts = $line -split '\s+'
        if ($parts.Length -ge 2) { $h[$parts[0]] = [int64]$parts[1] }
    }
    return $h
}

$cnt1 = 'rinha-backend-2026-api1-1'
$cnt2 = 'rinha-backend-2026-api2-1'

$before1 = Get-Cgroup $cnt1
$before2 = Get-Cgroup $cnt2

Write-Host "Running bench: $Total reqs / $Concurrency VUs..."
& pwsh -NoProfile -File "$PSScriptRoot\bench_fast.ps1" -TotalRequests $Total -Concurrency $Concurrency -Warmup $Warmup

$after1 = Get-Cgroup $cnt1
$after2 = Get-Cgroup $cnt2

function Print-Delta {
    param($name, $before, $after)
    $du = ($after['usage_usec'] - $before['usage_usec']) / 1000.0
    $dt = ($after['throttled_usec'] - $before['throttled_usec']) / 1000.0
    $dn = $after['nr_throttled'] - $before['nr_throttled']
    $dp = $after['nr_periods'] - $before['nr_periods']
    Write-Host ''
    Write-Host ("[{0}] periods={1}  cpu_used={2:N0}ms  throttled={3} events / {4:N0}ms  pct_throttled={5:F1}%" -f $name, $dp, $du, $dn, $dt, ([math]::Round(100 * $dt / [math]::Max(1, $du + $dt), 1)))
}

Print-Delta 'api1' $before1 $after1
Print-Delta 'api2' $before2 $after2
