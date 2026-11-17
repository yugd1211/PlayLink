# 가상 기기(AVD Medium_Phone_API_35)를 창 없이 띄우고 adb 로 쓸 수 있을 때까지 기다립니다.
# 사용: powershell -NoProfile -ExecutionPolicy Bypass -File start-avd.ps1 [-Gpu host|swiftshader_indirect] [-KillHung]
# 이미 떠 있으면 다시 띄우지 않고 reverse 만 다시 겁니다. 자세한 설명: docs/04_device_lab.md
param(
    [string]$Gpu = 'host',
    [switch]$KillHung,
    [int]$TimeoutSec = 300
)
$ErrorActionPreference = 'Stop'
$Serial = 'emulator-5554'
$Avd = 'Medium_Phone_API_35'
$AvdDir = "$env:USERPROFILE\.android\avd\Medium_Phone.avd"
$EmuDir = "$env:LOCALAPPDATA\Android\Sdk\emulator"
$Adb = 'C:\Program Files\Unity\Hub\Editor\2022.3.62f2\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
$LogDir = "$env:TEMP\playlink-device-lab"
New-Item -ItemType Directory -Force $LogDir | Out-Null

function DeviceState { $l = (& $Adb devices) | Where-Object { $_ -match "^$Serial\s" }; if ($l) { ($l -split '\s+')[1] } else { '' } }

$state = DeviceState
$qemu = Get-Process qemu-system-x86_64* -ErrorAction SilentlyContinue
$startNew = $false
if ($state -ne 'device') {
    if ($qemu -and $KillHung) {
        $qemu | Stop-Process -Force; Get-Process emulator -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2; $qemu = $null
    }
    if ($qemu) { Write-Output "qemu 가 이미 떠 있습니다(pid $($qemu.Id -join ','), adb 상태 '$state'). 부팅이 끝나기를 기다립니다. 멈춘 것이면 -KillHung 으로 다시 실행하세요." }
    else { $startNew = $true }
}
if ($startNew) {
    # 죽은 인스턴스가 남긴 잠금(이게 있으면 "Running multiple emulators with the same AVD" 로 실패). AVD 데이터는 건드리지 않습니다.
    Remove-Item "$AvdDir\hardware-qemu.ini.lock" -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item "$AvdDir\multiinstance.lock" -Force -Confirm:$false -ErrorAction SilentlyContinue
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $emuArgs = @('-avd', $Avd, '-port', '5554', '-no-window', '-no-snapshot', '-no-boot-anim', '-no-audio', '-gpu', $Gpu)
    $p = Start-Process -FilePath "$EmuDir\emulator.exe" -ArgumentList $emuArgs -WorkingDirectory $EmuDir -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$LogDir\emulator-$stamp.out.log" -RedirectStandardError "$LogDir\emulator-$stamp.err.log"
    Write-Output "emulator.exe 시작(pid $($p.Id)), 로그 $LogDir\emulator-$stamp.out.log"
}

$t0 = Get-Date; $booted = $false; $unauthSince = $null
while (((Get-Date) - $t0).TotalSeconds -lt $TimeoutSec) {
    $state = DeviceState
    if ($state -eq 'device') {
        $b = (& $Adb -s $Serial shell getprop sys.boot_completed 2>$null) -join ''
        if ($b.Trim() -eq '1') { $booted = $true; break }
    } elseif ($state -eq 'unauthorized') {
        if (-not $unauthSince) { $unauthSince = Get-Date }
        if (((Get-Date) - $unauthSince).TotalSeconds -gt 120) { Write-Output "NOT READY: adb 가 계속 unauthorized 입니다. tools/device-lab/authorize-adb.ps1 을 실행하세요(04_device_lab.md 4절)."; exit 3 }
    }
    if (((Get-Date) - $t0).TotalSeconds -gt 20 -and -not (Get-Process qemu-system-x86_64* -ErrorAction SilentlyContinue)) { Write-Output "NOT READY: qemu 프로세스가 끝났습니다. 로그를 보세요: $LogDir"; exit 4 }
    Start-Sleep 3
}
if (-not $booted) { Write-Output "NOT READY: ${TimeoutSec}초 안에 부팅이 끝나지 않았습니다(adb 상태 '$state')."; exit 5 }

& $Adb -s $Serial reverse tcp:7300 tcp:7300 | Out-Null
& $Adb -s $Serial reverse tcp:7311 tcp:7311 | Out-Null
Write-Output ("READY serial=$Serial ({0:n0}초) reverse: {1}" -f ((Get-Date) - $t0).TotalSeconds, ((& $Adb -s $Serial reverse --list) -join '; '))
exit 0
