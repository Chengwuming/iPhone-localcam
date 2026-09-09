$ErrorActionPreference = 'Stop'
$deskcamRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$registrar = Join-Path $deskcamRoot 'VirtualCamera\LocalCamVirtualCameraRegistrar.exe'
$mediaSource = Join-Path $deskcamRoot 'VirtualCamera\LocalCamMediaSource.dll'
$app = Join-Path $deskcamRoot 'LocalCam.Desktop.exe'
if (!(Test-Path -LiteralPath $registrar) -or !(Test-Path -LiteralPath $mediaSource) -or !(Test-Path -LiteralPath $app)) {
    throw 'DeskCam package is incomplete. Extract the full package first.'
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $MyInvocation.MyCommand.Path + '"'
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait
    exit
}
& $registrar --install $mediaSource
if ($LASTEXITCODE -ne 0) { throw "Camera registration failed: $LASTEXITCODE" }
$rule = Get-NetFirewallRule -DisplayName 'DeskCam Local Camera' -ErrorAction SilentlyContinue
if (!$rule) {
    New-NetFirewallRule -DisplayName 'DeskCam Local Camera' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 29100,29101 -Program $app -Profile Any | Out-Null
}
Write-Host 'DeskCam installed. Close this window, then open Start-DeskCam.cmd.'
Write-Host 'Camera name in QQ / Camera / Chrome: LocalCam Camera'
Read-Host 'Press Enter to close'
