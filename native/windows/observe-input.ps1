param(
    [string]$OutputPath = './input-observation.json',
    [ValidateRange(15, 120)][int]$DurationSeconds = 45,
    [ValidateRange(1, 5)][int]$IntervalSeconds = 2,
    [ValidateRange(1, 10)][int]$ReadTimeoutSeconds = 4
)

function Test-SwitchDeskFields {
    param([object]$Value, [string[]]$Names)
    if ($null -eq $Value -or $Value -isnot [pscustomobject]) { return $false }
    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Names.Count) { return $false }
    foreach ($name in $Names) {
        if ($actual -cnotcontains $name) { return $false }
    }
    return $true
}

function Test-SwitchDeskString {
    param([object]$Value, [bool]$Nonempty = $false)
    if ($Value -isnot [string]) { return $false }
    if ($Nonempty -and [string]::IsNullOrWhiteSpace($Value)) { return $false }
    for ($i = 0; $i -lt $Value.Length; $i++) {
        if ([char]::IsHighSurrogate($Value[$i])) {
            if ($i + 1 -ge $Value.Length -or -not [char]::IsLowSurrogate($Value[$i + 1])) {
                return $false
            }
            $i++
        } elseif ([char]::IsLowSurrogate($Value[$i])) {
            return $false
        }
    }
    return $true
}

function Test-SwitchDeskInteger {
    param([object]$Value, [long]$Minimum, [long]$Maximum)
    return (($Value -is [int] -or $Value -is [long]) -and
        [long]$Value -ge $Minimum -and [long]$Value -le $Maximum)
}

function Test-SwitchDeskUtcTimestamp {
    param([object]$Value)
    if ($Value -is [DateTime]) { return $Value.Kind -eq [DateTimeKind]::Utc }
    if ($Value -isnot [string] -or $Value -cnotmatch
        '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?(Z|\+00:00)$') {
        return $false
    }
    $parsed = [DateTimeOffset]::MinValue
    return [DateTimeOffset]::TryParse(
        $Value, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None, [ref]$parsed
    ) -and $parsed.Offset -eq [TimeSpan]::Zero
}

function Test-SwitchDeskProbeReport {
    param([object]$Report)
    if (-not (Test-SwitchDeskFields $Report @(
        'schema_version', 'platform', 'os_version', 'architecture', 'collected_at',
        'read_only', 'displays', 'permissions', 'capabilities', 'warnings'
    ))) { return $false }
    if (-not (Test-SwitchDeskInteger $Report.schema_version 1 1) -or
        $Report.platform -isnot [string] -or $Report.platform -cne 'windows' -or
        $Report.read_only -isnot [bool] -or
        $Report.read_only -ne $true -or
        -not (Test-SwitchDeskString $Report.os_version $true) -or
        -not (Test-SwitchDeskString $Report.architecture $true) -or
        -not (Test-SwitchDeskUtcTimestamp $Report.collected_at)) { return $false }
    if ($Report.displays -isnot [array] -or $Report.warnings -isnot [array]) { return $false }
    foreach ($display in $Report.displays) {
        if (-not (Test-SwitchDeskFields $display @(
            'id', 'name', 'builtin', 'width', 'height', 'refresh_hz', 'ddc_input'
        ))) { return $false }
        if (-not (Test-SwitchDeskString $display.id $true) -or
            -not (Test-SwitchDeskString $display.name $true) -or
            ($null -ne $display.builtin -and $display.builtin -isnot [bool]) -or
            -not (Test-SwitchDeskInteger $display.width 1 ([long]::MaxValue)) -or
            -not (Test-SwitchDeskInteger $display.height 1 ([long]::MaxValue))) { return $false }
        if ($null -ne $display.refresh_hz) {
            if (($display.refresh_hz -isnot [int] -and $display.refresh_hz -isnot [long] -and
                 $display.refresh_hz -isnot [double]) -or
                [double]::IsNaN([double]$display.refresh_hz) -or
                [double]::IsInfinity([double]$display.refresh_hz) -or
                [double]$display.refresh_hz -lt 0) { return $false }
        }
        $ddc = $display.ddc_input
        if (-not (Test-SwitchDeskFields $ddc @('status', 'value', 'detail')) -or
            $ddc.status -isnot [string] -or
            $ddc.status -cnotin @('not_tested', 'readable', 'unsupported', 'error') -or
            -not (Test-SwitchDeskString $ddc.detail)) { return $false }
        if ($ddc.status -ceq 'readable') {
            if (-not (Test-SwitchDeskInteger $ddc.value 0 65535)) { return $false }
        } elseif ($null -ne $ddc.value) { return $false }
    }
    if (-not (Test-SwitchDeskFields $Report.permissions @(
        'screen_capture', 'input_capture', 'input_injection'
    )) -or -not (Test-SwitchDeskFields $Report.capabilities @(
        'automatic_switch', 'native_file_paste', 'inactive_display_capture'
    ))) { return $false }
    foreach ($name in @('screen_capture', 'input_capture', 'input_injection')) {
        if ($Report.permissions.$name -isnot [string] -or
            $Report.permissions.$name -cnotin @(
            'granted', 'denied', 'not_checked', 'not_applicable'
        )) { return $false }
    }
    foreach ($name in @('automatic_switch', 'native_file_paste', 'inactive_display_capture')) {
        if ($Report.capabilities.$name -isnot [string] -or
            $Report.capabilities.$name -cne 'not_tested') { return $false }
    }
    foreach ($warning in $Report.warnings) {
        if (-not (Test-SwitchDeskString $warning)) { return $false }
    }
    return $true
}

function Stop-SwitchDeskWorker {
    param([Diagnostics.Process]$Process)
    try {
        if ($Process.HasExited) { return $true }
        $Process.Kill()
        return $Process.WaitForExit(1000)
    } catch {
        try { return $Process.HasExited } catch { return $false }
    }
}

function Invoke-SwitchDeskProbeSample {
    param(
        [string]$Executable,
        [string]$ProbePath,
        [int]$TimeoutMilliseconds
    )
    if ([string]::IsNullOrWhiteSpace($Executable) -or
        [string]::IsNullOrWhiteSpace($ProbePath) -or $TimeoutMilliseconds -lt 1) {
        throw 'Invalid worker executable, probe path, or timeout.'
    }
    $process = New-Object Diagnostics.Process
    $started = $false
    $outcome = $null
    $code = $null
    $report = $null
    try {
        $info = $process.StartInfo
        $info.FileName = $Executable
        $info.Arguments = '-NoLogo -NoProfile -NonInteractive -File "' +
            $ProbePath.Replace('"', '\"') + '" -ReadDdc'
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $info.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)
        if (-not $process.Start()) { throw 'Worker process did not start.' }
        $started = $true
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $finished = $process.WaitForExit($TimeoutMilliseconds)
        if (-not $finished) {
            $outcome = if (Stop-SwitchDeskWorker $process) { 'timeout' } else { 'cleanup_failed' }
        } else {
            $code = [int]$process.ExitCode
            $remaining = [Math]::Max(0, $TimeoutMilliseconds - [int]$clock.ElapsedMilliseconds)
            $outputReady = $stdoutTask.Wait($remaining)
            $remaining = [Math]::Max(0, $TimeoutMilliseconds - [int]$clock.ElapsedMilliseconds)
            $errorReady = $stderrTask.Wait($remaining)
            if ($code -ne 0) {
                $outcome = 'worker_error'
            } elseif (-not $outputReady -or -not $errorReady) {
                $outcome = 'invalid_report'
            } else {
                try {
                    $json = $stdoutTask.GetAwaiter().GetResult()
                    if ([string]::IsNullOrWhiteSpace($json) -or $json.TrimStart()[0] -cne '{') {
                        throw 'Worker JSON must be a top-level object.'
                    }
                    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) {
                        $candidate = ConvertFrom-Json -InputObject $json -DateKind String -ErrorAction Stop
                    } else {
                        $candidate = ConvertFrom-Json -InputObject $json -ErrorAction Stop
                    }
                    if (Test-SwitchDeskProbeReport $candidate) {
                        # PowerShell 7 converts ISO timestamps to DateTime; 5.1 leaves strings.
                        if ($candidate.collected_at -is [DateTime]) {
                            $candidate.collected_at = $candidate.collected_at.ToString(
                                'yyyy-MM-ddTHH:mm:ss.fffZ',
                                [Globalization.CultureInfo]::InvariantCulture
                            )
                        }
                        $report = $candidate
                        $outcome = 'ok'
                    } else { $outcome = 'invalid_report' }
                } catch { $outcome = 'invalid_report' }
            }
        }
    } finally {
        if ($started -and -not (Stop-SwitchDeskWorker $process)) {
            $outcome = 'cleanup_failed'
            $code = $null
            $report = $null
        }
        $process.Dispose()
    }
    if ($outcome -eq 'timeout' -or $outcome -eq 'cleanup_failed') {
        $code = $null
        $report = $null
    }
    if ($outcome -eq 'worker_error' -or $outcome -eq 'invalid_report') { $report = $null }
    return [pscustomobject]@{ outcome = $outcome; exit_code = $code; report = $report }
}

function Resolve-SwitchDeskOutputPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'OutputPath must be a nonempty file path.' }
    $provider = $null
    $drive = $null
    $destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
        $Path, [ref]$provider, [ref]$drive
    )
    if ($null -eq $provider -or $provider.Name -ne 'FileSystem') {
        throw 'OutputPath must resolve to a filesystem path.'
    }
    $parent = [IO.Path]::GetDirectoryName($destination)
    if (-not [IO.Directory]::Exists($parent)) {
        throw ('Output directory does not exist: ' + $parent)
    }
    if ([IO.File]::Exists($destination) -or [IO.Directory]::Exists($destination)) {
        throw ('Output path already exists: ' + $destination)
    }
    return $destination
}

function Save-SwitchDeskObservation {
    param([object]$Report, [string]$OutputPath)
    $destination = Resolve-SwitchDeskOutputPath $OutputPath
    $parent = [IO.Path]::GetDirectoryName($destination)
    $temporary = [IO.Path]::Combine(
        $parent, '.switchdesk-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    )
    $json = ConvertTo-Json -InputObject $Report -Depth 12 -Compress -ErrorAction Stop
    $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($json + "`n")
    $created = $false
    try {
        $stream = [IO.File]::Open(
            $temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None
        )
        $created = $true
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush()
        } finally { $stream.Dispose() }
        [IO.File]::Move($temporary, $destination)
    } finally {
        if ($created -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Invoke-SwitchDeskObservation {
    param(
        [string]$Executable,
        [string]$ProbePath,
        [int]$DurationSeconds,
        [int]$IntervalSeconds,
        [int]$ReadTimeoutSeconds
    )
    $samples = New-Object 'System.Collections.Generic.List[object]'
    $startedAt = [DateTime]::UtcNow.ToString(
        'yyyy-MM-ddTHH:mm:ss.fffZ', [Globalization.CultureInfo]::InvariantCulture
    )
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $durationMs = $DurationSeconds * 1000
    while ($clock.ElapsedMilliseconds -lt $durationMs -and $samples.Count -lt 256) {
        $startedMs = [long]$clock.ElapsedMilliseconds
        $remaining = [int]($durationMs - $startedMs)
        if ($remaining -lt 1) { break }
        $timeoutMs = [Math]::Min($ReadTimeoutSeconds * 1000, $remaining)
        $sample = Invoke-SwitchDeskProbeSample $Executable $ProbePath $timeoutMs
        $finishedMs = [long]$clock.ElapsedMilliseconds
        $samples.Add([ordered]@{
            started_ms = $startedMs
            finished_ms = $finishedMs
            outcome = $sample.outcome
            exit_code = $sample.exit_code
            report = $sample.report
        })
        if ($sample.outcome -eq 'cleanup_failed') { break }
        $remaining = [int]($durationMs - $clock.ElapsedMilliseconds)
        if ($remaining -gt 0) {
            [Threading.Thread]::Sleep([Math]::Min($IntervalSeconds * 1000, $remaining))
        }
    }
    return [ordered]@{
        schema_version = 1
        kind = 'input_observation'
        platform = 'windows'
        read_only = $true
        started_at = $startedAt
        requested_duration_seconds = $DurationSeconds
        elapsed_ms = [long]$clock.ElapsedMilliseconds
        samples = $samples.ToArray()
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
            throw 'This observation requires Windows 10 or Windows 11.'
        }
        $null = Resolve-SwitchDeskOutputPath $OutputPath
        [Console]::WriteLine('Keep the Windows picture for about 10 seconds.')
        [Console]::WriteLine('Use the monitor menu to switch to Mac; confirm its desktop for about 10 seconds.')
        [Console]::WriteLine('Switch back to Windows and wait for observation to finish.')
        $executable = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        $probe = Join-Path $PSScriptRoot 'diagnose.ps1'
        $observation = Invoke-SwitchDeskObservation `
            $executable $probe $DurationSeconds $IntervalSeconds $ReadTimeoutSeconds
        Save-SwitchDeskObservation $observation $OutputPath
        [Console]::WriteLine('Observation saved: ' + $OutputPath)
    } catch {
        [Console]::Error.WriteLine('SwitchDesk observation failed: ' + $_.Exception.Message)
        exit 1
    }
}
