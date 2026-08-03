# JIP (Just InstPlayer) — Windows 버전

WPF(.NET 8) 기반 Windows 데스크탑 버전. 배포본(exe)만 남아있던 소스를 디컴파일로 복원한 뒤,
안드로이드 앱(InstPlayerApp)의 최신 기능을 이식한 버전이다.

## 빌드

```powershell
dotnet build WindowsApp\instplayer.csproj -c Release
# 출력: WindowsApp\bin\Release\net8.0-windows\instplayer.exe
```

.NET 8 SDK 필요. (이 PC에는 `C:\Users\Administrator\.dotnet`에 설치됨 —
`%USERPROFILE%\.dotnet\dotnet.exe build ...` 로 실행)

## 외부 도구 (실행 폴더에 자동 복사됨)

| 파일 | 용도 | 출처 |
|------|------|------|
| `yt-dlp.exe` | YouTube 다운로드 | libs/ |
| `rubberband-r3.exe` + `sndfile.dll` | HQ 내보내기 (Rubber Band R3) | breakfastquay.com 3.3.0 GPL |
| `libmp3lame.32/64.dll` | MP3 인코딩 (NAudio.Lame) | libs/ |

## AI 반주 추출 (Demucs)

Python demucs를 사용한다. 설치 위치: `T:\demucs-env`

```powershell
# 설치 (최초 1회)
%LOCALAPPDATA%\Programs\Python\Python311\python.exe -m venv T:\demucs-env
T:\demucs-env\Scripts\pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu121
T:\demucs-env\Scripts\pip install demucs
```

- 앱은 `T:\demucs-env\Scripts\demucs.exe` (또는 실행폴더의 `demucs-env\Scripts\demucs.exe`)를 찾아 실행
- 모델(htdemucs, 약 80MB)은 최초 실행 시 자동 다운로드 → `T:\demucs-env\torch-cache`
- GTX 1660 Ti에서 CUDA 가속으로 동작

## 안드로이드 앱과의 기능 대응 (2026-08 이식)

| 기능 | 안드로이드 | Windows |
|------|-----------|---------|
| HQ 내보내기 | librb_export.so (JNI) | rubberband-r3.exe CLI + LAME 320k |
| 빠른 드래그 탐색 | FastDragTouchListener | 기존 구현이 이미 동등 (드래그 중 실시간 탐색) |
| 클립보드 YouTube 감지 | MainPage OnAppearing | Window Activated 이벤트 |
| 다운로드 파일 정리 | DownloadManagerPage | 🗑 버튼 (앱이 받은 파일만 추적/삭제) |
| AI 반주 추출 | demucs.cpp (ggml, JNI) | Python demucs (htdemucs, CUDA) |

Windows에만 있는 기능: 라이브러리 폴더 감시/검색, 녹음(마이크/루프백/믹스), VU 미터.

## 소스 구조

- `MainWindow.cs` — 전체 로직 (약 3,200줄, 디컴파일 복원본 + 이식 기능)
- `MainWindow.xaml` — UI (BAML 디컴파일 복원본)
- `AppSettings.cs` — 설정 직렬화 (`%APPDATA%\InstPlayer\settings.json`)
- `libs\` — 외부 바이너리 의존성
