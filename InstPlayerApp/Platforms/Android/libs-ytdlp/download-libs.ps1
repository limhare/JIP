# yt-dlp(youtubedl-android) 의존성 다운로드 — 저장소에 넣기엔 커서(최대 133MB) 빌드 전 1회 실행
# 실행: powershell -File download-libs.ps1
$files = @(
    "https://repo1.maven.org/maven2/io/github/junkfood02/youtubedl-android/library/0.18.1/library-0.18.1.aar",
    "https://repo1.maven.org/maven2/io/github/junkfood02/youtubedl-android/ffmpeg/0.18.1/ffmpeg-0.18.1.aar",
    "https://repo1.maven.org/maven2/io/github/junkfood02/youtubedl-android/common/0.18.1/common-0.18.1.aar",
    "https://repo1.maven.org/maven2/com/fasterxml/jackson/core/jackson-databind/2.11.1/jackson-databind-2.11.1.jar",
    "https://repo1.maven.org/maven2/com/fasterxml/jackson/core/jackson-annotations/2.11.1/jackson-annotations-2.11.1.jar",
    "https://repo1.maven.org/maven2/com/fasterxml/jackson/core/jackson-core/2.11.1/jackson-core-2.11.1.jar",
    "https://repo1.maven.org/maven2/commons-io/commons-io/2.5/commons-io-2.5.jar",
    "https://repo1.maven.org/maven2/org/apache/commons/commons-compress/1.12/commons-compress-1.12.jar"
)
foreach ($u in $files) {
    $n = Split-Path $u -Leaf
    $dest = Join-Path $PSScriptRoot $n
    if (Test-Path $dest) { "skip: $n"; continue }
    "download: $n"
    Invoke-WebRequest $u -OutFile $dest
}
"done"
