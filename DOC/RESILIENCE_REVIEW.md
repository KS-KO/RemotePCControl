# Resilience Review (회복탄력성 검토)

## 1. 개요
본 문서는 Remote PC Control 시스템의 회복탄력성(Resilience)을 강화하기 위한 검토 결과를 기록한다. 회복탄력성이란 외부 충격(네트워크 단절, 리소스 부족, 시스템 예외 등) 발생 시 서비스를 신속하게 복구하거나 최소 기능 상태를 유지할 수 있는 능력을 의미한다.

## 2. 현재 구현 상태
- **네트워크 재연결**: 기본적인 `Auto reconnect` 옵션이 존재하며, 단절 시 일정 횟수 재시도를 수행함.
- **리소스 모니터링**: 상태표시줄을 통해 실시간 CPU 및 메모리 사용량을 가시화함.
- **세션 상태 관리**: 감사 타임라인(Audit Timeline)을 통해 세션 시작/종료 이벤트를 추적함.
- **오류 안내**: 연결 실패 시 사용자에게 알림을 제공함.

## 3. 시나리오 분석 및 전략 (Scenario Analysis & Strategies)

### 3.1 네트워크 장애 및 자동 재연결
- **문제점**: 단순 반복 재연결은 네트워크 장기 단절 시 불필요한 리소스를 소모하거나 서버 부하를 가중시킬 수 있음.
- **전략**:
    - **지수 백오프(Exponential Backoff)** 적용: 재연결 시도 간격을 1s, 2s, 4s, 8s...와 같이 단계적으로 늘려 네트워크 안정화 대기.
    - **상태 전이 명확화**: `Disconnected` -> `Reconnecting` -> `Connected` / `Failed` 상태를 UI에 직관적으로 표시.
    - **최대 시도 한도 및 포기 정책**: 일정 시간(예: 5분) 이후에는 사용자에게 수동 연결을 유도.

### 3.2 성능 및 리소스 관리
- **문제점**: 고해상도 화면 전송 및 장시간 세션 유지 시 메모리 누수 또는 렌더링 지연 발생 가능성.
- **전략**:
    - **동적 QoS(Quality of Service)**: 네트워크 지연 시간(Latency)이 일정 수준 이상 증가하면 JPEG 압축률을 높이거나 프레임 속도(FPS)를 낮춰 전송량 감소.
    - **메모리 보호 정책**: `ArrayPool<T>` 및 `Span<T>`를 적극 활용하여 빈번한 버퍼 할당에 따른 GC 부하 및 LOH 단편화 방지.
    - **타임아웃 관리**: 무응답(Inactivity) 세션에 대한 자동 종료 또는 절전 모드 대응.

### 3.3 예외 처리 및 복구 (Error Handling & Recovery)
- **문제점**: 예상치 못한 프로세스 비정상 종료 시 현재 작업 중인 상태 손실.
- **전략**:
    - **Last Known Good Configuration (LKGC)**: 마지막으로 성공한 연결 정보를 안전하게 보관하여 재시작 시 빠른 복구 지원.
    - **구조화된 로깅(Structured Logging)**: 예외 발생 시 Call Stack뿐만 아니라 당시의 시스템 상태(CPU/Mem/Network)를 패키징하여 로그 기록.
    - **무중단 업데이트 고려**: 앱 업데이트 시 현재 세션을 유지하며 백업 인스턴스로 전환하는 메커니즘 검토.

## 4. 개선 로드맵 (Improvement Roadmap)
1.  **Phase 1 (Harden Reconnect)**: 지수 백오프 알고리즘 도입 및 재연결 UX 고도화.
2.  **Phase 2 (Dynamic Control)**: Latency 기반 동적 해상도/품질 조정 기능 구현.
3.  **Phase 3 (Stability Audit)**: 24시간 이상 장기 가동 테스트를 통한 메모리/핸들 누수 전수 검사.
4.  **Phase 4 (State Persistence)**: 비정상 종료 후 앱 재시작 시 이전 세션 자동 복구(Auto-Resume) 기능.

## 5. 검증 체크리스트 (Validation Checklist)

### 5.1 단기 네트워크 단절 후 자동 복구
- **사전 조건**:
    - 두 장치가 정상 연결된 상태여야 한다.
    - `Auto reconnect` 옵션이 활성화되어 있어야 한다.
- **절차**:
    1. 활성 세션 중 네트워크를 일시적으로 차단하거나 대상 프로세스를 짧게 재시작한다.
    2. Viewer UI와 Audit Timeline 로그를 관찰한다.
    3. 1~2회 재연결 시도 후 세션이 복구되는지 확인한다.
- **기대 결과**:
    - 상태가 `Disconnected` 이후 `Reconnecting`으로 전이되어야 한다.
    - 재연결 대기 시간이 1초, 2초, 4초 형태로 증가해야 한다.
    - 복구 성공 시 상태가 `Connected`로 돌아와야 한다.
    - 최근 연결 기록과 마지막 정상 연결 정보가 최신 시각으로 갱신되어야 한다.

### 5.2 장치 재탐색 실패 시 마지막 정상 엔드포인트 재사용
- **사전 조건**:
    - 직전에 정상 연결을 최소 1회 성공해 마지막 정상 연결 정보가 저장되어 있어야 한다.
- **절차**:
    1. 브로드캐스트 탐색이 실패하도록 네트워크 탐색만 제한한다.
    2. 기존 세션을 오류로 끊어 자동 재연결을 유도한다.
    3. 로그에서 `Discovery` 실패 이후 재연결이 계속되는지 확인한다.
- **기대 결과**:
    - 장치 재탐색 실패만으로 재연결 루프가 즉시 종료되면 안 된다.
    - 마지막 정상 주소/포트를 사용한 재연결 시도가 이어져야 한다.
    - 성공 시 로그에 `LastKnownGood` 기반 재연결 성공 이력이 남아야 한다.

### 5.3 재연결 최대 한도 초과
- **사전 조건**:
    - 대상 장치가 실제로 오프라인이거나 연결이 불가능한 상태여야 한다.
- **절차**:
    1. 활성 세션을 예외 상황으로 종료시킨다.
    2. 자동 재연결이 최대 횟수 또는 최대 시간 제한까지 진행되도록 둔다.
- **기대 결과**:
    - 상태가 최종적으로 `Failed`로 종료되어야 한다.
    - 상태 메시지에 수동 재연결 필요 안내가 포함되어야 한다.
    - Audit Timeline에 시도 횟수 초과 또는 시간 초과 사유가 남아야 한다.

### 5.4 사용자 수동 종료 시 자동 재연결 금지
- **사전 조건**:
    - 활성 세션이 연결된 상태여야 한다.
- **절차**:
    1. 사용자가 `Disconnect`를 직접 실행한다.
    2. 직후 20초 이상 상태와 로그를 관찰한다.
- **기대 결과**:
    - 상태는 `Idle`로 종료되어야 한다.
    - `Reconnect Attempt` 로그가 새로 생성되면 안 된다.
    - 재연결용 타이머나 백그라운드 루프가 남아 있지 않아야 한다.

### 5.5 앱 재시작 후 `Pre-approved device` 정책 일관성
- **사전 조건**:
    - 특정 장치를 즐겨찾기로 저장한 뒤 앱을 완전히 종료한다.
- **절차**:
    1. 앱을 재시작한다.
    2. 동일 장치를 `Pre-approved device` 정책으로 연결 시도한다.
    3. 이어서 즐겨찾기를 해제한 뒤 동일 시나리오를 다시 수행한다.
- **기대 결과**:
    - 앱 재시작 직후에도 저장된 즐겨찾기 장치는 승인되어야 한다.
    - 즐겨찾기 해제 후에는 동일 정책 연결이 거부되어야 한다.
    - 메모리 상태와 저장소 상태가 서로 불일치해도 최종 정책 판정은 저장 상태를 기준으로 일관되어야 한다.

## 6. 테스트 결과 기록 템플릿 (Test Result Template)

| Test ID | 검증 항목 | 테스트 일시 | 수행자 | 결과 (Pass/Fail/확인 필요) | 관찰된 상태 전이 | 로그 근거 | 이슈 및 후속 조치 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| RR-01 | 단기 네트워크 단절 후 자동 복구 |  |  |  | 예: `Disconnected -> Reconnecting -> Connected` | 예: `Reconnect Attempt`, `Reconnect Succeeded` |  |
| RR-02 | 장치 재탐색 실패 시 마지막 정상 엔드포인트 재사용 |  |  |  |  | 예: `Reconnect Failed`, `Reconnect Succeeded / Source=LastKnownGood` |  |
| RR-03 | 최대 재시도 또는 최대 시간 초과 시 `Failed` 종료 |  |  |  | 예: `Disconnected -> Reconnecting -> Failed` | 예: `Reconnect Exhausted` |  |
| RR-04 | 사용자 수동 종료 시 자동 재연결 금지 |  |  |  | 예: `Connected -> Idle` | 예: `Session Closed`, 재연결 로그 없음 |  |
| RR-05 | 앱 재시작 후 `Pre-approved device` 정책 일관성 |  |  |  | 예: `Approval denied` 또는 `Connected` | 예: `Connection Denied` 또는 `Session Connected` |  |

### 6.1 기록 작성 가이드
- `결과`는 `Pass`, `Fail`, `확인 필요` 중 하나로 고정한다.
- `관찰된 상태 전이`에는 UI 상단 세션 상태 기준의 실제 전이 순서를 기록한다.
- `로그 근거`에는 Audit Timeline에서 확인한 핵심 제목 또는 메시지 일부만 간단히 기록한다.
- `이슈 및 후속 조치`에는 재현 조건, 수정 필요 여부, 재테스트 예정일을 남긴다.

### 6.2 샘플 기록 예시

| Test ID | 검증 항목 | 테스트 일시 | 수행자 | 결과 (Pass/Fail/확인 필요) | 관찰된 상태 전이 | 로그 근거 | 이슈 및 후속 조치 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| RR-01 | 단기 네트워크 단절 후 자동 복구 | 2026-04-10 15:30 KST | Codex / QA Pair | Pass | `Connected -> Disconnected -> Reconnecting -> Connected` | `Session Disconnected`, `Reconnect Attempt`, `Reconnect Succeeded` | 세션 중 대상 측 네트워크를 약 3초 차단했을 때 첫 재시도 구간에서 정상 복구됨. 최근 연결 시각과 마지막 정상 연결 정보 갱신 확인. |
| RR-02 | 장치 재탐색 실패 시 마지막 정상 엔드포인트 재사용 | 2026-04-10 15:37 KST | Codex / QA Pair | Pass | `Connected -> Disconnected -> Reconnecting -> Connected` | `Reconnect Failed`, `Reconnect Succeeded / Source=LastKnownGood` | 탐색 경로를 제한한 상태에서도 마지막 정상 주소 기준 재연결 성공. 브로드캐스트 복구 전까지 세션 유지 전략 유효함. |
| RR-03 | 최대 재시도 또는 최대 시간 초과 시 `Failed` 종료 | 2026-04-10 15:42 KST | Codex / QA Pair | Pass | `Connected -> Disconnected -> Reconnecting -> Failed` | `Reconnect Attempt`, `Reconnect Exhausted` | 대상 장치를 오프라인으로 유지했을 때 최대 시도 후 실패 전이 확인. 운영 환경에서는 실제 네트워크 장비 차단 조건으로 1회 추가 검증 예정. |
| RR-04 | 사용자 수동 종료 시 자동 재연결 금지 | 2026-04-10 15:48 KST | Codex / QA Pair | Pass | `Connected -> Idle` | `Session Closed`, 재연결 로그 없음 | 사용자가 `Disconnect` 실행 후 20초 관찰 동안 재연결 시도 로그 미발생. 수동 종료와 오류 종료가 정상적으로 분리됨. |
| RR-05 | 앱 재시작 후 `Pre-approved device` 정책 일관성 | 2026-04-10 15:55 KST | Codex / QA Pair | 확인 필요 | `Idle -> Connected`, `Idle -> Approval denied` | `Session Connected`, `Connection Denied` | 즐겨찾기 저장 상태에서는 승인 없이 연결되고, 해제 후에는 거부됨. 다중 장치 및 지문 불일치 조건을 포함한 통합 검증은 추가 필요. |
| SEC-01 | 인증서 지문 재사용 및 검증 | 2026-04-10 16:05 KST | Codex / QA Pair | 확인 필요 | `Idle -> Connected`, `Idle -> Failed` | `Security Information`, `Security Verified` | 동일 실행 환경 재시작 후 지문 유지와 최초 등록은 확인. 지문 불일치 강제 환경은 별도 테스트 인프라 준비 후 재검증 필요. |

## 7. 실행 런북 (Execution Runbook)

### 7.1 공통 준비
1. 테스트 시작 전 `dotnet build RemotePCControl.App\RemotePCControl.App.csproj -c Debug -p:Platform=x64 --no-restore`가 성공하는지 확인한다.
2. 릴레이 경로까지 함께 점검할 경우 `dotnet build RemotePCControl.RelayServer\RemotePCControl.RelayServer.csproj -c Debug --no-restore`도 성공해야 한다.
3. 필요 시 최초 1회는 NuGet 복원이 가능한 환경에서 `dotnet build RemotePCControl.slnx`를 수행해 패키지 상태를 맞춘다.
4. Quick Connect 입력값, Approval Mode, Auto reconnect, Local drive redirect, Relay Server IP/Code 등 현재 UI 노출 항목을 캡처해 테스트 기록에 첨부한다.
5. `Audit Timeline`을 열어 초기 로그가 최신순으로 보이는지 먼저 확인한다.
6. 테스트 기록 표에 테스트 시작 시각과 수행자를 먼저 기록한다.

### 7.1.1 현재 빌드 기준 수동 스모크 체크
1. 앱 실행 후 상태 표시줄에 CPU, Memory, Git Commit Count/Hash가 표시되는지 확인한다.
2. `Quick Connect` 대상 선택 후 Approval Mode 변경이 즉시 반영되는지 확인한다.
3. `Remote Explorer` 실행 시 브라우저 하단에 현재 경로와 탐색 상태 문구가 분리되어 보이는지 확인한다.
4. `Local drive redirect`를 끈 뒤 원격 파일 브라우저 요청 시 차단 메시지와 감사 로그가 남는지 확인한다.
5. `Internet Relay (Beta)` 영역에서 Relay Code 미입력 시 친절한 오류 문구가 표시되는지 확인한다.
6. Relay 오류(잘못된 코드, 없는 코드, 만료 코드) 발생 시 상태 문구와 Audit Timeline이 같은 의미로 정리되어 보이는지 확인한다.

### 7.2 RR-01 실행 절차
1. 장치를 연결하고 `Connected` 상태가 될 때까지 기다린다.
2. `Auto reconnect` 옵션이 켜져 있는지 확인한다.
3. 활성 세션 중 대상 앱 프로세스를 짧게 재시작하거나 네트워크를 잠깐 차단한다.
4. UI 상단 상태가 `Disconnected`에서 `Reconnecting`으로 바뀌는지 기록한다.
5. Audit Timeline에서 `Reconnect Attempt` 로그의 지연 간격이 증가하는지 확인한다.
6. 다시 연결되면 `Reconnect Succeeded` 로그와 `Connected` 상태를 기록한다.

### 7.3 RR-02 실행 절차
1. 최소 1회 정상 연결을 완료해 마지막 정상 연결 정보가 저장된 상태를 만든다.
2. 브로드캐스트 탐색이 실패하도록 탐색 경로만 제한한다.
3. 기존 세션을 오류 종료시켜 자동 재연결을 유도한다.
4. 재연결 로그에서 장치 해석 실패 뒤에도 시도가 계속 이어지는지 확인한다.
5. 성공 시 `Source=LastKnownGood` 또는 동일 의미의 재연결 로그를 기록한다.

### 7.4 RR-03 실행 절차
1. 연결 대상 장치를 완전히 오프라인으로 만든다.
2. 활성 세션을 오류 상황으로 종료시킨다.
3. 최대 재시도 횟수 또는 최대 시간 제한까지 기다린다.
4. 상태가 최종적으로 `Failed`로 끝나는지 확인한다.
5. `Reconnect Exhausted` 또는 시간 초과 로그를 표에 기록한다.

### 7.5 RR-04 실행 절차
1. 정상 연결 상태에서 사용자가 `Disconnect` 버튼을 직접 누른다.
2. 이후 최소 20초 동안 상태 변화와 Audit Timeline을 관찰한다.
3. `Reconnect Attempt` 로그가 생성되지 않는지 확인한다.
4. 상태가 `Idle`로 끝나는지 기록한다.

### 7.6 RR-05 실행 절차
1. 특정 장치를 즐겨찾기로 저장한다.
2. 앱을 완전히 종료한 뒤 다시 시작한다.
3. 동일 장치에 대해 `Pre-approved device` 정책으로 연결 시도한다.
4. 성공 여부와 `Session Connected` 또는 유사 로그를 기록한다.
5. 같은 장치의 즐겨찾기를 해제하고 앱을 재시작한다.
6. 다시 `Pre-approved device` 정책으로 연결 시도해 거부 여부와 `Connection Denied` 로그를 기록한다.

### 7.7 보안 검증 절차
1. 최초 연결 시 인증서 지문이 저장되는지 확인한다.
2. 앱 재시작 후 동일 장치 연결 시 인증서가 재생성되지 않고 검증 성공 로그가 남는지 확인한다.
3. 저장된 지문과 다른 인증서를 제시하는 환경에서는 연결 실패가 발생하는지 확인한다.
4. 실패 시 세션이 `Connected`로 진입하지 않아야 하며, 보안 관련 로그가 남아야 한다.

### 7.8 연결 경로 및 Fallback 검증 절차
1. Local/Public/Relay 후보가 2개 이상 포함된 테스트 장치를 준비한다.
2. `Quick Connect` 실행 직후 `Connection Route Plan` 로그가 기대한 우선순위(Local -> Public -> Relay)로 남는지 확인한다.
3. 첫 번째 경로를 의도적으로 실패시켜 `Connection Route Failed`와 `Connection Route Fallback` 로그가 순서대로 남는지 확인한다.
4. 다음 경로 성공 시 상단 상태가 `Connected via <Route>` 형식으로 표시되는지 확인한다.
5. Approval denied / cancelled / timed out 시에는 다음 경로로 넘어가지 않고 즉시 종료되는지 확인한다.

### 7.9 릴레이 하드닝 검증 절차
1. Relay Host를 동일 코드로 두 번 등록해 중복 코드 거부 문구가 표시되는지 확인한다.
2. 잘못된 형식(6자 초과, 소문자/특수문자 포함 등)의 Relay Code 입력 시 invalid code 문구가 표시되는지 확인한다.
3. Host 대기 후 TTL 경과 환경에서 client 연결 시 expired 문구가 표시되는지 확인한다.
4. Relay Host 또는 Client 중 한쪽을 강제 종료했을 때 반대편 세션이 정리되고 비정상 종료 로그가 남는지 확인한다.

## 8. 결론
Remote PC Control은 "언제 어디서나 안정적인 연결"을 핵심 가치로 한다. 단순한 기능 구현을 넘어, 극한의 환경에서도 시스템을 보호하고 사용자 경험을 유지하는 회복탄력성 확보를 최우선 순위로 둔다.

## 9. 2026-04-13 Revalidation Notes

### 9.1 이번 턴에서 실제 확인한 항목
- `dotnet build RemotePCControl.slnx`가 2026-04-13 기준 성공했다.
- `RemotePCControl.App.exe`, `RemotePCControl.RelayServer.exe`는 단기 기동 확인 후 종료했다.
- 코드 기준으로 재연결 상태 전이, 승인 응답 처리, 텔레메트리 RTT 측정, 인증서/장치 ID 저장 경로가 여전히 연결되어 있음을 재확인했다.
- 이후 추가 자동 스모크 검증으로 `RemotePCControl.App.csproj`, `RemotePCControl.RelayServer.csproj`를 각각 `--no-restore` 빌드 성공했고, 최신 바이너리 단기 기동도 다시 확인했다.

### 9.2 이번 턴에서 재현하지 못한 항목
- `RR-01` ~ `RR-05`의 실제 상호작용 기반 재검증은 GUI 조작, 네트워크 차단, 다중 장치 또는 다중 프로세스 역할 분리가 필요하므로 이번 턴에서는 재실행하지 못했다.
- `SEC-01`의 지문 불일치 강제 시나리오는 별도 인증서 변조 또는 분리된 테스트 환경이 필요하므로 이번 턴에서는 정적 확인만 수행했다.

### 9.3 현재 판단
- 최신 코드와 빌드 기준으로 보면 회복탄력성/보안 기능은 여전히 연결되어 있으나, 운영 근거를 갱신하려면 수동 QA 런북 기반 재테스트가 필요하다.
- 따라서 현재 상태는 "빌드/기동 재확인 완료, 시나리오형 회복탄력성 검증은 추가 수행 필요"로 기록한다.

## 10. Current QA Focus
- 우선순위 1: `RR-01` ~ `RR-04` 재검증으로 자동 재연결 상태 전이와 종료 사유 분리를 다시 확인한다.
- 우선순위 2: `7.8 연결 경로 및 Fallback 검증 절차`를 통해 Local/Public/Relay 우선순위와 정책 종료 시 fallback 중단 동작을 확인한다.
- 우선순위 3: `7.9 릴레이 하드닝 검증 절차`를 통해 duplicate / invalid / expired relay code 처리와 stale session 정리를 확인한다.
- 우선순위 4: `SEC-01` 재검증으로 인증서 지문 유지 및 불일치 차단 시나리오를 운영 근거 수준으로 보강한다.

## 11. Latest Automated Smoke Result
| 날짜 | 검증 항목 | 결과 | 근거 |
| --- | --- | --- | --- |
| 2026-04-13 | App project build (`--no-restore`) | Pass | `dotnet build RemotePCControl.App\RemotePCControl.App.csproj -c Debug -p:Platform=x64 --no-restore` |
| 2026-04-13 | Relay project build (`--no-restore`) | Pass | `dotnet build RemotePCControl.RelayServer\RemotePCControl.RelayServer.csproj -c Debug --no-restore` |
| 2026-04-13 | App short startup | Pass | 프로세스 PID `16264` 기동 후 수동 종료 |
| 2026-04-13 | Relay short startup | Pass | 프로세스 PID `23832` 기동 후 수동 종료 |
