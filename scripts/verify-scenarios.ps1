$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cliDll = Join-Path $workspaceRoot 'src/AgentGame.Cli/bin/Release/net10.0/agent-game.dll'
$testDirectory = Join-Path $workspaceRoot "artifacts/cli-tests/$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($testDirectory) | Out-Null
$script:scenarioChecks = 0
function Invoke-ScenarioCheck([string]$Name, [string[]]$Arguments, [int]$Code, [bool]$ErrorStream = $false, [bool]$Json = $true) {
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.ArgumentList.Add($cliDll)
    foreach ($value in $Arguments) { $info.ArgumentList.Add($value) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw 'Cannot start CLI.' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) { $process.Kill($true); throw "Timeout: $Name" }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $Code) { throw "Exit code mismatch: $Name ($($process.ExitCode)); $stderr" }
        $content = if ($ErrorStream) { $stderr } else { $stdout }
        $other = if ($ErrorStream) { $stdout } else { $stderr }
        if ($other.Length -ne 0 -or [string]::IsNullOrWhiteSpace($content) -or $content.Contains([char]27)) { throw "Stream contract: $Name" }
        $result = if ($Json) { $content | ConvertFrom-Json -ErrorAction Stop } else { $content }
        $script:scenarioChecks++
        Write-Host "PASS Scenario CLI $Name"
        return $result
    } finally { $process.Dispose() }
}
$output = Join-Path $testDirectory 'generated scene.json'
$generated = Invoke-ScenarioCheck 'generate' @('scenario','generate','--seed','42','--out',$output) 0
if (-not $generated.valid -or $generated.seed -cne '42' -or $generated.reference_length -gt 128) { throw 'Invalid generation summary.' }
$validated = Invoke-ScenarioCheck 'validate roundtrip' @('scenario','validate',$output) 0
if ($validated.initial_hash -cne $generated.initial_hash -or $validated.reference_length -ne $generated.reference_length) { throw 'Roundtrip changed state.' }
$manual = Join-Path $workspaceRoot 'tests/Fixtures/Core/facility-small.json'
$manualResult = Invoke-ScenarioCheck 'manual shortest path' @('scenario','validate',$manual) 0
if ($manualResult.reference_length -ne 13) { throw 'Manual route changed.' }
foreach ($case in @(
    @{File='invalid-key-behind-door.json';Error='key_unreachable_before_door'},
    @{File='invalid-door-bypass.json';Error='door_not_required'}
)) {
    $result = Invoke-ScenarioCheck $case.Error @('scenario','validate',(Join-Path $workspaceRoot "tests/Fixtures/Core/$($case.File)")) 1
    if ($result.valid -or $result.error -cne $case.Error) { throw 'Bad topology accepted.' }
}
$before = (Get-FileHash -LiteralPath $output).Hash
$null = Invoke-ScenarioCheck 'refuse overwrite' @('scenario','generate','--seed','42','--out',$output) 1 $true
if ((Get-FileHash -LiteralPath $output).Hash -cne $before) { throw 'Existing file modified.' }
foreach ($case in @(
    @{Name='duplicate option';Args=@('--seed','42','--seed','43','--out',$output)},
    @{Name='unknown option';Args=@('--seed','42','--wat','1','--out',$output)},
    @{Name='missing output';Args=@('--seed','42')},
    @{Name='noncanonical seed';Args=@('--seed','042','--out',$output)},
    @{Name='overflow seed';Args=@('--seed','18446744073709551616','--out',$output)}
)) { $null = Invoke-ScenarioCheck $case.Name (@('scenario','generate') + $case.Args) 2 $true $false }
$rejected = Join-Path $testDirectory 'rejected.json'
$failure = Invoke-ScenarioCheck 'bounded retries' @('scenario','generate','--seed','42','--max-reference-length','1','--max-attempts','2','--out',$rejected) 1 $true
if ($failure.seed -cne '42' -or $failure.generation_attempts -ne 2 -or $failure.error -cne 'reference_length_exceeded' -or (Test-Path -LiteralPath $rejected)) { throw 'Failure diagnostics or file lifecycle wrong.' }
$failure = Invoke-ScenarioCheck 'turn budget' @('scenario','generate','--seed','42','--max-ticks','1','--max-attempts','2','--out',$rejected) 1 $true
if ($failure.error -cne 'no_solution_within_turn_limit') { throw 'Turn-budget diagnostic wrong.' }
$bad = Join-Path $testDirectory 'invalid.json'
[IO.File]::WriteAllBytes($bad, [byte[]]@(0xc3,0x28))
$null = Invoke-ScenarioCheck 'invalid UTF8' @('scenario','validate',$bad) 1 $true
[IO.File]::WriteAllBytes($bad, [byte[]]::new(65537))
$null = Invoke-ScenarioCheck 'over-limit file' @('scenario','validate',$bad) 1 $true
$forged = Get-Content -LiteralPath $manual -Raw | ConvertFrom-Json
$forged | Add-Member -NotePropertyName reference_length -NotePropertyValue 14 -Force
[IO.File]::WriteAllText($bad, ($forged | ConvertTo-Json -Depth 8))
$failure = Invoke-ScenarioCheck 'false reference certificate' @('scenario','validate',$bad) 1
if ($failure.valid -or $failure.error -cne 'reference_length_mismatch') { throw 'False reference certificate accepted.' }
Write-Output "Scenario CLI: $script:scenarioChecks/$script:scenarioChecks passed."
