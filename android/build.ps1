# PlayLink 앱 빌드(Windows PowerShell). Unity 에디터의 Android 모듈에 든 JDK 17 + Gradle 런처를 씁니다(따로 내려받는 도구 없음).
# 사용: .\build.ps1                → proto 빌드(배포·측정용, 디버그 불가, 진단 켬) + 단위 시험 + dist\ 로 복사
#       .\build.ps1 -Publish       → 위에 더해 Unity 도구의 App~ 폴더로 복사(tools\publish-to-unity.ps1)
#       .\build.ps1 assembleDebug  → 인자를 그대로 gradle 에 넘김
# 버전은 app\build.gradle.kts 의 appVersionCode / appVersionName 한 곳에서 읽습니다.
$ErrorActionPreference = 'Stop'
$publish = $false
$gradleArgs = @()
foreach ($a in $args) { if ($a -eq '-Publish') { $publish = $true } else { $gradleArgs += $a } }
# 도구 위치: 환경 변수 PLAYLINK_JDK(JDK 17 폴더), PLAYLINK_GRADLE_JAR(gradle-launcher jar), PLAYLINK_SDK(Android SDK 폴더)로 정합니다.
# 없으면 Unity Hub 로 설치한 에디터 가운데 Android 모듈(JDK + Gradle 8.13 이상)이 든 가장 새 것을 찾아 씁니다.
$U = $null; $found = $null
foreach ($d in (Get-ChildItem 'C:\Program Files\Unity\Hub\Editor' -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)) {
  $a = Join-Path $d.FullName 'Editor\Data\PlaybackEngines\AndroidPlayer'
  $j = Get-ChildItem (Join-Path $a 'Tools\gradle\lib') -Filter 'gradle-launcher-*.jar' -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($j -and (Test-Path "$a\OpenJDK\bin\java.exe") -and $j.Name -match 'gradle-launcher-(\d+)\.(\d+)' -and ([int]$Matches[1] -gt 8 -or ([int]$Matches[1] -eq 8 -and [int]$Matches[2] -ge 13))) { $U = $a; $found = $j.FullName; break }
}
$env:JAVA_HOME = if ($env:PLAYLINK_JDK) { $env:PLAYLINK_JDK } else { "$U\OpenJDK" }
$gradleJar = if ($env:PLAYLINK_GRADLE_JAR) { $env:PLAYLINK_GRADLE_JAR } else { $found }
foreach ($p in @("$env:JAVA_HOME\bin\java.exe", $gradleJar)) { if (-not $p -or -not (Test-Path $p)) { throw "빌드 도구가 없습니다: $p (PLAYLINK_JDK / PLAYLINK_GRADLE_JAR 로 위치를 지정하세요)" } }
$sdk = if ($env:PLAYLINK_SDK) { $env:PLAYLINK_SDK } elseif ($U -and (Test-Path "$U\SDK\platforms\android-36")) { "$U\SDK" } else { $null }
if ($sdk) { [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'local.properties'), 'sdk.dir=' + $sdk.Replace('\', '/') + "`n") }
elseif (-not (Test-Path (Join-Path $PSScriptRoot 'local.properties')) -and -not $env:ANDROID_HOME) { throw 'Android SDK 위치를 모릅니다: PLAYLINK_SDK 또는 ANDROID_HOME 을 지정하세요' }
Set-Location $PSScriptRoot
$custom = $gradleArgs.Count -gt 0
if (-not $custom) { $gradleArgs = @('testProtoUnitTest', 'assembleProto') }
& "$env:JAVA_HOME\bin\java.exe" -cp $gradleJar org.gradle.launcher.GradleMain -p . --console=plain @gradleArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not $custom) {
  $gradle = Get-Content 'app\build.gradle.kts' -Raw -Encoding UTF8
  if ($gradle -notmatch 'val appVersionCode = (\d+)') { throw 'appVersionCode 를 찾지 못했습니다' }
  $code = [int]$Matches[1]
  if ($gradle -notmatch 'val appVersionName = "([^"]+)"') { throw 'appVersionName 을 찾지 못했습니다' }
  $name = $Matches[1]
  $apk = 'app\build\outputs\apk\proto\app-proto.apk'
  $dist = Join-Path $PSScriptRoot '..\dist'
  New-Item -ItemType Directory -Force $dist | Out-Null
  Get-ChildItem $dist -Filter 'PlayLink-*.apk' | Remove-Item -Force      # dist 에는 최신 APK 하나만 둡니다
  $file = "PlayLink-$name.apk"
  Copy-Item $apk "$dist\$file" -Force
  $f = Get-Item "$dist\$file"
  $sha = (Get-FileHash $f -Algorithm SHA256).Hash.ToLower()
  $json = '{"package":"io.playlink.app","versionName":"' + $name + '","versionCode":' + $code + ',"build":"proto","proto":2,"auth":1,"minSdk":26,"file":"' + $file + '","size":' + $f.Length + ',"sha256":"' + $sha + '"}'
  [IO.File]::WriteAllText("$dist\app.json", $json)
  Write-Host "dist\$file  $($f.Length) bytes  sha256 $sha"
  if ($publish) { & (Join-Path $PSScriptRoot '..\tools\publish-to-unity.ps1') }
}
