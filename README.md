# OrbTail Server

실시간 멀티플레이 배틀로얄 게임 **OrbTail**의 .NET 서버입니다.

플레이어는 몬스터를 처치하고 소환석을 모아 오브를 소환·강화합니다.

오브는 플레이어를 따라 꼬리를 이루며 자동으로 공격하고, 플레이어는 이동으로 상대의 오브를 깨뜨릴 수 있습니다.

시간이 지나면 구역이 순차적으로 폐쇄되며, 안전한 구역으로 이동하면서 생존을 겨룹니다.

Unity 클라이언트와 .NET 서버를 1인 개발했으며, 이 저장소에는 서버 코드와 테스트를 포함합니다.

## 플레이 영상

- https://blog.naver.com/passioncitizen/224417588804

## 기술 스택

.NET 8 · C# · TCP · MessagePack · Redis · NATS · Docker

## 저장소 구성

### user_server — 계정·세션·매칭 관리

로그인과 플레이어 정보를 관리하고, 매칭된 참가자에게 게임 서버 주소와 입장 티켓을 전달합니다.

- `Accounts/`, `Players/`: 계정 인증 정보·토큰과 플레이어 정보 관리
- `Sessions/`: 클라이언트 요청 처리, 로그인 세션 관리와 서버 간 세션 메시지 전달
- `Matching/`: 공용 대기열과 매칭 리더 관리, 참가자 배정, 게임 서버 선택 및 입장 티켓 발급

### game_server — 실시간 매치 실행

매치별 상태를 관리하며 이동·전투·몬스터·봇의 게임 로직을 실행하고, 각 클라이언트에 필요한 상태 변경을 전달합니다.

- `Sessions/`: 입장 요청과 플레이어 명령 처리, 세션별 패킷 전송 및 전송 상태 관리
- `Matches/`: 매치 생성·틱 실행, 이동·전투·구역 폐쇄, 상태 동기화와 종료 처리
- `Players/`: 플레이어 이동·상호작용·아이템 획득, 오브 소환·강화·공격과 봇 행동 처리

### network — 공통 통신·프로토콜·게임 데이터

두 서버가 사용하는 통신 기반과 인프라 연동 코드를 제공합니다. 클라이언트와 공유하는 프로토콜·게임 데이터 정의도 포함합니다.

- `Core/`, `Packets/`: TCP 연결과 송수신 버퍼·큐, 패킷 생성 및 MessagePack 직렬화 처리
- `Common/`: 프로토콜·메시지 모델, 게임 규칙·지도 데이터와 CSV 로딩
- `Infrastructure/`: Redis 접근과 NATS 메시징 연동
- `GameEntry/`, `Routing/`: 입장 티켓 발급·검증·소비, 게임 서버 등록·조회와 프로토콜 라우팅

### server_tests — 서버 로직 검증

매칭·입장·세션 처리와 매치 진행, 전투 판정, 상태 동기화 등을 검증하는 테스트입니다.

## 실행 방법

Docker와 Docker Compose v2가 필요합니다. .NET SDK는 Docker 이미지에 포함되어 있어 별도로 설치하지 않아도 됩니다.

아래 명령은 Bash 기준이며, Windows에서는 Docker Desktop을 실행한 뒤 Git Bash를 사용합니다.

### 기본 실행

저장소 루트에서 실행합니다.

```bash
cp .env.local.example .env.local
bash dev.sh start
```

게임 서버·유저 서버·Redis·NATS를 실행합니다.

### 다중 서버 실행

```bash
SCALE=1 bash dev.sh start
```

게임 서버와 유저 서버를 각각 2대씩 실행합니다.

| 구분 | 기본 서버 | 추가 서버 |
|---|---|---|
| 클라이언트 → 유저 서버 | `7000` | `7001` |
| 클라이언트 → 게임 서버 | `9001` | `9003` |

### 로그 확인 및 종료

```bash
# 게임 서버 로그 확인
bash dev.sh logs game_server

# 기본 구성 종료
bash dev.sh stop

# 다중 서버 구성 종료
SCALE=1 bash dev.sh stop
```

## 빌드 및 테스트

저장소 루트에서 다음 명령을 실행하면 Docker 안에서 패키지 복원·빌드·테스트를 수행합니다.

**Linux·macOS**

```bash
docker run --rm \
  --mount "type=bind,source=$(pwd),target=/src" \
  --workdir /src \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test server.sln --configuration Release
```

**Windows Git Bash**

```bash
MSYS_NO_PATHCONV=1 docker run --rm \
  --mount "type=bind,source=$(pwd -W),target=/src" \
  --workdir /src \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test server.sln --configuration Release
```
