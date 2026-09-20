# 127c-encoder

<img width="1700" height="1574" alt="image" src="https://github.com/user-attachments/assets/0ad3f053-aa24-48a1-a9d3-d2c994e310ab" />


1227 Cloud용 비디오 인코딩 UI 클라이언트

## 기능

- 비디오 파일 큐
  - 파일 선택 또는 드래그 앤 드롭으로 여러 파일 추가
  - 추가 순서대로 순차 인코딩
  - 선택 제거 / 모두 제거 / 위·아래 순서 변경
  - 파일명, 파일 크기, 상태 표시
  - 완료: 옅은 초록색 / 오류·강제 중지: 옅은 빨간색
  - 재시작 시 완료 파일은 건너뛰고 나머지 파일부터 처리
  - `중지하기`를 누르면 실행 중인 FFmpeg 프로세스를 즉시 종료
- 출력 폴더 편집
  - 기본값: 프로그램 실행 위치의 `./encoded`
- 인코딩 프리셋
  - 비디오: H.264 (`libx264`) 고정
  - x264 튜닝: `animation` 고정
  - `fast` / `medium` / `slow` 프리셋
  - 최대 비트레이트 / VBV 버퍼 크기
  - 품질: `CRF 28`, 프로필: High@Level 4.0 고정
  - 기본: HE-AAC v1 스테레오 `64k` (`fdkaac -p 5 -b 64`)
  - 절약: HE-AAC v2 스테레오 `32k` (`fdkaac -p 29 -b 32`)
  - 오디오 게인: dB 단위, 기본 `0`
  - 다이내믹 노멀라이징: 필요할 때만 선택
- 결과 파일: `<입력 파일명>.mp4`
- fdkaac 자동 준비
  - 인코딩 시 필요한 바이너리를 확인하고 없으면 자동 다운로드
  - 프로그램 실행 경로의 `./fdkaac/<os>-<arch>/`에 설치
  - 127c-encoder의 `deps-fdkaac-v1` Release asset 사용
  - GitHub 제공 SHA-256 digest 검증 후 실행
- FFmpeg 자동 준비
  - 시작 시 기존 설치를 검사하고, 없으면 `FFmpeg 다운로드` 버튼을 표시
  - 설치가 끝날 때까지 `인코딩 시작` 버튼은 비활성화
  - 프로그램 실행 경로의 `./ffmpeg/<os>-<arch>/`에 설치
  - 지원: Windows / Linux / macOS, x64 / ARM64
  - 설치 파일 SHA-256 검증 및 `libx264` 인코더 확인
  - Windows·Linux: BtbN 최신 안정 브랜치 GPL 빌드
  - macOS: 고정된 FFmpeg 8.1.2 GPL 빌드와 SHA-256 매니페스트

## 실행

FFmpeg와 fdkaac 바이너리는 프로그램이 직접 관리합니다. 최초 준비 시 인터넷 연결과 프로그램 실행 경로의 쓰기 권한이 필요합니다.

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
- `Fdkaac/*`: 플랫폼 판별, Release 조회, 다운로드, SHA-256 검증, 설치 및 실행 검증
- `Encoding/Validation`: 입력 파일·출력 경로·코덱·프리셋·비트레이트 검증
- `Encoding/Arguments`: FFmpeg CLI 인자 생성
- `Encoding/Services`: 출력 폴더 생성 및 FFmpeg 인코딩 프로세스 실행
- `MainWindow`: 서비스 호출 결과·설치 진행·FFmpeg 실행(인코딩) 로그 표시

## 출력 파일명

- 기본: `[127c]원본파일명.mp4`
- 같은 파일명이 있으면: `[127c]원본파일명 (1).mp4`, `[127c]원본파일명 (2).mp4`, ... 순으로 번호 추가

## 기본 인코딩 파이프라인

```text
입력
├─ FFmpeg → H.264 비디오
└─ FFmpeg → PCM/CAF pipe → fdkaac
                           ├─ 기본: HE-AAC v1 64k
                           └─ 절약: HE-AAC v2 32k
                                      ↓
                         FFmpeg stream-copy remux
                                      ↓
                                  최종 MP4
```

