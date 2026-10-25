param(
    [string]$ReferenceDirectory,
    [string]$OutputDirectory,
    [string[]]$SourceFiles
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($ReferenceDirectory)) {
    $ReferenceDirectory = Join-Path $root '.build/tools/net48/reference/build/.NETFramework/v4.8'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'dist/windows-app'
}
if (-not $SourceFiles) {
    $SourceFiles = @((Join-Path $root 'native/windows/Probe.cs')) +
        @(Get-ChildItem -LiteralPath (Join-Path $root 'native/windows/app') -Filter '*.cs' |
            Sort-Object Name | ForEach-Object { $_.FullName })
}

[Reflection.Assembly]::LoadFrom((Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')) | Out-Null
$references = New-Object 'System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]'
foreach ($name in @('mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms',
                     'System.Web.Extensions', 'System.Runtime.Serialization', 'System.Xml')) {
    $path = Join-Path $ReferenceDirectory ($name + '.dll')
    if (-not [IO.File]::Exists($path)) { throw ('Missing Microsoft .NET 4.8 reference: ' + $path) }
    $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($path))
}
$trees = New-Object 'System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]'
$parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion(
    [Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp5
)
foreach ($path in $SourceFiles) {
    $absolute = [IO.Path]::GetFullPath($path)
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
        [IO.File]::ReadAllText($absolute), $parseOptions, $absolute, [Text.Encoding]::UTF8
    ))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new(
    [Microsoft.CodeAnalysis.OutputKind]::WindowsApplication
).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).
    WithPlatform([Microsoft.CodeAnalysis.Platform]::X64).
    WithDeterministic($true)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(
    'SwitchDesk', $trees, $references, $options
)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$output = Join-Path $OutputDirectory 'SwitchDesk.exe'
$temporary = Join-Path $OutputDirectory ('.build-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$pe = $null
$resources = $null
$manifest = $null
try {
    $manifestPath = Join-Path $root 'native/windows/app/app.manifest'
    if ([IO.File]::Exists($manifestPath)) { $manifest = [IO.File]::OpenRead($manifestPath) }
    $resources = $compilation.CreateDefaultWin32Resources($true, $false, $manifest, $null)
    $pe = [IO.File]::Create($temporary)
    $result = $compilation.Emit($pe, $null, $null, $resources)
    $pe.Dispose()
    $pe = $null
    if (-not $result.Success) {
        foreach ($diagnostic in $result.Diagnostics) {
            [Console]::Error.WriteLine($diagnostic.ToString())
        }
        throw 'Windows app compilation failed.'
    }
    [IO.File]::Copy($temporary, $output, $true)
    foreach ($name in @('SwitchDesk.exe.config', 'README.zh-CN.md')) {
        $source = Join-Path $root ('native/windows/app/' + $name)
        if ([IO.File]::Exists($source)) { [IO.File]::Copy($source, (Join-Path $OutputDirectory $name), $true) }
    }
    Write-Output ('Built Windows x64 .NET Framework 4.8 app: ' + $output)
} finally {
    if ($null -ne $pe) { $pe.Dispose() }
    if ($null -ne $resources) { $resources.Dispose() }
    if ($null -ne $manifest) { $manifest.Dispose() }
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
