# ===============================
# Benchmark Configuration
# ===============================
$Count = 100000
$Iterations = 5
$Url = "http://localhost:5122/api/publish-batch?count=$Count"

# Arrays to store metrics
$durations = @()
$cpuUsages = @()
$memoryUsages = @()
$cpuDeltas = @()
$memoryDeltas = @()

Write-Host "===============================`n"
Write-Host "Starting Benchmark Test`n"
Write-Host "Messages per iteration: $Count`n"
Write-Host "Total iterations: $Iterations`n"
Write-Host "===============================`n"

# Get the NATS container name (leave empty for local/non-Docker runs)
$containerName = ""  # e.g., set to "nats" when using Docker; empty enables process-based stats

# Process name pattern for bare-metal NATS (aggregate 32/64-bit instances)
$natsProcessNamePattern = "nats-server"

# Function to get Docker container stats
function Get-DockerStats {
    param($containerName)
    
    try {
        if (-not $containerName) { return $null }
        $stats = docker stats $containerName --no-stream --format "{{.CPUPerc}},{{.MemUsage}}" 2>$null
        if ($stats) {
            $parts = $stats -split ','
            $cpuPercent = $parts[0] -replace '%', ''
            $memUsage = $parts[1] -split '/' | Select-Object -First 1
            
            # Convert memory to MB if needed
            if ($memUsage -match '(\d+\.?\d*)([MGK]i?B)') {
                $value = [double]$matches[1]
                $unit = $matches[2]
                
                $memoryMB = switch -Wildcard ($unit) {
                    "G*" { $value * 1024 }
                    "M*" { $value }
                    "K*" { $value / 1024 }
                    default { $value }
                }
            } else {
                $memoryMB = 0
            }
            
            return @{
                CPU = [double]$cpuPercent
                Memory = $memoryMB
            }
        }
    } catch {
        Write-Host "Warning: Could not get Docker stats"
    }
    
    return $null
}

# Function to get raw CPU time (seconds) and memory (MB) for local nats-server processes
function Get-NatsProcessesRawStats {
    param([string]$namePattern = "nats-server")

    try {
        $procs = Get-Process | Where-Object { $_.Name -like "$namePattern*" }
        if (-not $procs) {
            return @{ CpuSeconds = 0.0; MemoryMB = 0.0 }
        }

        $cpuSeconds = ($procs | Measure-Object -Property CPU -Sum).Sum
        if ($null -eq $cpuSeconds) { $cpuSeconds = 0 }
        $memBytes = ($procs | Measure-Object -Property WorkingSet64 -Sum).Sum
        if ($null -eq $memBytes) { $memBytes = 0 }

        return @{
            CpuSeconds = [double]$cpuSeconds
            MemoryMB   = [math]::Round(($memBytes / 1MB), 2)
        }
    } catch {
        Write-Host "Warning: Could not get process stats"
        return @{ CpuSeconds = 0.0; MemoryMB = 0.0 }
    }
}

# ===============================
# Main Benchmark Loop
# ===============================
for ($i = 1; $i -le $Iterations; $i++) {
    Write-Host "`n--- Iteration $i of $Iterations ---"
    
    # Capture CPU and Memory before the call
    if ($containerName) {
        $statsBefore = Get-DockerStats -containerName $containerName
    } else {
        $rawBefore = Get-NatsProcessesRawStats -namePattern $natsProcessNamePattern
        $tBefore = Get-Date
    }
    
    # Make the API call
    $start = Get-Date
    try {
        $response = Invoke-WebRequest -Uri $Url -Method POST -TimeoutSec 120
        $end = Get-Date
        $duration = ($end - $start).TotalMilliseconds
        
        # Store duration
        $durations += $duration
        
        Write-Host "Status: $($response.StatusCode)"
        Write-Host "Time Taken: $([math]::Round($duration, 2)) ms"
        Write-Host "Throughput: $([math]::Round($Count / ($duration / 1000), 2)) messages/sec"
        
        # Capture CPU and Memory after the call
        Start-Sleep -Milliseconds 500  # Small delay to let metrics update
        
        if ($containerName) {
            $statsAfter = Get-DockerStats -containerName $containerName

            if ($statsAfter -and $statsBefore) {
                $cpuDelta = $statsAfter.CPU - $statsBefore.CPU
                $memoryDelta = $statsAfter.Memory - $statsBefore.Memory
                
                $cpuUsages += $statsAfter.CPU
                $memoryUsages += $statsAfter.Memory
                $cpuDeltas += $cpuDelta
                $memoryDeltas += $memoryDelta
                
                Write-Host "NATS CPU: $([math]::Round($statsAfter.CPU, 2))% (Delta: $([math]::Round($cpuDelta, 2))%)"
                Write-Host "NATS Memory: $([math]::Round($statsAfter.Memory, 2)) MB (Delta: $([math]::Round($memoryDelta, 2)) MB)"
            } elseif ($statsAfter) {
                $cpuUsages += $statsAfter.CPU
                $memoryUsages += $statsAfter.Memory
                
                Write-Host "NATS CPU: $([math]::Round($statsAfter.CPU, 2))%"
                Write-Host "NATS Memory: $([math]::Round($statsAfter.Memory, 2)) MB"
            }
        } else {
            $rawAfter = Get-NatsProcessesRawStats -namePattern $natsProcessNamePattern
            $tAfter = Get-Date

            $wall = ($tAfter - $tBefore).TotalSeconds
            if ($wall -le 0) { $wall = 0.5 }
            $cpuDeltaSec = $rawAfter.CpuSeconds - $rawBefore.CpuSeconds
            $cpuCount = [int]$env:NUMBER_OF_PROCESSORS
            if ($cpuCount -le 0) { $cpuCount = 1 }
            # Normalize to percent of total system CPU (0-100)
            $cpuPercent = if ($wall -gt 0) { (100.0 * $cpuDeltaSec / $wall) / $cpuCount } else { 0 }

            $memoryDelta = $rawAfter.MemoryMB - $rawBefore.MemoryMB

            $cpuUsages += $cpuPercent
            $memoryUsages += $rawAfter.MemoryMB
            $cpuDeltas += $cpuPercent
            $memoryDeltas += $memoryDelta

            Write-Host "NATS CPU: $([math]::Round($cpuPercent, 2))%"
            Write-Host "NATS Memory: $([math]::Round($rawAfter.MemoryMB, 2)) MB (Delta: $([math]::Round($memoryDelta, 2)) MB)"
        }
        
    } catch {
        Write-Host "Error on iteration $i : $_"
        $durations += 0
    }
    
    # Small delay between iterations
    Start-Sleep -Milliseconds 500
}

# ===============================
# Calculate and Display Statistics
# ===============================
Write-Host "`n`n===============================`n"
Write-Host "BENCHMARK SUMMARY`n"
Write-Host "===============================`n"

# Duration Statistics
$avgDuration = ($durations | Measure-Object -Average).Average
$minDuration = ($durations | Measure-Object -Minimum).Minimum
$maxDuration = ($durations | Measure-Object -Maximum).Maximum
$totalDuration = ($durations | Measure-Object -Sum).Sum

Write-Host "Duration Statistics:"
Write-Host "  Average: $([math]::Round($avgDuration, 2)) ms"
Write-Host "  Minimum: $([math]::Round($minDuration, 2)) ms"
Write-Host "  Maximum: $([math]::Round($maxDuration, 2)) ms"
Write-Host "  Total: $([math]::Round($totalDuration / 1000, 2)) seconds"

# Throughput Statistics
$avgThroughput = $Count / ($avgDuration / 1000)
Write-Host "`nThroughput Statistics:"
Write-Host "  Average: $([math]::Round($avgThroughput, 2)) messages/sec"
Write-Host "  Total Messages Sent: $($Count * $Iterations)"

# CPU Statistics (if available)
if ($cpuUsages.Count -gt 0) {
    $avgCpu = ($cpuUsages | Measure-Object -Average).Average
    $minCpu = ($cpuUsages | Measure-Object -Minimum).Minimum
    $maxCpu = ($cpuUsages | Measure-Object -Maximum).Maximum
    
    Write-Host "`nNATS Container CPU Usage Statistics:"
    Write-Host "  Average: $([math]::Round($avgCpu, 2))%"
    Write-Host "  Minimum: $([math]::Round($minCpu, 2))%"
    Write-Host "  Maximum: $([math]::Round($maxCpu, 2))%"
}

# Memory Statistics (if available)
if ($memoryUsages.Count -gt 0) {
    $avgMemory = ($memoryUsages | Measure-Object -Average).Average
    $minMemory = ($memoryUsages | Measure-Object -Minimum).Minimum
    $maxMemory = ($memoryUsages | Measure-Object -Maximum).Maximum
    
    Write-Host "`nNATS Container Memory Usage Statistics:"
    Write-Host "  Average: $([math]::Round($avgMemory, 2)) MB"
    Write-Host "  Minimum: $([math]::Round($minMemory, 2)) MB"
    Write-Host "  Maximum: $([math]::Round($maxMemory, 2)) MB"
}

# CPU Delta Statistics (if available)
if ($cpuDeltas.Count -gt 0) {
    $avgCpuDelta = ($cpuDeltas | Measure-Object -Average).Average
    $minCpuDelta = ($cpuDeltas | Measure-Object -Minimum).Minimum
    $maxCpuDelta = ($cpuDeltas | Measure-Object -Maximum).Maximum
    
    Write-Host "`nNATS Container CPU Delta (Before/After) Statistics:"
    Write-Host "  Average Change: $([math]::Round($avgCpuDelta, 2))%"
    Write-Host "  Minimum Change: $([math]::Round($minCpuDelta, 2))%"
    Write-Host "  Maximum Change: $([math]::Round($maxCpuDelta, 2))%"
}

# Memory Delta Statistics (if available)
if ($memoryDeltas.Count -gt 0) {
    $avgMemoryDelta = ($memoryDeltas | Measure-Object -Average).Average
    $minMemoryDelta = ($memoryDeltas | Measure-Object -Minimum).Minimum
    $maxMemoryDelta = ($memoryDeltas | Measure-Object -Maximum).Maximum
    
    Write-Host "`nNATS Container Memory Delta (Before/After) Statistics:"
    Write-Host "  Average Change: $([math]::Round($avgMemoryDelta, 2)) MB"
    Write-Host "  Minimum Change: $([math]::Round($minMemoryDelta, 2)) MB"
    Write-Host "  Maximum Change: $([math]::Round($maxMemoryDelta, 2)) MB"
}

Write-Host "`n===============================`n"
Write-Host "Benchmark Complete!`n"
Write-Host "===============================`n"




# $Count = 100000
# $Iterations = 5
# $Url = "http://localhost:5001/api/publish-batch?count=$Count"

# # Arrays to store metrics
# $durations = @()
# $cpuUsages = @()
# $memoryUsages = @()
# $cpuDeltas = @()
# $memoryDeltas = @()

# Write-Host "===============================`n"
# Write-Host "Starting Benchmark Test`n"
# Write-Host "Messages per iteration: $Count`n"
# Write-Host "Total iterations: $Iterations`n"
# Write-Host "===============================`n"

# # Get the NATS container name - update this with your actual container name/ID
# $containerName = "096cad784eac84b07cfe2f1e0fae8d32416130c929091be2bff9769dbe6d2448"  # Change this to your NATS container name or ID

# # Function to get Docker container stats
# function Get-DockerStats {
#     param($containerName)
    
#     try {
#         $stats = docker stats $containerName --no-stream --format "{{.CPUPerc}},{{.MemUsage}}" 2>$null
#         if ($stats) {
#             $parts = $stats -split ','
#             $cpuPercent = $parts[0] -replace '%', ''
#             $memUsage = $parts[1] -split '/' | Select-Object -First 1
            
#             # Convert memory to MB if needed
#             if ($memUsage -match '(\d+\.?\d*)([MGK]i?B)') {
#                 $value = [double]$matches[1]
#                 $unit = $matches[2]
                
#                 $memoryMB = switch -Wildcard ($unit) {
#                     "G*" { $value * 1024 }
#                     "M*" { $value }
#                     "K*" { $value / 1024 }
#                     default { $value }
#                 }
#             } else {
#                 $memoryMB = 0
#             }
            
#             return @{
#                 CPU = [double]$cpuPercent
#                 Memory = $memoryMB
#             }
#         }
#     } catch {
#         Write-Host "Warning: Could not get Docker stats"
#     }
    
#     return $null
# }

# # ===============================
# # Main Benchmark Loop
# # ===============================
# for ($i = 1; $i -le $Iterations; $i++) {
#     Write-Host "`n--- Iteration $i of $Iterations ---"
    
#     # Capture CPU and Memory before the call
#     $statsBefore = Get-DockerStats -containerName $containerName
    
#     # Make the API call
#     $start = Get-Date
#     try {
#         $response = Invoke-WebRequest -Uri $Url -Method POST -TimeoutSec 120
#         $end = Get-Date
#         $duration = ($end - $start).TotalMilliseconds
        
#         # Store duration
#         $durations += $duration
        
#         Write-Host "Status: $($response.StatusCode)"
#         Write-Host "Time Taken: $([math]::Round($duration, 2)) ms"
#         Write-Host "Throughput: $([math]::Round($Count / ($duration / 1000), 2)) messages/sec"
        
#         # Capture CPU and Memory after the call
#         Start-Sleep -Milliseconds 500  # Small delay to let metrics update
        
#         $statsAfter = Get-DockerStats -containerName $containerName
        
#         if ($statsAfter -and $statsBefore) {
#             $cpuDelta = $statsAfter.CPU - $statsBefore.CPU
#             $memoryDelta = $statsAfter.Memory - $statsBefore.Memory
            
#             $cpuUsages += $statsAfter.CPU
#             $memoryUsages += $statsAfter.Memory
#             $cpuDeltas += $cpuDelta
#             $memoryDeltas += $memoryDelta
            
#             Write-Host "NATS Container CPU: $([math]::Round($statsAfter.CPU, 2))% (Delta: $([math]::Round($cpuDelta, 2))%)"
#             Write-Host "NATS Container Memory: $([math]::Round($statsAfter.Memory, 2)) MB (Delta: $([math]::Round($memoryDelta, 2)) MB)"
#         } elseif ($statsAfter) {
#             $cpuUsages += $statsAfter.CPU
#             $memoryUsages += $statsAfter.Memory
            
#             Write-Host "NATS Container CPU: $([math]::Round($statsAfter.CPU, 2))%"
#             Write-Host "NATS Container Memory: $([math]::Round($statsAfter.Memory, 2)) MB"
#         }
        
#     } catch {
#         Write-Host "Error on iteration $i : $_"
#         $durations += 0
#     }
    
#     # Small delay between iterations
#     Start-Sleep -Milliseconds 500
# }

# # ===============================
# # Calculate and Display Statistics
# # ===============================
# Write-Host "`n`n===============================`n"
# Write-Host "BENCHMARK SUMMARY`n"
# Write-Host "===============================`n"

# # Duration Statistics
# $avgDuration = ($durations | Measure-Object -Average).Average
# $minDuration = ($durations | Measure-Object -Minimum).Minimum
# $maxDuration = ($durations | Measure-Object -Maximum).Maximum
# $totalDuration = ($durations | Measure-Object -Sum).Sum

# Write-Host "Duration Statistics:"
# Write-Host "  Average: $([math]::Round($avgDuration, 2)) ms"
# Write-Host "  Minimum: $([math]::Round($minDuration, 2)) ms"
# Write-Host "  Maximum: $([math]::Round($maxDuration, 2)) ms"
# Write-Host "  Total: $([math]::Round($totalDuration / 1000, 2)) seconds"

# # Throughput Statistics
# $avgThroughput = $Count / ($avgDuration / 1000)
# Write-Host "`nThroughput Statistics:"
# Write-Host "  Average: $([math]::Round($avgThroughput, 2)) messages/sec"
# Write-Host "  Total Messages Sent: $($Count * $Iterations)"

# # CPU Statistics (if available)
# if ($cpuUsages.Count -gt 0) {
#     $avgCpu = ($cpuUsages | Measure-Object -Average).Average
#     $minCpu = ($cpuUsages | Measure-Object -Minimum).Minimum
#     $maxCpu = ($cpuUsages | Measure-Object -Maximum).Maximum
    
#     Write-Host "`nNATS Container CPU Usage Statistics:"
#     Write-Host "  Average: $([math]::Round($avgCpu, 2))%"
#     Write-Host "  Minimum: $([math]::Round($minCpu, 2))%"
#     Write-Host "  Maximum: $([math]::Round($maxCpu, 2))%"
# }

# # Memory Statistics (if available)
# if ($memoryUsages.Count -gt 0) {
#     $avgMemory = ($memoryUsages | Measure-Object -Average).Average
#     $minMemory = ($memoryUsages | Measure-Object -Minimum).Minimum
#     $maxMemory = ($memoryUsages | Measure-Object -Maximum).Maximum
    
#     Write-Host "`nNATS Container Memory Usage Statistics:"
#     Write-Host "  Average: $([math]::Round($avgMemory, 2)) MB"
#     Write-Host "  Minimum: $([math]::Round($minMemory, 2)) MB"
#     Write-Host "  Maximum: $([math]::Round($maxMemory, 2)) MB"
# }

# # CPU Delta Statistics (if available)
# if ($cpuDeltas.Count -gt 0) {
#     $avgCpuDelta = ($cpuDeltas | Measure-Object -Average).Average
#     $minCpuDelta = ($cpuDeltas | Measure-Object -Minimum).Minimum
#     $maxCpuDelta = ($cpuDeltas | Measure-Object -Maximum).Maximum
    
#     Write-Host "`nNATS Container CPU Delta (Before/After) Statistics:"
#     Write-Host "  Average Change: $([math]::Round($avgCpuDelta, 2))%"
#     Write-Host "  Minimum Change: $([math]::Round($minCpuDelta, 2))%"
#     Write-Host "  Maximum Change: $([math]::Round($maxCpuDelta, 2))%"
# }

# # Memory Delta Statistics (if available)
# if ($memoryDeltas.Count -gt 0) {
#     $avgMemoryDelta = ($memoryDeltas | Measure-Object -Average).Average
#     $minMemoryDelta = ($memoryDeltas | Measure-Object -Minimum).Minimum
#     $maxMemoryDelta = ($memoryDeltas | Measure-Object -Maximum).Maximum
    
#     Write-Host "`nNATS Container Memory Delta (Before/After) Statistics:"
#     Write-Host "  Average Change: $([math]::Round($avgMemoryDelta, 2)) MB"
#     Write-Host "  Minimum Change: $([math]::Round($minMemoryDelta, 2)) MB"
#     Write-Host "  Maximum Change: $([math]::Round($maxMemoryDelta, 2)) MB"
# }

# Write-Host "`n===============================`n"
# Write-Host "Benchmark Complete!`n"
# Write-Host "===============================`n"