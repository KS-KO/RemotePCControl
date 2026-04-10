# Phase D: 인터넷 확장(Internet Extension) 아키텍처 설계 (Draft)

## 1. 개요
현재 Remote PC Control은 로컬 네트워크(LAN) 환경에서의 P2P 통신 및 브로드캐스트 탐색을 기반으로 동작한다. Phase D의 목표는 사용자가 전 세계 어디서든 인터넷을 통해 자신의 PC에 안전하게 접속할 수 있도록 시스템 구조를 확장하는 것이다.

## 2. 주요 아키텍처 컴포넌트

### 2.1 Identity & Signaling Server (중앙 식별자 서버)
- **역할**: 장치의 전역 식별자(GUID)와 현재 상태(Online/Offline), 접속 가능한 엔드포인트 정보를 매핑하고 관리한다.
- **기능**:
    - 장치 등록 및 인증 (JWT 기반).
    - 시그널링(Signaling): 클라이언트와 호스트 간의 연결 협상(ICE Candidate 교환).
    - Presence 관리: 실시간 접속 상태 모니터링.

### 2.2 Relay Server (데이터 중계 서버)
- **역할**: 방화벽이나 NAT 환경으로 인해 직접 P2P 연결이 불가능한 경우, 데이터를 중계하여 통신을 유지한다.
- **기술 스택**: .NET 기반 고성능 소켓 서버 또는 Kestrel(WebSockets).
- **보안**: 종단간 암호화(E2EE)를 유지하여 릴레이 서버 자체에서도 원격 화면 데이터에 접근할 수 없도록 설계.

### 2.3 Hybrid Connection Strategy
1.  **Direct P2P**: 동일 망인 경우 현재처럼 직접 연결.
2.  **STUN/ICE**: NAT 환경에서 구멍 뚫기(Hole Punching) 시도.
3.  **Relay (Fallback)**: 위 방법 실패 시 중계 서버를 통해 데이터 파이프 구축.

## 3. 통신 프로토콜 확장

### 3.1 Signaling 프로토콜 (0x20 계열 추가)
- **0x20 (Offer)**: 세션 연결 요청 정보 전달.
- **0x21 (Answer)**: 연결 수락 및 엔드포인트 응답.
- **0x22 (Candidate)**: ICE 후보 목록 교환.

### 3.2 Relay 프로토콜 (0x30 계열 추가)
- **0x30 (JoinRoom)**: 릴레이 서버의 특정 세션 채널 입장.
- **0x31 (RelayData)**: 중계 데이터를 위한 캡슐화 패킷.

## 4. 보안 강화 로드맵
- **TLS 1.3 강제**: 모든 인터넷 구간 통신에 대한 암호화 강화.
- **Certificate Pinning**: 사전에 협의된 장치 인증서만 연결 허용.
- **2FA/MFA**: 웹 포털 또는 모바일 앱을 통한 추가 인증 단계 검토.

## 5. 단계별 실행 계획
1.  **D-1 (Identity)**: 중앙 식별자 관리용 미니멀 서버(REST API) 구축 및 연동.
2.  **D-2 (Relay Prototype)**: 소켓 기반 릴레이 서버 프로토타입 개발 및 Fallback 로직 적용.
3.  **D-3 (NAT Traversal)**: STUN 기반 Hole Punching 라이브러리 연동 및 최적화.
4.  **D-4 (Remote Portal)**: 웹 브라우저에서도 장치 상태를 확인하고 연결을 트리거할 수 있는 관리 대시보드 구축.

---
> [!NOTE]
> 본 설계안은 인터넷 환경으로의 확장을 위한 기초 가이드라인입니다. 실제 구현 시 인프라 비용 및 지연 시간(Latency) 최적화 전략이 기술적 핵심이 될 것입니다.
