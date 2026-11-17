#!/bin/bash
# 내부용: Unity 6000.2.10f1 에 든 JDK 17 + Gradle 8.13 런처로 gradle 을 실행합니다. 사용: ./build.sh <gradle 인자>
cd "$(dirname "$0")"
U="C:\Program Files\Unity\Hub\Editor\6000.2.10f1\Editor\Data\PlaybackEngines\AndroidPlayer"
export JAVA_HOME="$U\OpenJDK"
"$JAVA_HOME/bin/java" -cp "$U\Tools\gradle\lib\gradle-launcher-8.13.jar" org.gradle.launcher.GradleMain -p . --console=plain "$@"
