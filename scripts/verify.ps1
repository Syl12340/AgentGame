param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location -LiteralPath $workspaceRoot
try {
    if (-not $SkipBuild) {
        dotnet build AgentGame.slnx -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    }
    foreach ($project in @('Core','Protocol','Runtime','Architecture')) {
        dotnet run --project "tests/AgentGame.$project.Tests" -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "$project contract checks failed." }
    }
    $cliDll = Join-Path $workspaceRoot 'src/AgentGame.Cli/bin/Release/net10.0/agent-game.dll'
    $cases = @(
        @{Arguments=@('--help');Code=0;Expected='Usage:';Error=$false},
        @{Arguments=@('--version');Code=0;Expected='0.1.0-dev';Error=$false},
        @{Arguments=@('play');Code=2;Expected='not implemented';Error=$true},
        @{Arguments=@('unknown-command');Code=2;Expected='Unknown';Error=$true}
    )
    foreach ($case in $cases) {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
        $startInfo.ArgumentList.Add($cliDll)
        foreach ($cliArgument in $case.Arguments) { $startInfo.ArgumentList.Add($cliArgument) }
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        try {
            if (-not $process.Start()) { throw 'Cannot start CLI fixture.' }
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(5000)) { $process.Kill($true); throw 'CLI fixture timed out.' }
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
            if ($process.ExitCode -ne $case.Code) { throw "Wrong CLI exit code for $($case.Arguments)." }
            $text = if ($case.Error) { $stderr } else { $stdout }
            $other = if ($case.Error) { $stdout } else { $stderr }
            if (-not $text.Contains($case.Expected) -or $other.Length -ne 0) { throw "CLI stream contract failed for $($case.Arguments)." }
            if ($stdout.Contains([char]27) -or $stderr.Contains([char]27)) { throw 'Unexpected ANSI output.' }
            Write-Output "PASS CLI $($case.Arguments -join ' ')"
        } finally { $process.Dispose() }
    }
    Write-Output 'CLI: 4/4 passed.'
    & (Join-Path $PSScriptRoot 'verify-scenarios.ps1')
    & (Join-Path $PSScriptRoot 'verify-run.ps1')
} finally { Pop-Location }
