# Starts Dave on MuMu with one click: the "dave" instance, button listener, microphone, fake GPS, and the app.
$mumu    = "C:\Program Files\Netease\MuMuPlayer\nx_main"
$manager = "$mumu\MuMuManager.exe"
$adb     = "$mumu\adb.exe"
$index   = 7   # the MuMu instance called "dave"

# MuMu has no GPS, so Dave gets a fake position. Utrecht Centraal; change these to your own place if you like.
$lat = "52.08945"
$lon = "5.10986"

Write-Host "Starting MuMu instance 'dave'..."
& $manager control -v $index launch | Out-Null
$waited = 0
do {
    Start-Sleep -Seconds 2
    $waited += 2
    $info = (& $manager info -v $index) | Out-String | ConvertFrom-Json
} until ($info.player_state -eq "start_finished" -or $waited -ge 180)
if ($info.player_state -ne "start_finished") {
    Write-Host "MuMu didn't finish starting. Try again, or start it by hand."
    Read-Host "Press Enter to close"
    exit 1
}

$device = "$($info.adb_host_ip):$($info.adb_port)"
& $adb connect $device | Out-Null
Start-Sleep -Seconds 3

function Send-Shell($command) { & $adb -s $device shell $command 2>&1 | Out-Null }

Write-Host "Turning on the button listener, microphone, voice and GPS..."
Send-Shell "settings put secure enabled_accessibility_services com.dave.carai/com.dave.carai.ButtonService"
Send-Shell "settings put secure accessibility_enabled 1"
Send-Shell "pm grant com.google.android.googlequicksearchbox android.permission.RECORD_AUDIO"
Send-Shell "settings put secure tts_default_synth com.google.android.tts"
Send-Shell "cmd appops set com.android.shell android:mock_location allow"
Send-Shell "cmd location providers add-test-provider gps"
Send-Shell "cmd location providers set-test-provider-enabled gps true"
Send-Shell "cmd location providers set-test-provider-location gps --location $lat,$lon"

# Dave ignores positions older than an hour, so keep the fake one fresh in the background while MuMu runs.
$refresh = @"
while (`$true) {
    Start-Sleep -Seconds 600
    `$out = & '$adb' -s $device shell cmd location providers set-test-provider-location gps --location $lat,$lon 2>&1
    if (`$LASTEXITCODE -ne 0) { break }
}
"@
Start-Process powershell -WindowStyle Hidden -ArgumentList "-NoProfile", "-Command", $refresh

& $manager control -v $index app launch -pkg com.dave.carai | Out-Null
Write-Host ""
Write-Host "Dave is ready. Press G or say 'Hey Dave'."
Start-Sleep -Seconds 4
