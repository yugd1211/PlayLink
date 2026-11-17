# 가상 기기를 정상 종료합니다(데이터가 디스크에 써지도록 콘솔 kill 을 먼저 쓰고, 20초 안에 안 끝나면 강제 종료).
$Serial = 'emulator-5554'
$Adb = 'C:\Program Files\Unity\Hub\Editor\2022.3.62f2\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
if ((& $Adb devices) -match "^$Serial\s+device") { & $Adb -s $Serial shell sync | Out-Null; & $Adb -s $Serial emu kill | Out-Null }
$t0 = Get-Date
while ((Get-Process qemu-system-x86_64* -ErrorAction SilentlyContinue) -and ((Get-Date) - $t0).TotalSeconds -lt 20) { Start-Sleep 1 }
$q = Get-Process qemu-system-x86_64* -ErrorAction SilentlyContinue
if ($q) { $q | Stop-Process -Force; Write-Output 'qemu 강제 종료' } else { Write-Output 'qemu 정상 종료' }
Get-Process emulator -ErrorAction SilentlyContinue | Stop-Process -Force
