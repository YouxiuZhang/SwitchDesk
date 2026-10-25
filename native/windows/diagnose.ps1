param(
    [string]$OutputPath,
    [switch]$ReadDdc
)

$ErrorActionPreference = 'Stop'

try {
    # Guard before compiling or calling any Win32 entry point.
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'This diagnostic requires Windows 10 or Windows 11.'
    }

    $source = Join-Path $PSScriptRoot 'Probe.cs'
    if ($null -eq ('SwitchDesk.Native' -as [type])) {
        Add-Type -Path $source -ErrorAction Stop | Out-Null
    }

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64
    )
    try {
        $key = $baseKey.OpenSubKey('SOFTWARE\Microsoft\Windows NT\CurrentVersion')
        if ($null -eq $key) { throw 'Cannot read the Windows version registry key.' }
        try {
            $major = $key.GetValue('CurrentMajorVersionNumber', $null)
            $minor = $key.GetValue('CurrentMinorVersionNumber', $null)
            $build = $key.GetValue('CurrentBuildNumber', $null)
            $ubr = $key.GetValue('UBR', $null)
            if ($null -eq $major -or $null -eq $minor -or [string]::IsNullOrWhiteSpace([string]$build)) {
                throw 'Windows version registry values are incomplete.'
            }
            $osVersion = ('{0}.{1}.{2}' -f $major, $minor, $build)
            if ($null -ne $ubr) { $osVersion += ('.{0}' -f $ubr) }
        } finally {
            $key.Close()
        }
    } finally {
        $baseKey.Close()
    }

    $processor = $env:PROCESSOR_ARCHITEW6432
    if ([string]::IsNullOrWhiteSpace($processor)) { $processor = $env:PROCESSOR_ARCHITECTURE }
    if ([string]::IsNullOrWhiteSpace($processor)) {
        $processor = if ([Environment]::Is64BitOperatingSystem) { 'AMD64' } else { 'x86' }
    }
    switch ($processor.ToUpperInvariant()) {
        'AMD64' { $architecture = 'x64' }
        'ARM64' { $architecture = 'arm64' }
        'X86' { $architecture = 'x86' }
        default { $architecture = $processor }
    }

    $native = [SwitchDesk.Native]::Collect([bool]$ReadDdc)
    $displays = New-Object 'System.Collections.Generic.List[object]'
    foreach ($item in $native.Displays) {
        $displays.Add(@{
            id = $item.Id
            name = $item.Name
            builtin = $null
            width = $item.Width
            height = $item.Height
            refresh_hz = $item.RefreshHz
            ddc_input = @{
                status = $item.DdcInput.Status
                value = $item.DdcInput.Value
                detail = $item.DdcInput.Detail
            }
        })
    }

    $report = @{
        schema_version = 1
        platform = 'windows'
        os_version = $osVersion
        architecture = $architecture
        collected_at = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ', [Globalization.CultureInfo]::InvariantCulture)
        read_only = $true
        displays = $displays.ToArray()
        permissions = @{
            screen_capture = 'not_checked'
            input_capture = 'not_checked'
            input_injection = 'not_checked'
        }
        capabilities = @{
            automatic_switch = 'not_tested'
            native_file_paste = 'not_tested'
            inactive_display_capture = 'not_tested'
        }
        warnings = [string[]]$native.Warnings.ToArray()
    }

    $json = ConvertTo-Json -InputObject $report -Depth 8 -Compress -ErrorAction Stop
    $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($json + "`n")
    if ($PSBoundParameters.ContainsKey('OutputPath')) {
        if ([string]::IsNullOrWhiteSpace($OutputPath)) { throw 'OutputPath must be a nonempty file path.' }
        $provider = $null
        $drive = $null
        $destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath(
            $OutputPath, [ref]$provider, [ref]$drive
        )
        if ($null -eq $provider -or $provider.Name -ne 'FileSystem') {
            throw 'OutputPath must resolve to a filesystem path.'
        }
        $parent = [IO.Path]::GetDirectoryName($destination)
        if (-not [IO.Directory]::Exists($parent)) { throw ('Output directory does not exist: ' + $parent) }
        if ([IO.File]::Exists($destination) -or [IO.Directory]::Exists($destination)) {
            throw ('Output path already exists: ' + $destination)
        }
        $temporary = [IO.Path]::Combine($parent, '.switchdesk-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        $created = $false
        try {
            $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $created = $true
            try {
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush()
            } finally {
                $stream.Dispose()
            }
            [IO.File]::Move($temporary, $destination)
        } finally {
            if ($created -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        }
    } else {
        $stdout = [Console]::OpenStandardOutput()
        $stdout.Write($bytes, 0, $bytes.Length)
        $stdout.Flush()
    }
} catch {
    [Console]::Error.WriteLine('SwitchDesk diagnostic failed: ' + $_.Exception.Message)
    exit 1
}
