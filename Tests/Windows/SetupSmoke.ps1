# Runs only on the disposable Windows CI desktop, after building the Setup.
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:RUNNER_TEMP 'OfficeTouchBar-install-check'
$setup = (Resolve-Path 'artifacts/OfficeTouchBar-Setup.exe').Path
$userRoot = Join-Path $env:APPDATA 'OfficeTouchBar'
$settings = Join-Path $userRoot 'settings.json'
if ((Test-Path $root) -or (Test-Path $settings)) {
    throw 'Smoke test requires a fresh runner and must not replace an existing installation/settings.'
}
New-Item $userRoot -ItemType Directory -Force | Out-Null
'{"SettingsVersion":2,"StartWithWindows":false,"DuplicateDisplayOnStartup":false,"DeletePreviousDaysInk":false}' |
    Set-Content $settings -Encoding utf8
$settingsHash = (Get-FileHash $settings).Hash
$inkSentinel = Join-Path $userRoot 'Ink\setup-preservation-check.isf'
New-Item (Split-Path $inkSentinel) -ItemType Directory -Force | Out-Null
'setup test sentinel' | Set-Content $inkSentinel
$inkHash = (Get-FileHash $inkSentinel).Hash

function Install([string] $log) {
    $process = Start-Process $setup -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
        "/DIR=`"$root`"", "/LOG=`"$log`"")
    if ($process.ExitCode -ne 0) { throw "Setup failed: $($process.ExitCode)" }
    $version = (Get-Item (Join-Path $root 'OfficeTouchBar.exe')).VersionInfo.FileVersion
    if ($version -ne '0.3.21.0') { throw "Unexpected installed version: $version" }
    if ((Get-FileHash $settings).Hash -ne $settingsHash -or
        (Get-FileHash $inkSentinel).Hash -ne $inkHash) { throw 'Setup changed user settings or ink.' }
}
Install (Join-Path $PWD 'artifacts/setup-install.log')
Write-Output 'PASS Setup fresh installation and user data preservation'
'obsolete payload' | Set-Content (Join-Path $root 'obsolete-test.dll')
Add-Content (Join-Path $root 'installed-files.txt') 'obsolete-test.dll'
'keep this document' | Set-Content (Join-Path $root 'user-notes.txt')
Install (Join-Path $PWD 'artifacts/setup-upgrade.log')
if (Test-Path (Join-Path $root 'obsolete-test.dll')) { throw 'Upgrade left obsolete payload.' }
if (!(Test-Path (Join-Path $root 'user-notes.txt'))) { throw 'Upgrade removed an unlisted document.' }
Write-Output 'PASS Setup repeat installation, obsolete payload cleanup and document preservation'

$app = Start-Process (Join-Path $root 'OfficeTouchBar.exe') -PassThru
try {
    $started = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 300
        $app.Refresh()
        if ($app.HasExited) { throw "Installed application exited: $($app.ExitCode)" }
        $log = Join-Path $userRoot 'diagnostic.log'
        if ((Test-Path $log) -and (Select-String -Path $log -SimpleMatch 'application started version=0.3.21.0' -Quiet)) {
            $started = $true
            break
        }
    }
    if (!$started) { throw 'Installed application did not finish startup.' }
    Write-Output 'PASS installed self-contained application startup'
} finally {
    if (!$app.HasExited) { Stop-Process -Id $app.Id; $app.WaitForExit() }
}
$uninstall = Start-Process (Join-Path $root 'unins000.exe') -Wait -PassThru -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$PWD\artifacts\setup-uninstall.log`"")
if ($uninstall.ExitCode -ne 0 -or (Test-Path (Join-Path $root 'OfficeTouchBar.exe'))) {
    throw 'Uninstall did not remove the installed application.'
}
if (!(Test-Path $settings) -or (Get-FileHash $inkSentinel).Hash -ne $inkHash) {
    throw 'Uninstall removed user data.'
}
Write-Output 'PASS uninstall preserves user settings and saved ink'
