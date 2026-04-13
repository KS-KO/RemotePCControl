# RemotePCControl

## Quick Start
- 앱 빌드:
  - `dotnet build RemotePCControl.App\RemotePCControl.App.csproj -c Debug -p:Platform=x64 --no-restore`
- 릴레이 서버 빌드:
  - `dotnet build RemotePCControl.RelayServer\RemotePCControl.RelayServer.csproj -c Debug --no-restore`
- 앱 실행:
  - `RemotePCControl.App\bin\Debug\RemotePCControl.App.exe`
- 릴레이 서버 실행:
  - `RemotePCControl.RelayServer\bin\Debug\net9.0\RemotePCControl.RelayServer.exe`

## Current Validation Focus
- 자동 재연결 상태 전이와 종료 사유 분리
- Local -> Public -> Relay 자동 fallback 경로 확인
- Relay duplicate / invalid / expired code 처리 확인
- 인증서 지문 유지 및 불일치 차단 확인

상세 QA 절차와 기록 템플릿은 `DOC/RESILIENCE_REVIEW.md`를 기준으로 관리합니다.

