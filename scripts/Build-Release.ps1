[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$GitHubRepository,

    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')]
    [string]$Version = '0.1.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$nativeBuild = Join-Path $artifactsRoot 'native-build'
$nativeLive = Join-Path $artifactsRoot 'virtual-camera\live'
$publishDirectory = Join-Path $artifactsRoot 'publish\win-x64'
$releaseDirectory = Join-Path $artifactsRoot 'release'

function Reset-GeneratedDirectory([string]$Path) {
    $absolute = [System.IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside artifacts: $absolute"
    }
    if (Test-Path -LiteralPath $absolute) {
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
    New-Item -ItemType Directory -Path $absolute | Out-Null
}

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable failed with exit code $LASTEXITCODE"
    }
}

$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer (vswhere.exe) was not found.'
}
$visualStudio = (& $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath).Trim()
if (-not $visualStudio) {
    throw 'Visual Studio with MSBuild and C++ tools was not found.'
}
$msbuild = Join-Path $visualStudio 'MSBuild\Current\Bin\MSBuild.exe'
$cmake = Join-Path $visualStudio 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$innoCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $programFilesX86 'Inno Setup 6\ISCC.exe'),
    (Join-Path $programFiles 'Inno Setup 6\ISCC.exe')
)
$inno = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $inno) {
    throw 'Inno Setup 6 was not found.'
}

Reset-GeneratedDirectory $nativeBuild
Reset-GeneratedDirectory $publishDirectory
Reset-GeneratedDirectory $releaseDirectory
New-Item -ItemType Directory -Path $nativeLive -Force | Out-Null

$mediaProject = Join-Path $repositoryRoot 'src\LocalCam.VirtualCamera\vendor\Windows-Camera\Samples\VirtualCamera\VirtualCameraMediaSource\VirtualCameraMediaSource.vcxproj'
Invoke-Checked $msbuild @(
    $mediaProject,
    '/t:Restore',
    '/p:RestorePackagesConfig=true',
    '/m'
)
Invoke-Checked $msbuild @(
    $mediaProject,
    '/p:Configuration=Release',
    '/p:Platform=x64',
    '/m'
)
$mediaDll = Join-Path (Split-Path $mediaProject) 'x64\Release\VirtualCameraMediaSource.dll'
if (-not (Test-Path -LiteralPath $mediaDll)) {
    throw "Native Media Source output was not found: $mediaDll"
}
Copy-Item -LiteralPath $mediaDll -Destination (Join-Path $nativeLive 'LocalCamMediaSource.dll') -Force

$nativeSource = Join-Path $repositoryRoot 'src\LocalCam.VirtualCamera'
Invoke-Checked $cmake @('-S', $nativeSource, '-B', $nativeBuild, '-G', 'Visual Studio 17 2022', '-A', 'x64')
Invoke-Checked $cmake @('--build', $nativeBuild, '--config', 'Release', '--parallel')
$registrar = Join-Path $nativeBuild 'Release\LocalCamVirtualCameraRegistrar.exe'
$probe = Join-Path $nativeBuild 'Release\LocalCamVirtualCameraCapabilityProbe.exe'
foreach ($file in @($registrar, $probe)) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Native utility output was not found: $file"
    }
}
Copy-Item -LiteralPath $registrar -Destination (Join-Path $nativeLive 'LocalCamVirtualCameraRegistrar.exe') -Force
Copy-Item -LiteralPath $probe -Destination (Join-Path $nativeLive 'LocalCamVirtualCameraCapabilityProbe.exe') -Force

Push-Location $repositoryRoot
try {
    Invoke-Checked 'dotnet' @('build', '.\LocalCam.sln', '--configuration', 'Release')
    Invoke-Checked 'dotnet' @('run', '--project', '.\tests\LocalCam.SmokeTests\LocalCam.SmokeTests.csproj', '--configuration', 'Release', '--no-build')
    Invoke-Checked 'dotnet' @(
        'publish',
        '.\apps\desktop\LocalCam.Desktop\LocalCam.Desktop.csproj',
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--output', $publishDirectory,
        "/p:Version=$Version",
        "/p:GitHubRepository=$GitHubRepository",
        '/p:DebugType=None',
        '/p:DebugSymbols=false'
    )
    Invoke-Checked $inno @("/DAppVersion=$Version", '.\installer\LocalCam.iss')
}
finally {
    Pop-Location
}

$installer = Join-Path $releaseDirectory 'LocalCam-Setup-x64.exe'
if (-not (Test-Path -LiteralPath $installer)) {
    throw "Installer output was not found: $installer"
}
$hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
"Installer: $installer"
"SHA256: $($hash.Hash)"
