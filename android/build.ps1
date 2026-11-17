# GameViewRemote 앱 빌드(Windows PowerShell). Unity 6000.2.10f1 에 든 JDK 17 + Gradle 8.13 런처를 씁니다(따로 내려받는 도구 없음).
# 사용: .\build.ps1                → proto 빌드(배포·측정용, 디버그 불가, 진단 켬) + 단위 시험 + dist\ 로 복사
#       .\build.ps1 -Publish       → 위에 더해 Unity 도구의 App~ 폴더로 복사(tools\publish-to-unity.ps1)
#       .\build.ps1 assembleDebug  → 인자를 그대로 gradle 에 넘김
# 버전은 app\build.gradle.kts 의 gvrVersionCode / gvrVersionName 한 곳에서 읽습니다.
$ErrorActionPreference = 'Stop'
$publish = $false
$gradleArgs = @()
foreach ($a in $args) { if ($a -eq '-Publish') { $publish = $true } else { $gradleArgs += $a } }
$U = 'C:\Program Files\Unity\Hub\Editor\6000.2.10f1\Editor\Data\PlaybackEngines\AndroidPlayer'
# 도구 위치는 환경 변수로 바꿀 수 있습니다(다른 PC, 코드 리뷰 M8): GVR_JDK(JDK 17 폴더), GVR_GRADLE_JAR(gradle-launcher jar), GVR_SDK(Android SDK 폴더)
$env:JAVA_HOME = if ($env:GVR_JDK) { $env:GVR_JDK } else { "$U\OpenJDK" }
$gradleJar = if ($env:GVR_GRADLE_JAR) { $env:GVR_GRADLE_JAR } else { "$U\Tools\gradle\lib\gradle-launcher-8.13.jar" }
foreach ($p in @("$env:JAVA_HOME\bin\java.exe", $gradleJar)) { if (-not (Test-Path $p)) { throw "빌드 도구가 없습니다: $p (GVR_JDK / GVR_GRADLE_JAR 로 위치를 지정하세요)" } }
if ($env:GVR_SDK) { [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'local.properties'), 'sdk.dir=' + $env:GVR_SDK.Replace('\', '/') + "`n") }
elseif (-not (Test-Path (Join-Path $PSScriptRoot 'local.properties')) -and -not $env:ANDROID_HOME) { throw 'Android SDK 위치를 모릅니다: GVR_SDK 또는 ANDROID_HOME 을 지정하세요' }
Set-Location $PSScriptRoot
$custom = $gradleArgs.Count -gt 0
if (-not $custom) { $gradleArgs = @('testProtoUnitTest', 'assembleProto') }
& "$env:JAVA_HOME\bin\java.exe" -cp $gradleJar org.gradle.launcher.GradleMain -p . --console=plain @gradleArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not $custom) {
  $gradle = Get-Content 'app\build.gradle.kts' -Raw -Encoding UTF8
  if ($gradle -notmatch 'val gvrVersionCode = (\d+)') { throw 'gvrVersionCode 를 찾지 못했습니다' }
  $code = [int]$Matches[1]
  if ($gradle -notmatch 'val gvrVersionName = "([^"]+)"') { throw 'gvrVersionName 을 찾지 못했습니다' }
  $name = $Matches[1]
  $apk = 'app\build\outputs\apk\proto\app-proto.apk'
  $dist = Join-Path $PSScriptRoot '..\dist'
  New-Item -ItemType Directory -Force $dist | Out-Null
  Get-ChildItem $dist -Filter 'GameViewRemote-*.apk' | Remove-Item -Force      # dist 에는 최신 APK 하나만 둡니다
  $file = "GameViewRemote-$name.apk"
  Copy-Item $apk "$dist\$file" -Force
  $f = Get-Item "$dist\$file"
  $sha = (Get-FileHash $f -Algorithm SHA256).Hash.ToLower()
  $json = '{"package":"kr.co.seeplay.gvr","versionName":"' + $name + '","versionCode":' + $code + ',"build":"proto","proto":2,"auth":1,"minSdk":26,"file":"' + $file + '","size":' + $f.Length + ',"sha256":"' + $sha + '"}'
  [IO.File]::WriteAllText("$dist\app.json", $json)
  Write-Host "dist\$file  $($f.Length) bytes  sha256 $sha"
  if ($publish) { & (Join-Path $PSScriptRoot '..\tools\publish-to-unity.ps1') }
}
