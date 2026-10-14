#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version = '1.0.0',
    [string]$CompilerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sdkVersion = '10.0.402'
$runtimeVersion = '10.0.13'
$innoVersion = '6.7.3'

if ($env:OS -ne 'Windows_NT') {
    throw 'Generate the Windows installer on Windows.'
}
foreach ($component in $Version.Split('.')) {
    if ([long]$component -gt 65535) {
        throw 'Version components must be between 0 and 65535.'
    }
}

if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $CompilerPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
}
$compiler = Get-Item -LiteralPath $CompilerPath
if ($compiler.PSIsContainer -or $compiler.VersionInfo.ProductVersion -notmatch '^6\.7\.3(?:\D|$)') {
    throw "Install Inno Setup $innoVersion and specify its ISCC.exe with -CompilerPath."
}
$dotnet = (Get-Command dotnet -CommandType Application).Source

Push-Location -LiteralPath $PSScriptRoot
try {
    $actualSdk = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $actualSdk.Trim() -ne $sdkVersion) {
        throw "This procedure requires .NET SDK $sdkVersion; selected SDK: $actualSdk."
    }

    # A new directory keeps stale files out of the payload without deleting previous artifacts.
    $outputDirectory = Join-Path $PSScriptRoot ("obj\windows-installer\$Version\" + [guid]::NewGuid().ToString('N'))
    $publishDirectory = Join-Path $outputDirectory 'publish'
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    $project = Join-Path $PSScriptRoot 'src\LeRobocopier.Desktop\LeRobocopier.Desktop.csproj'
    $publishArguments = @(
        'publish', $project, '--configuration', 'Release', '--framework', 'net10.0',
        '--runtime', 'win-x64', '--self-contained', 'true', '--output', $publishDirectory,
        "-p:Version=$Version", "-p:AssemblyVersion=$Version.0", "-p:FileVersion=$Version.0",
        "-p:RuntimeFrameworkVersion=$runtimeVersion", '-p:TargetLatestRuntimePatch=false',
        '-p:UseAppHost=true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
        '-p:PublishReadyToRun=false'
    )
    & $dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    foreach ($file in @('LeRobocopier.Desktop.exe', 'LeRobocopier.Desktop.dll',
        'LeRobocopier.Core.dll', 'LeRobocopier.Desktop.deps.json',
        'LeRobocopier.Desktop.runtimeconfig.json', 'hostfxr.dll', 'hostpolicy.dll',
        'coreclr.dll', 'System.Private.CoreLib.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $file) -PathType Leaf)) {
            throw "The self-contained publication is missing $file."
        }
    }
    $runtimeConfig = Get-Content -Raw -LiteralPath (Join-Path $publishDirectory 'LeRobocopier.Desktop.runtimeconfig.json') | ConvertFrom-Json
    $includedRuntime = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' })
    if ($includedRuntime.Count -ne 1 -or $includedRuntime[0].version -ne $runtimeVersion) {
        throw "The publication must include Microsoft.NETCore.App $runtimeVersion."
    }

    $setupScript = Join-Path $PSScriptRoot 'LeRobocopier.Windows.iss'
    & $compiler.FullName "/DAppVersion=$Version" "/DPublishDir=$publishDirectory" "/DOutputDir=$outputDirectory" $setupScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE."
    }
    $installer = Join-Path $outputDirectory "LeRobocopier-$Version-win-x64-setup.exe"
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
        throw 'Inno Setup did not produce the expected installer.'
    }
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    "$hash  $([System.IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
    Write-Output "Installer: $installer"
    Write-Output "SHA-256: $hash"
}
finally {
    Pop-Location
}
