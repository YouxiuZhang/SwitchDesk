param([string]$ReferenceDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $ReferenceDirectory) { $ReferenceDirectory = Join-Path $root '.build/tools/net48/reference/build/.NETFramework/v4.8' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist/product/windows' }
[Reflection.Assembly]::LoadFrom((Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')) | Out-Null
$refs = New-Object 'System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]'
foreach ($name in @('mscorlib','System','System.Core','System.Drawing','System.Windows.Forms','System.Web.Extensions','System.Security','System.Runtime.Serialization','System.Xml')) {
    $path = Join-Path $ReferenceDirectory ($name + '.dll')
    if (-not [IO.File]::Exists($path)) { throw ('Missing reference ' + $path) }
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($path))
}
$sources = @((Join-Path $root 'native/windows/Probe.cs'), (Join-Path $root 'native/windows/app/MonitorControl.cs'),
    (Join-Path $root 'native/windows/desktop-assets/AssemblyInfo.cs')) +
    @(Get-ChildItem (Join-Path $root 'native/windows/product') -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$trees = New-Object 'System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]'
$parse = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp5)
foreach ($source in $sources) {
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($source),$parse,$source,[Text.Encoding]::UTF8))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::WindowsApplication).
    WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithPlatform([Microsoft.CodeAnalysis.Platform]::X64).WithDeterministic($true)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('SwitchDesk',$trees,$refs,$options)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$temporary = Join-Path $OutputDirectory ('.build-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$manifest = $null; $resources = $null; $pe = $null; $icon = $null
try {
    $manifest = [IO.File]::OpenRead((Join-Path $root 'native/windows/desktop-assets/app.manifest'))
    $icon = [IO.File]::OpenRead((Join-Path $root 'native/windows/desktop-assets/SwitchDesk.ico'))
    $resources = $compilation.CreateDefaultWin32Resources($true,$false,$manifest,$icon)
    $pe = [IO.File]::Create($temporary)
    $result = $compilation.Emit($pe,$null,$null,$resources)
    $pe.Dispose(); $pe=$null
    foreach ($d in $result.Diagnostics) {
        if ($d.Severity.ToString() -in @('Error','Warning')) { [Console]::Error.WriteLine($d.ToString()) }
    }
    if (-not $result.Success) { throw 'Windows desktop compilation failed' }
    [IO.File]::Copy($temporary,(Join-Path $OutputDirectory 'SwitchDesk.exe'),$true)
    foreach ($name in @('SwitchDesk.exe.config','README.zh-CN.md','SwitchDesk.ico','SwitchDesk.png')) {
        $source = Join-Path $root ('native/windows/desktop-assets/' + $name)
        if ([IO.File]::Exists($source)) { [IO.File]::Copy($source,(Join-Path $OutputDirectory $name),$true) }
    }
    Write-Output ('Built Windows desktop: ' + (Join-Path $OutputDirectory 'SwitchDesk.exe'))
} finally {
    if ($null -ne $pe) { $pe.Dispose() }; if ($null -ne $resources) { $resources.Dispose() }; if ($null -ne $manifest) { $manifest.Dispose() }
    if ($null -ne $icon) { $icon.Dispose() }
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
