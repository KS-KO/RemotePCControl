# Product Request Document

## 1. 문서 정보
- 제품명: Remote PC Control
- 문서명: 원격 데스크톱 연결 및 제어 프로그램 PRD
- 버전: v0.2 Draft
- 작성일: 2026-03-17
- 대상 독자: 기획, 개발, QA, 운영

## 2. 제품 개요
Remote PC Control은 사용자가 인터넷 또는 사내망 환경에서 다른 PC에 안전하게 연결하여 화면을 확인하고 입력을 제어할 수 있도록 지원하는 원격 데스크톱 프로그램이다. 본 제품은 개인 사용자, 원격 근무자, 소규모 조직, 헬프데스크 담당자를 주요 대상으로 하며, 설치와 연결 과정을 단순화하면서도 보안과 안정성을 확보하는 것을 목표로 한다.

초기 버전은 Windows 환경을 우선 지원하며, 향후 멀티 플랫폼 확장을 고려한 구조를 지향한다.
초기 구현 프레임워크는 .NET 9를 기준으로 하며, 배포 및 실행 아키텍처는 x64 전용 빌드를 기본으로 한다.
또한, 단일 애플리케이션이 서버(제어 대상)와 클라이언트(제어 주체) 역할을 모두 수행할 수 있도록 통합 구조로 구현한다.
클라이언트 UI 아키텍처는 유지보수성과 테스트 용이성을 위해 WPF(Windows Presentation Foundation) 기반의 MVVM 패턴을 적용한다.

## 3. 문제 정의
- 사용자는 외부에서 자신의 PC 또는 관리 대상 PC에 접속해 작업을 이어갈 수 있어야 한다.
- 원격 지원 담당자는 대상 사용자의 PC 상태를 직접 확인하고 즉시 문제를 해결할 수 있어야 한다.
- 기존 원격 접속 도구는 설치, 방화벽 설정, 승인 절차, 성능, 보안 정책 대응 측면에서 진입 장벽이 존재한다.
- 사용자 입장에서는 연결이 쉬우면서도 무단 접속에 대한 불안이 적은 제품이 필요하다.

## 4. 목표
### 제품 목표
- 최소한의 설정만으로 빠르게 연결 가능한 원격 접속 경험 제공
- 원격 화면 보기, 입력 제어, 파일 전송 등 핵심 원격 업무 기능 통합
- 접속 승인, 인증, 암호화, 로그를 포함한 신뢰 가능한 보안 체계 제공
- 네트워크 품질 저하 상황에서도 연결 유지 또는 자동 재연결 지원

### 비즈니스 목표
- 개인 및 소규모 조직을 위한 사용이 쉬운 원격 제어 제품 포지셔닝
- 초기 MVP 출시 후 실제 사용 데이터를 바탕으로 상용화 가능성 검증
- 향후 무인 접속, 조직 관리 기능, 다중 플랫폼 지원으로 확장 가능한 기반 확보

## 5. 비목표
- 초기 버전에서 대기업용 중앙 관리자 콘솔 및 고도화된 조직 권한 체계 제공
- 모바일 기기에서의 완전한 원격 제어 UX 제공
- 화상회의, 협업 채팅, 문서 공동 편집 등 협업 플랫폼 기능 제공
- 대규모 SaaS 운영을 위한 청구, 라이선스, 테넌트 관리 기능 제공

## 6. 대상 사용자
### 1차 사용자
- 외부에서 본인 PC에 접속하려는 개인 사용자
- 재택 근무 중 회사 PC 또는 사내 장비에 접속해야 하는 사용자
- 가족 또는 지인의 PC를 원격 지원하는 사용자
- 소규모 IT 관리자 및 헬프데스크 담당자

### 2차 사용자
- 교육 기관 및 소규모 사무실 관리자
- 테스트 장비 또는 서버성 PC에 원격 점검이 필요한 개발자 및 QA 담당자

## 7. 핵심 사용자 시나리오
### 시나리오 1: 개인 원격 접속
사용자는 외부에서 집 또는 사무실 PC에 접속하여 문서 작업, 애플리케이션 실행, 파일 확인을 수행한다.

### 시나리오 2: 원격 기술 지원
지원 담당자는 대상 사용자의 승인을 받은 뒤 원격으로 접속하여 문제를 진단하고 해결한다.

### 시나리오 3: 업무 환경 접속
사용자는 회사 노트북에서 사내 PC 또는 원격 서버성 장치에 접속하여 업무용 프로그램을 실행한다.

### 시나리오 4: 세션 중 데이터 교환
사용자는 로컬 장치와 원격 장치 간 파일을 전송하고, 클립보드를 공유하며, 세션 종료 후 접속 기록을 확인한다.

### 시나리오 5: 연결 복구
네트워크 일시 장애가 발생하면 시스템은 자동 재연결을 시도하고, 실패 시 명확한 원인과 다음 행동을 사용자에게 안내한다.

## 8. 제품 범위
### 포함 범위
- 원격 장치 등록 및 연결
- 승인 기반 또는 사전 승인 기반 접속
- 화면 전송 및 마우스/키보드 제어
- 파일 전송
- 로컬 드라이브 연결
- 클립보드 텍스트 공유
- 세션 상태 표시 및 접속 로그 저장
- 자동 재연결 및 오류 안내

### 제외 범위
- 조직 단위 사용자 관리 콘솔
- 다중 사용자 동시 협업 세션
- 모바일 앱 기반 완전 제어
- 세션 녹화 및 감사 리포트 자동 생성

## 9. 요구사항
### 기능 요구사항
- FR-0: 단일 애플리케이션 내에서 서버(제어 대상) 모드와 클라이언트(제어 주체) 모드로 모두 동작할 수 있어야 한다.
- FR-1: 사용자는 장치 ID, 초대 코드, 또는 저장된 장치 목록을 통해 원격 PC에 연결할 수 있어야 한다.
- FR-2: 시스템은 원격 연결 시 사용자 승인 절차 또는 사전 승인 정책을 지원해야 한다.
- FR-3: 사용자는 Windows 원격 데스크톱(RDP) 앱과 동일한 뷰 수준으로 대상 PC의 Windows 바탕화면을 자연스럽게 볼 수 있도록 구현되어야 하며, 마우스와 키보드 입력으로 원격 PC를 매끄럽게 제어할 수 있어야 한다.
- FR-4: 사용자는 전체 화면, 화면 맞춤, 해상도 조정, 다중 모니터 전환 기능을 사용할 수 있어야 한다.
- FR-5: 사용자는 로컬 장치와 원격 장치 간 파일 업로드 및 다운로드를 수행할 수 있어야 한다.
- FR-6: 사용자는 원격 세션에서 로컬 PC의 드라이브를 연결하여 원격 시스템에서 탐색 가능한 형태로 접근할 수 있어야 한다.
- FR-7: 사용자는 세션 중 클립보드 텍스트 공유 기능을 사용할 수 있어야 한다.
- FR-8: 사용자는 세션 중 `Ctrl+C` 및 `Ctrl+V` 입력을 통해 파일 복사 및 붙여넣기 기반 전송을 수행할 수 있어야 한다.
- FR-9: 사용자는 최근 접속 목록 및 즐겨찾기 장치를 관리할 수 있어야 한다.
- FR-10: 시스템은 연결 상태, 지연 시간, 재연결 상태 등 세션 상태 정보를 제공해야 한다.
- FR-11: 시스템은 접속 시작 및 종료 시각, 사용자, 대상 장치, 승인 여부 등 세션 로그를 저장해야 한다.
- FR-12: 시스템은 네트워크 단절 시 자동 재연결을 시도하고 실패 시 명확한 오류 메시지를 제공해야 한다.
- FR-13: 사용자는 세션 중 화면 잠금, 입력 차단, 세션 종료 기능을 사용할 수 있어야 한다.
- FR-14: 시스템은 향후 확장을 위해 장치 관리, 인증, 세션 관리 기능을 분리 가능한 구조로 제공해야 한다.
- FR-15: 애플리케이션의 상태표시줄 오른쪽 끝에는 변경 이력 및 버전 식별의 용이성을 위해, 현재 관리 중인 Git의 Commit Count와 Hash Code 9자리를 표시해야 한다.
- FR-16: 애플리케이션은 동일한 환경에서 중복 프로세스가 불필요하게 늘어나는 것을 방지하고 오직 단일 인스턴스(Single Instance) 형태로만 한 번만 실행되어야 한다.
- FR-17: 애플리케이션의 상태표시줄에는 운영자가 세션 중 시스템 부하를 빠르게 확인할 수 있도록 로컬 PC의 CPU 사용률과 메모리 사용 상태를 표시해야 한다.
- FR-18: 사용자는 IP 주소 대신 장치 이름(Device Name) 또는 장치 번호(Device Code)를 사용하여 대상 PC를 식별하고 연결할 수 있어야 한다.
- FR-19: 시스템은 사용자 표시용 장치 식별자(Device Name, Device Code)와 내부 고유 식별자(Internal Device GUID)를 분리하여 관리해야 한다.
- FR-20: 시스템은 동일 로컬 네트워크에서 UDP 브로드캐스트 기반 장치 검색을 지원해야 한다.
- FR-21: 시스템은 앱 시작 시 및 장치 식별자 변경 시, 동일 로컬 네트워크 내 장치 이름 또는 장치 번호의 중복 여부를 확인해야 한다.
- FR-22: 동일한 장치 이름 또는 장치 번호가 발견되었고 내부 GUID가 다를 경우, 시스템은 충돌로 판단하고 사용자에게 경고해야 한다.
- FR-23: 동일 식별자를 가진 장치가 여러 대 발견되면, 시스템은 사용자가 연결 대상을 선택할 수 있는 후보 목록 UI를 제공해야 한다.
- FR-24: 시스템은 로컬 직접 연결을 우선 시도하고, 실패 시 다른 연결 후보 경로를 순차적으로 시도할 수 있어야 한다.
- FR-25: 장치 식별 및 연결 해석 구조는 향후 중앙 디렉터리 서버 및 중계 서버 확장이 가능하도록 설계되어야 한다.
- FR-26: 주요 연결 입력 항목, 원격 기능 토글, 상태 표시 영역에는 운영자가 의미를 빠르게 이해할 수 있도록 문맥형 ToolTip을 제공해야 한다.

### 비기능 요구사항
- NFR-1: 모든 원격 세션 데이터는 전송 중 암호화되어야 한다.
- NFR-2: 인증 정보와 장치 식별 정보는 안전하게 저장되어야 하며 평문 저장을 금지한다.
- NFR-3: 평균적인 사무용 네트워크 환경에서 사용자가 체감 가능한 수준의 낮은 입력 지연을 제공해야 한다.
- NFR-4: 연결 실패, 인증 실패, 파일 전송 실패 시 복구 가능한 오류 처리와 사용자 안내를 제공해야 한다.
- NFR-5: 초기 버전은 Windows를 우선 지원하되, 향후 macOS 및 Linux 확장이 가능한 구조를 고려해야 한다.
- NFR-6: UI는 비전문가도 쉽게 사용할 수 있도록 단순하고 명확해야 한다.
- NFR-7: 로그는 추적 가능해야 하지만 민감 정보 노출을 최소화해야 한다.
- NFR-8: 장시간 세션에서도 성능 저하와 메모리 누수를 최소화해야 한다.
- NFR-9: 애플리케이션은 .NET 9 기반으로 개발해야 한다.
- NFR-10: 초기 배포본은 x64 아키텍처만 지원하는 전용 빌드로 제공해야 한다.
- NFR-11: 클라이언트 UI 레이어는 WPF(Windows Presentation Foundation) 기반의 MVVM 패턴을 적용하여 View, ViewModel, Model의 책임을 분리해야 한다.
- NFR-12: 빌드 폴더 간소화를 적용하여, 개발 및 배포 빌드 출력 경로가 불필요하게 깊어지는 것(프레임워크 타겟, 런타임 식별자 폴더 중첩)을 방지하고 팀이 직관적으로 접근할 수 있는 단일 구조로 표준화해야 한다.
- NFR-13: 프로젝트 소스 코드의 변경 이력 추적 및 체계적인 협업 관리를 보장하기 위해, Git(버전 관리 시스템)을 사용하여 모든 형상 관리를 수행해야 한다.
- NFR-14: 장치 식별자 충돌 검사는 오탐 또는 미탐 가능성을 사용자에게 명확히 안내할 수 있어야 하며, 충돌 결과는 로그로 기록되어야 한다.
- NFR-15: 로컬 장치 검색은 동일 서브넷 환경에서 합리적인 시간 내에 완료되어야 하며, 검색 실패가 애플리케이션 전체 응답성을 저해해서는 안 된다.
- NFR-16: 장치 검색, 충돌 감지, 연결 해석 과정은 비동기 방식으로 수행되어 UI 스레드를 차단하지 않아야 한다.
- NFR-17: 장치 검색 및 연결 로그에는 장치 이름, 장치 번호, 내부 GUID 일부, 연결 경로 유형(Local/Public/Relay), 승인 결과를 포함할 수 있어야 한다.
- NFR-18: 장치 식별 및 연결 구조는 향후 인터넷 환경에서의 전역 유일성 검증, 디렉터리 조회, 중계 연결로 확장 가능한 계층 구조를 유지해야 한다.

## 10. 성공 지표
- 첫 연결 성공률이 내부 목표 기준 이상일 것
- 평균 연결 소요 시간이 사용자 허용 범위 내일 것
- 세션 비정상 종료율과 재연결 실패율이 관리 가능한 수준일 것
- 파일 전송 성공률이 높고 주요 사용성 불만이 적을 것
- 사용자 테스트에서 연결 편의성과 안정성 만족도가 긍정적으로 평가될 것

## 11. 제약사항 및 리스크
### 제약사항
- OS별 화면 캡처 및 입력 제어 방식 차이로 인해 초기 버전은 Windows 중심 설계가 필요하다.
- NAT, 방화벽, 사내 보안 정책 등에 따라 직접 연결이 제한될 수 있다.
- 원격 제어 기능은 보안 민감도가 높아 승인, 인증, 로그, 암호화 체계가 필수다.
- 개발 및 배포 프레임워크는 .NET 9로 고정하며, CPU 아키텍처는 초기 버전에서 x64만 지원한다.
- 클라이언트 애플리케이션 구조는 WPF 기반의 MVVM 패턴을 기준으로 설계하여 UI 로직과 비즈니스 로직의 혼합을 방지해야 한다.
- 빌드 출력(아티팩트) 경로는 빌드 폴더 간소화 설정을 통해 불필요하게 깊거나 복잡하지 않게 관리해야 하며, 운영 및 테스트 전달 프로세스에서의 혼선을 줄여야 한다.
- 로컬 브로드캐스트 기반 중복 확인은 동일 서브넷 환경에 한해 유효하며, 인터넷 환경에서 전역 유일성을 보장하지 못한다.

### 리스크
- 네트워크 상태에 따라 사용자 경험 편차가 크게 발생할 수 있다.
- 인증 및 권한 모델이 약할 경우 무단 접속 위험이 존재한다.
- 파일 전송 및 클립보드 공유 기능은 민감 정보 유출 경로가 될 수 있다.
- 설치 시 서비스 등록, 방화벽 예외, 권한 요구가 사용성을 저해할 수 있다.
- 브로드캐스트 기반 중복 감지는 네트워크 격리, 방화벽 정책, 동시 시작 경쟁 상태에 따라 일부 충돌을 검출하지 못할 수 있다.
- 사용자 표시용 장치 이름은 편의성을 제공하지만 중복 가능성이 있으므로, 실제 시스템 식별은 내부 GUID와 함께 처리해야 한다.

## 12. 오픈 이슈
- 초기 연결 방식을 P2P 우선으로 할지, 중계 서버 기반으로 할지, 혼합형으로 할지 결정이 필요하다.
- 인증 방식을 PIN 기반, 계정 로그인 기반, 또는 다중 인증 포함 구조 중 무엇으로 할지 확정이 필요하다.
- 무인 접속 기능을 MVP에 포함할지, 후속 버전으로 분리할지 판단이 필요하다.
- 파일 전송, 다중 모니터, 클립보드, 세션 로그 고도화 기능의 MVP 우선순위 확정이 필요하다.
- 상용화를 고려할 경우 라이선스 정책과 개인정보 처리 정책 정의가 필요하다.
- 장치 번호(Device Code)를 사용자 지정으로 할지 자동 발급으로 할지 결정이 필요하다.
- 로컬 탐색 프로토콜을 UDP 브로드캐스트로 할지 멀티캐스트로 할지 결정이 필요하다.
- 동일 식별자 충돌 시 자동 이름 변경 규칙을 둘지, 사용자 수동 수정만 허용할지 결정이 필요하다.
- 인터넷 확장 시 중앙 디렉터리와 릴레이 서버를 분리할지 통합할지 결정이 필요하다.
## 13. Multi-Monitor Update
- The MVP viewer must support separate selection of `Captured Display` and `Viewer Window Display`.
- The `Viewer Window Display` selector must include `Auto (Safe Display)`.
- When `Keep viewer off captured display` is enabled, the viewer window must avoid the monitor currently used for capture.
- If the viewer target and capture target are the same while safety mode is enabled, the viewer must fall back to a different monitor when available.
- The UI and runtime logs should expose the active capture display and viewer display choices for operator validation.
- This requirement exists to prevent recursive self-capture during same-PC or loopback validation.
- The status bar must expose local CPU and memory usage for quick resource-state validation during a remote session.

## 14. Implementation Update Summary
### 14.1 Current MVP Status
- The application currently supports loopback-based remote desktop verification on the same PC through a unified server/client runtime.
- The remote viewer can receive screen frames, relay mouse input, relay keyboard input, and open programs through remote interaction.
- Capture-display selection, viewer-display selection, safe viewer placement, audit timeline presentation, and loopback file upload flow are connected in the current build.
- The current build does not yet complete the full PRD scope for bidirectional file transfer, persisted audit logging, clipboard synchronization, drive redirection, or encrypted transport.

### 14.2 Screen Capture and Rendering
- The original unstable DXGI desktop duplication runtime path was replaced with a GDI-based capture path for better execution stability in the current environment.
- Raw BGRA rendering is supported in the viewer, and JPEG-compressed frame transport/decoding is also supported.
- When the screen image does not change, redundant frame transmission is reduced to lower CPU usage and transport overhead.
- Frame reception and remote window creation events are exposed through the audit timeline for runtime verification.

### 14.3 Multi-Monitor Behavior
- Operators can choose `Captured Display` and `Viewer Window Display` independently.
- `Viewer Window Display` includes `Auto (Safe Display)` mode.
- When safe placement is enabled, the viewer window avoids the captured monitor to prevent recursive self-capture.
- If the operator manually moves the viewer onto the captured monitor in an extended-display environment, the viewer is repositioned to a safe monitor when available.

### 14.4 Viewer and Session Behavior
- When the operator closes the remote desktop window, the active remote session is terminated and the viewer is not reopened automatically by subsequent frames.
- The viewer now exposes a shortcut bar for `Start`, `Run`, `Task Manager`, `Alt+Tab`, and `Esc`.
- The viewer can forward keyboard input after focus is acquired, allowing program launch through Start search, Run dialog, and common shortcuts.
- The local arrow cursor is explicitly shown over the remote viewer surface.
- The current implementation provides connection quality summary text and reconnect attempt logs, but does not yet expose a measured latency indicator.

### 14.5 UI and Operator Controls
- The Connection Center currently exposes selectors for device ID, approval policy, captured display, viewer window display, capture rate, and transfer compression.
- Key operator-facing inputs and controls should expose ToolTip guidance so that device lookup, display routing, compression, and transfer options can be understood without separate documentation.
- The Remote Features panel includes options such as clipboard sync, Ctrl+C/Ctrl+V file copy, safe viewer placement, local drive redirect, and auto reconnect.
- Transfer compression options currently include raw BGRA and JPEG quality presets.
- Capture rate presets currently include 15 FPS and 30 FPS.
- The current build target and distribution baseline are maintained as a dedicated x64 build for Windows.
- The bottom status bar currently shows live local CPU usage, memory usage, and the Git commit count/hash label.
- Recent connections and favorite-device management are not yet exposed as dedicated operator workflows in the current UI.

### 14.6 Documentation and Remaining Follow-Up
- The PRD already reflects multi-monitor safety behavior and status-bar resource visibility requirements.
- CPU and memory indicators are no longer a follow-up item; the current status bar already shows live local CPU and memory usage together with the Git version label.
- If remote cursor visibility still appears inconsistent after the local cursor fix, a remote-cursor overlay should be considered as the next implementation step.
- `Approval Policy` is partially connected. The current build includes allow/deny branching, but `Support request` is not yet tied to a distinct end-to-end host approval workflow and policy rules still need hardening.
- The `Auto reconnect` option is no longer UI-only. The current build includes a bounded retry workflow, but additional UX refinement and clearer disconnect cause reporting are still follow-up items.
- Clipboard sync, `Ctrl+C` / `Ctrl+V` file transfer, and local drive redirect currently expose operator toggles and status messaging, but they are not yet wired to a complete remote session data path.
- File transfer currently supports a basic upload/send path in the loopback runtime, but a production-ready upload/download workflow and operator-selectable destination handling are still follow-up items.
- Audit timeline entries are currently kept in application memory for runtime verification. Persistent session log storage is still a follow-up item.
- Transport encryption and secure-at-rest protection for stored device identity data remain follow-up items.
- Recent connection history and favorite device management remain follow-up items.

### 14.7 Build Verification Update
- A routine verification flow for the current Windows x64 MVP must include `clean -> build -> run` in sequence.
- The purpose of this verification is to confirm that the local development environment can reproduce a fresh executable state before additional feature work proceeds.
- Any failure discovered during clean, build, or startup must be recorded with the failing stage and the exact error message for follow-up.

### 14.8 Next Development Target
- The next implementation target is to harden `Approval Policy` so that `User approval`, `Pre-approved device`, and `Support request` each follow a distinct and verifiable session-authorization path.
- `Pre-approved device` must only allow devices explicitly marked as trusted, and must not rely on placeholder logic.
- `Support request` must progress beyond UI-only snapshot state and participate in the actual connection request and approval flow.
- Approval outcomes should be reflected consistently in session state text and runtime audit logs so operators can understand whether a session was allowed, denied, cancelled, or timed out.

### 14.9 Clipboard Sync Development Target
- The next implementation target after approval-policy hardening is to connect the existing clipboard option to an actual text clipboard synchronization path inside the loopback session runtime.
- Clipboard sync must be limited to text content for the current MVP step.
- The synchronization flow should avoid redundant clipboard writes and should not create an infinite echo loop between local and remote runtime endpoints.
- Clipboard sync state changes and notable synchronization events should be visible in runtime logs for operator validation.

### 14.10 Recent And Favorite Device Development Target
- The next implementation target after clipboard synchronization is to expose operator workflows for recent connections and favorite devices in the main client UI.
- A successful connection attempt should update a recent-connection list with device identity, timestamp, and last-used approval mode.
- Operators should be able to toggle whether a device is treated as a favorite without editing source code or seed data.
- Favorite state and recent-connection history should persist across application restarts for the local operator profile.
- The approval and connection flow should reuse the persisted favorite flag when evaluating `Pre-approved device` policy.
