# adb 가 unauthorized 일 때만 씁니다: 가상 기기 화면의 "Allow USB debugging?" 창에서
# [Always allow from this computer] 와 [Allow] 를 에뮬레이터 콘솔(127.0.0.1:5554)의 터치 이벤트로 누릅니다.
# adb 입력은 인증 전이라 못 쓰고, 콘솔의 'event mouse' 는 이 이미지에서 먹지 않아 멀티터치 이벤트를 보냅니다.
# 좌표는 1080x2400 화면, 영어 UI 기준(2026-11-14 확인). 화면이 다르면 -ShotDir 로 찍어 보고 좌표를 바꾸세요.
param([int]$Port = 5554, [string]$ShotDir = '', [int[]]$Check = @(148, 1233), [int[]]$Allow = @(893, 1399), [switch]$ShotOnly)
$tok = (Get-Content "$env:USERPROFILE\.emulator_console_auth_token" -Raw).Trim()
$c = New-Object System.Net.Sockets.TcpClient('127.0.0.1', $Port); $s = $c.GetStream(); $buf = New-Object byte[] 65536
function ReadAll { Start-Sleep -Milliseconds 400; $o = ''; while ($s.DataAvailable) { $n = $s.Read($buf, 0, $buf.Length); $o += [Text.Encoding]::ASCII.GetString($buf, 0, $n); Start-Sleep -Milliseconds 100 }; $o }
function Send([string]$l) { $b = [Text.Encoding]::ASCII.GetBytes("$l`r`n"); $s.Write($b, 0, $b.Length); $s.Flush(); ReadAll | Out-Null }
function Tap([int]$x, [int]$y) {
    $X = [int]($x / 1080 * 32767); $Y = [int]($y / 2400 * 32767)
    Send "event send EV_ABS:ABS_MT_SLOT:0 EV_ABS:ABS_MT_TRACKING_ID:7 EV_ABS:ABS_MT_POSITION_X:$X EV_ABS:ABS_MT_POSITION_Y:$Y EV_ABS:ABS_MT_TOUCH_MAJOR:8 EV_ABS:ABS_MT_PRESSURE:512 EV_KEY:BTN_TOUCH:1 EV_SYN:0:0"
    Send "event send EV_ABS:ABS_MT_SLOT:0 EV_ABS:ABS_MT_TRACKING_ID:-1 EV_KEY:BTN_TOUCH:0 EV_SYN:0:0"
}
ReadAll | Out-Null; Send "auth $tok"
if ($ShotDir) { New-Item -ItemType Directory -Force $ShotDir | Out-Null; Send "screenrecord screenshot $ShotDir"; Start-Sleep 2 }
if (-not $ShotOnly) { Tap $Check[0] $Check[1]; Start-Sleep 1; Tap $Allow[0] $Allow[1] }
$c.Close()
