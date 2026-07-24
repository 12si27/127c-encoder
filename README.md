# 127c-encoder

1227 Cloud용 비디오 인코딩 UI 클라이언트

## 기능

- 비디오 입력 파일 선택
- 출력 폴더 편집
  - 기본값: 프로그램 실행 위치의 `./encoded`
- 인코딩 프리셋
  - 비디오: H.264 (`libx264`) 고정
  - x264 튜닝: `animation` 고정
  - `fast` / `medium` / `slow` 프리셋
  - 최대 비트레이트 / VBV 버퍼 크기
  - 품질: `CRF 28`, 프로필: High@Level 4.0 고정
  - 오디오: `libopus` 스테레오 `64k` 고정
  - 오디오 게인: dB 단위, 기본 `0`
  - 다이내믹 노멀라이징: 필요할 때만 선택
- 결과 파일: `<입력 파일명>_encoded.mp4`
- FFmpeg 자동 준비
  - 시작 시 기존 설치를 검사하고, 없으면 `FFmpeg 다운로드` 버튼을 표시
  - 설치가 끝날 때까지 `인코딩 시작` 버튼은 비활성화
  - 프로그램 실행 경로의 `./ffmpeg/<os>-<arch>/`에 설치
  - 지원: Windows / Linux / macOS, x64 / ARM64
  - 설치 파일 SHA-256 검증 및 `libx264`, `libopus` 인코더 확인
  - Windows·Linux: BtbN 최신 안정 브랜치 GPL 빌드
  - macOS: 고정된 FFmpeg 8.1.2 GPL 빌드와 SHA-256 매니페스트

## 실행

FFmpeg가 없으면 다운로드 버튼으로 빌드를 내려받으므로 인터넷 연결과 프로그램 실행 경로의 쓰기 권한이 필요합니다.

```bash
dotnet run
```

```bash
DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet watch
```

## 코드 구성

- `Ffmpeg/Platform`: OS·CPU 아키텍처 판별
- `Ffmpeg/Builds`: 플랫폼별 다운로드 URL 및 SHA-256 카탈로그 조회
- `Ffmpeg/Installation`: 다운로드, 무결성 확인, 압축 해제, 설치 교체
- `Ffmpeg/Validation`: 실행 파일, UI용 인코더 검증
- `Ffmpeg/Services`: 기존 설치 재사용 또는 설치 판단
- `Encoding/Validation`: 입력 파일·출력 경로·코덱·프리셋·비트레이트 검증
- `Encoding/Arguments`: FFmpeg CLI 인자 생성
- `Encoding/Services`: 출력 폴더 생성 및 FFmpeg 인코딩 프로세스 실행
- `MainWindow`: 서비스 호출 결과·설치 진행·FFmpeg 실행(인코딩) 로그 표시

## 기본 인코딩 파라미터

```bash
ffmpeg -hide_banner -y -i input.mp4 \
  -map 0:v:0 -map 0:a:0? \
  -c:v libx264 -preset fast -tune animation -profile:v high -level:v 4.0 -crf 28 \
  -maxrate 2000k -bufsize 4000k -vf bwdif=mode=send_frame:deint=interlaced -pix_fmt yuv420p -fps_mode vfr \
  -c:a libopus -b:a 64k -ac 2 -movflags +faststart output_encoded.mp4
```
