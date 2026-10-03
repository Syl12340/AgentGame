$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cliDll = Join-Path $workspaceRoot 'src/AgentGame.Cli/bin/Release/net10.0/agent-game.dll'
$python = if ($env:AGENT_GAME_PYTHON) { $env:AGENT_GAME_PYTHON } else { 'python' }
$testDirectory = Join-Path $workspaceRoot "artifacts/run-cli-tests/$([guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($testDirectory) | Out-Null
$scene = Get-Content -LiteralPath (Join-Path $workspaceRoot 'tests/Fixtures/Core/facility-small.json') -Raw | ConvertFrom-Json
$scene.max_ticks = 13
$scenario = Join-Path $testDirectory 'valid scene.json'
[IO.File]::WriteAllText($scenario, ($scene | ConvertTo-Json -Depth 8))
$agent = Join-Path $testDirectory 'agent with spaces.py'
Copy-Item -LiteralPath (Join-Path $workspaceRoot 'tests/Fixtures/Agents/fault_agent.py') -Destination $agent
$script:runChecks = 0
function Invoke-RunCheck([string]$Name, [string[]]$Arguments, [int]$Code, [bool]$ErrorStream = $false, [bool]$Json = $true) {
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
        if (-not $process.WaitForExit(12000)) { $process.Kill($true); throw "CLI timeout: $Name" }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $Code) { throw "Wrong CLI exit code: $Name ($($process.ExitCode)); $stderr" }
        $content = if ($ErrorStream) { $stderr } else { $stdout }
        $other = if ($ErrorStream) { $stdout } else { $stderr }
        if ($other.Length -ne 0 -or [string]::IsNullOrWhiteSpace($content) -or $content.Contains([char]27)) { throw "Wrong stream: $Name" }
        if ($Json -and ($content.Trim() -split '\r?\n').Count -ne 1) { throw "Not one JSON line: $Name" }
        $result = if ($Json) { $content | ConvertFrom-Json -ErrorAction Stop } else { $content }
        $script:runChecks++
        Write-Host "PASS Run CLI $Name"
        return $result
    } finally { $process.Dispose() }
}
$prefix = @('run','--scenario',$scenario)
$success = Invoke-RunCheck 'success' ($prefix + @('--',$python,'-u',$agent,'--mode','success')) 0
if ($success.format -cne 'run/1' -or $success.kind -cne 'success' -or $success.tick -ne 13 -or -not $success.terminated -or $success.truncated) { throw 'Wrong success summary.' }
$random = Join-Path $workspaceRoot 'agents/random_agent.py'
$result = Invoke-RunCheck 'random protocol baseline' ($prefix + @('--',$python,'-u',$random,'--seed','42')) 0
if ($result.kind -cne 'turn_limit' -or $result.tick -ne 13 -or -not $result.truncated) { throw 'Wrong random outcome.' }
$result = Invoke-RunCheck 'wrong request ID' ($prefix + @('--',$python,'-u',$agent,'--mode','wrong-id')) 1
if ($result.error.code -cne 'protocol_violation' -or $result.tick -ne 0) { throw 'Invalid action advanced state.' }
$result = Invoke-RunCheck 'decision timeout' ($prefix + @('--decision-ms','250','--',$python,'-u',$agent,'--mode','silent')) 1
if ($result.error.code -cne 'decision_timeout' -or $result.tick -ne 0) { throw 'Wrong timeout summary.' }
$result = Invoke-RunCheck 'missing executable' ($prefix + @('--',(Join-Path $testDirectory 'missing-executable'))) 1
if ($result.error.code -cne 'start_failed') { throw 'Wrong startup summary.' }
$marker = '中文 spaces ; & $() literal "quotes"'
$result = Invoke-RunCheck 'argument boundaries' ($prefix + @('--',$python,'-u',$agent,'--mode','argv','--marker',$marker)) 0
if ($result.agent_name -cne $marker) { throw 'ArgumentList did not preserve argument.' }
$result = Invoke-RunCheck 'stderr flood stays inside JSON' ($prefix + @('--',$python,'-u',$agent,'--mode','stderr-flood')) 0
if ($result.kind -cne 'turn_limit' -or $result.stderr_tail.Length -gt 65536 -or -not $result.stderr_tail.TrimEnd().EndsWith('TAIL_MARKER')) { throw 'Stderr boundary failed.' }
$null = Invoke-RunCheck 'missing argument separator' $prefix 2 $true $false
$null = Invoke-RunCheck 'duplicate run option' ($prefix + @('--scenario',$scenario,'--',$python,$agent)) 2 $true $false
$invalid = Join-Path $workspaceRoot 'tests/Fixtures/Core/invalid-door-bypass.json'
$null = Invoke-RunCheck 'invalid scenario before startup' @('run','--scenario',$invalid,'--',$python,'-u',$agent,'--mode','wait') 1 $true
Write-Output "Run CLI: $script:runChecks/$script:runChecks passed."
