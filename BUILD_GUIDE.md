# JIP (InstPlayer) 빌드 & 배포 가이드

## 빌드

```bash
# 항상 Release로 빌드할 것 (Debug로 빌드하면 서명 키가 달라져 앱 데이터 초기화됨)
dotnet build -c Release -f net9.0-android -p:AndroidSdkDirectory="$ANDROID_HOME"
```

출력 APK: `InstPlayerApp/bin/Release/net9.0-android/com.instplayer.app-Signed.apk`

## ADB 무선 연결

```bash
# 1. 태블릿: 설정 → 개발자 옵션 → 무선 디버깅 → 페어링 코드로 기기 페어링
adb pair <IP>:<페어링포트> <페어링코드>

# 2. 연결 (포트는 무선 디버깅 화면에 표시됨, 페어링 포트와 다름)
adb connect <IP>:<연결포트>

# 3. 연결 확인
adb devices
```

## 설치

```bash
# 기기가 하나만 연결된 경우
adb install -r InstPlayerApp/bin/Release/net9.0-android/com.instplayer.app-Signed.apk

# 여러 기기가 보이는 경우 -s 옵션으로 지정
adb -s <IP>:<포트> install -r InstPlayerApp/bin/Release/net9.0-android/com.instplayer.app-Signed.apk
```

## 주의사항

- **반드시 Release로 빌드** — Debug↔Release 전환 시 서명 키가 달라져 Android가 기존 앱을 제거 후 재설치함. 이 경우 가사, 설정, 다운로드 파일 등 앱 내부 데이터가 모두 삭제됨.
- `adb install -r` 옵션은 기존 데이터를 유지하면서 업데이트 설치 (동일 서명 키일 때만 동작).
- ADB 무선 연결 포트는 태블릿 재부팅/재연결 시 변경됨.
