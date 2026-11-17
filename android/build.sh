#!/bin/bash
# 내부용: build.ps1 을 그대로 부릅니다(도구 위치는 build.ps1 이 찾습니다). 사용: ./build.sh [gradle 인자]
powershell -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$(dirname "$0")/build.ps1")" "$@"
