param(
    [string]$CandidateHost = '',
    [ValidateRange(1,65535)][int]$CandidatePort = 47832,
    [ValidateRange(3,10)][int]$Samples = 5
)
$ErrorActionPreference = 'Stop'
$targets = @(
    [pscustomobject]@{Name='Current OpenDesk VPS';Address='139.99.135.69';Port=47832},
    [pscustomobject]@{Name='Mumbai reference (AWS API, not a relay)';Address='ec2.ap-south-1.amazonaws.com';Port=443},
    [pscustomobject]@{Name='Singapore reference (AWS API, not a relay)';Address='ec2.ap-southeast-1.amazonaws.com';Port=443}
)
if($CandidateHost){$targets += [pscustomobject]@{Name='Candidate relay';Address=$CandidateHost;Port=$CandidatePort}}
$results = foreach($target in $targets){
    $durations = [System.Collections.Generic.List[double]]::new()
    $failures = 0
    try{
        $address = [Net.Dns]::GetHostAddresses($target.Address) | Where-Object {$_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork} | Select-Object -First 1
        if(-not $address){throw 'No IPv4 address'}
        for($sample=0;$sample -lt $Samples;$sample++){
            $client = [Net.Sockets.TcpClient]::new()
            $watch = [Diagnostics.Stopwatch]::StartNew()
            try{
                $connecting=$client.ConnectAsync($address,[int]$target.Port)
                if(-not $connecting.Wait(3000)){throw 'Connection timed out'}
                $durations.Add($watch.Elapsed.TotalMilliseconds)
            }catch{$failures++}finally{$watch.Stop();$client.Dispose()}
        }
    }catch{$failures=$Samples}
    $ordered = @($durations | Sort-Object)
    [pscustomobject]@{
        Target=$target.Name
        MinimumMs=if($ordered.Count){[Math]::Round($ordered[0],1)}else{$null}
        MedianMs=if($ordered.Count){[Math]::Round($ordered[[int][Math]::Floor($ordered.Count/2)],1)}else{$null}
        MaximumMs=if($ordered.Count){[Math]::Round($ordered[-1],1)}else{$null}
        Failed=$failures
        Samples=$Samples
    }
}
$results | Format-Table -AutoSize
Write-Output 'TCP setup measurements show route latency, not screen FPS or guaranteed relay performance.'
Write-Output 'Run from BOTH PCs. Reference endpoints belong to AWS and only compare regional routes.'
