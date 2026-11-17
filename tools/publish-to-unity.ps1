# dist\ 의 APK 와 app.json 을 Unity 도구가 내주는 자리(App~)로 복사합니다. 몇 번을 돌려도 같은 결과입니다.
#   dist\PlayLink-<버전>.apk → <도구 폴더>\App~\PlayLink.apk
#   dist\app.json                   → <도구 폴더>\App~\app.json   (versionCode, versionName 을 서버가 읽음)
# 사용: .\tools\publish-to-unity.ps1 [-ToolFolder <Unity 프로젝트의 PlayLink 폴더>]
# App~ 는 이름 끝의 ~ 때문에 Unity 가 임포트하지 않으므로 복사해도 재컴파일이 일어나지 않고, 서버는 다음 요청부터 새 파일을 내줍니다(app.json 은 1초 안에 다시 읽음).
param([string]$ToolFolder = (Join-Path $PSScriptRoot '..\unity\io.playlink\Editor'))
$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot '..\dist'
$info = Get-Content (Join-Path $dist 'app.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$src = Join-Path $dist $info.file
if (-not (Test-Path $src)) { throw "APK 가 없습니다: $src (먼저 android\build.ps1)" }
$sha = (Get-FileHash $src -Algorithm SHA256).Hash.ToLower()
if ($sha -ne $info.sha256) { throw "dist 의 APK 와 app.json 의 sha256 이 다릅니다. android\build.ps1 을 다시 돌리세요." }
if (-not (Test-Path (Join-Path $ToolFolder 'PlayLinkHost.cs'))) { throw "Unity 도구 폴더가 아닙니다: $ToolFolder" }
$app = Join-Path $ToolFolder 'App~'
New-Item -ItemType Directory -Force $app | Out-Null
Copy-Item $src (Join-Path $app 'PlayLink.apk') -Force
Copy-Item (Join-Path $dist 'app.json') (Join-Path $app 'app.json') -Force
$out = (Get-FileHash (Join-Path $app 'PlayLink.apk') -Algorithm SHA256).Hash.ToLower()
if ($out -ne $sha) { throw '복사한 APK 의 sha256 이 다릅니다.' }
Write-Host "App~\PlayLink.apk  $($info.versionName) ($($info.versionCode))  $((Get-Item $src).Length) bytes  sha256 $sha"
