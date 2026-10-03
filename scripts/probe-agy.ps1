param([switch]$Legacy, [ValidateSet('json','text','default')][string]$OutputFormat = 'json', [switch]$ReadWorkspace,
    [switch]$WriteProbe, [switch]$AcceptEdits)
$ErrorActionPreference = 'Stop'
$probeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$info = [Diagnostics.ProcessStartInfo]::new('C:\Users\Shaoyilei\AppData\Local\agy\bin\agy.exe')
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.WorkingDirectory = $probeRoot
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
foreach ($value in @('--sandbox','--model','gemini-3.1-pro-high','--print-timeout','30s')) { $info.ArgumentList.Add($value) }
if ($OutputFormat -ne 'default') { $info.ArgumentList.Add('--output-format'); $info.ArgumentList.Add($OutputFormat) }
if ($AcceptEdits) { $info.ArgumentList.Add('--mode'); $info.ArgumentList.Add('accept-edits') }
$prompt = 'Read-only diagnostic. Do not use tools or modify files. Reply exactly AGY_PROBE_OK.'
if ($ReadWorkspace) { $prompt = 'Read-only diagnostic. Read README.md in the working directory and report its first heading. Do not modify any file or external state.' }
if ($WriteProbe) {
    $probeFile = if ($AcceptEdits) { 'accept-edits-write.txt' } else { 'default-write.txt' }
    $prompt = "Create only artifacts/agy-diagnostics/$probeFile containing AGY_WRITE_OK. This one file write is authorized for a diagnostic. Do not modify any other file or external state. Reply with the file path when done."
}
if ($Legacy) { $info.ArgumentList.Add("--print=$prompt") }
else { $info.ArgumentList.Add('--print'); $info.ArgumentList.Add($prompt) }
$probe = [Diagnostics.Process]::new()
$probe.StartInfo = $info
try {
    if (-not $probe.Start()) { throw 'Cannot start AGY.' }
    $probe.StandardInput.Close()
    $output = $probe.StandardOutput.ReadToEndAsync()
    $errorText = $probe.StandardError.ReadToEndAsync()
    if (-not $probe.WaitForExit(45000)) { $probe.Kill($true); throw 'Probe timed out.' }
    $result = @{ exit_code=$probe.ExitCode; stdout=$output.GetAwaiter().GetResult(); stderr=$errorText.GetAwaiter().GetResult(); legacy=[bool]$Legacy }
    $directory = Join-Path $probeRoot 'artifacts/agy-diagnostics'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $prefix = if ($Legacy) {'legacy'} else {'positional'}
    $name = "$prefix-$OutputFormat-read$([bool]$ReadWorkspace)-write$([bool]$WriteProbe)-accept$([bool]$AcceptEdits).json"
    $json = $result | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $directory $name), $json)
    Write-Output $json
} finally { $probe.Dispose() }
