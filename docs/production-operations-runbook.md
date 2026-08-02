# Production Operations Runbook

Документ описує підготовку, запуск і оновлення production-пакетів Nexus blockchain node.

## Початкове налаштування

Bootstrap node:

```powershell
dotnet run --project Nexus.Setup -- setup bootstrap --public-url https://bootstrap.example.com --grpc-url https://bootstrap.example.com:7141
dotnet run --project Nexus.Setup -- validate --env deploy/bootstrap-node.env
```

Edge node:

```powershell
dotnet run --project Nexus.Setup -- invite --url https://bootstrap.example.com
dotnet run --project Nexus.Setup -- setup edge --invite <invite-token>
dotnet run --project Nexus.Setup -- validate --env deploy/edge-node.env
```

Consensus node:

```powershell
dotnet run --project Nexus.Setup -- setup consensus --invite <invite-token>
dotnet run --project Nexus.Setup -- validate --env deploy/consensus-node.env
```

Після генерації env-файлу потрібно перевірити публічні URL, `ORACLE_PUBLIC_KEY`, Raft endpoints для consensus nodes та шляхи до постійних volume.

## Запуск

Bootstrap node:

```powershell
docker compose --env-file deploy/bootstrap-node.env -f deploy/docker-compose.bootstrap.yml up -d --build
```

Edge node з UI та Iroh sidecar:

```powershell
docker compose --env-file deploy/edge-node.env -f deploy/docker-compose.edge-node.yml up -d --build
```

Consensus node з Iroh sidecar:

```powershell
docker compose --env-file deploy/consensus-node.env -f deploy/docker-compose.full-node.yml up -d --build
```

Перевірка контейнерів:

```powershell
docker compose --env-file deploy/bootstrap-node.env -f deploy/docker-compose.bootstrap.yml ps
docker compose --env-file deploy/edge-node.env -f deploy/docker-compose.edge-node.yml ps
docker compose --env-file deploy/consensus-node.env -f deploy/docker-compose.full-node.yml ps
```

Перевірка node endpoints:

```powershell
curl.exe -fsS https://bootstrap.example.com/api/setup/status
curl.exe -fsS http://localhost:7042/api/setup/status
curl.exe -fsS http://localhost:7042/api/setup/migrations
dotnet run --project Nexus.Setup -- check --url http://localhost:7042
```

## Setup Wizard

Вкладка Network в UI містить майстер налаштування ноди. Майстер генерує env-файли для чотирьох режимів:

- local node;
- edge node з підключенням через connection invite;
- consensus node;
- bootstrap node.

Форма перевіряє обов'язкові URL або connection invite і викликає `POST /api/setup/plan`. Endpoint генерує env-вміст з новими секретами та повертає файл для запуску.

У таблиці Known peers адміністратор може approve/revoke peer та змінити registry role на `Edge`, `Consensus` або `Bootstrap`. Зміна ролі у registry не додає вузол у Raft majority автоматично: consensus node має бути запущена з валідним `RAFT_TRANSPORT=Tcp` і TCP endpoint або з `RAFT_TRANSPORT=Iroh` та затвердженою Iroh identity/peer mapping.

## Signed Intent Ingress

Для Edge/network operations доступний endpoint:

```text
POST /api/network/intents/submit
```

Запит містить `SignedIntent` і сумісний `BlockModel` envelope. Нода перевіряє intent signature, network id, timestamp/nonce replay protection, відповідність payload/channel/actor у block envelope, після чого передає блок у наявний PoC/Raft commit flow. Це дає стабільний контракт для Iroh forwarding, поки блок ще підписується клієнтом для сумісності з існуючим verify.

## Raft Transport Policy

Production consensus core підтримує `RAFT_TRANSPORT=Tcp` і `RAFT_TRANSPORT=Iroh`; обидва режими проходять product smoke з majority commit та відновленням кластера після restart. TCP залишається default для публічних стабільних серверів. Iroh consensus transport вимагає persistent sidecar identity, `RAFT_IROH_NODE_ID`, локальні control/listen endpoints і approved consensus peer mapping. Вузли за NAT, які не затверджені як consensus members, повинні працювати як Edge nodes: вони відправляють intents/proposals через Iroh forwarding і синхронізують committed log без участі в Raft majority.

Після запуску або оновлення кожна consensus node має повертати `operational=true` з `GET /api/consensus/raft/status`. Додатково перевіряються однаковий leader на всіх вузлах, `lastCommittedIndex == lastAppliedIndex`, `snapshot.stateMachineHealthy=true` та, після створення checkpoint, `snapshot.publishedSnapshotIndex == snapshot.currentSnapshotIndex`. Значення `currentSnapshotIndex` без `publishedSnapshotIndex` означає pending snapshot, який ще не можна вважати готовим до recovery.

## Update Policy

Правила оновлення описані в `deploy/update-policy.json`.

Основні вимоги:

- зберігати volume з SQLite database;
- зберігати `.NET` node identity;
- зберігати Iroh identity;
- оновлювати bootstrap-node по одному екземпляру за раз;
- оновлювати `edge-node` / `consensus-node` та `iroh-sidecar` однією версією;
- тримати `P2P_ALLOW_REGISTRATION_TOKEN_FALLBACK=false` у публічних мережах;
- виконувати rollback попереднім image tag з тими самими volume.

Перед оновленням:

```powershell
dotnet run --project Nexus.Setup -- validate --env deploy/edge-node.env
docker compose --env-file deploy/edge-node.env -f deploy/docker-compose.edge-node.yml config
docker compose --env-file deploy/edge-node.env -f deploy/docker-compose.edge-node.yml pull
```

Застосування оновлення:

```powershell
docker compose --env-file deploy/edge-node.env -f deploy/docker-compose.edge-node.yml up -d --build
```

Перевірка після оновлення:

- node identity configured;
- sync token configured;
- Iroh enabled для edge-node;
- signed registration active;
- bootstrap peer configured для edge-node;
- Docker containers перебувають у стані `healthy`.

Readiness endpoints:

- `GET /api/setup/status`;
- `GET /api/setup/version`;
- `GET /api/setup/migrations`;
- `GET /api/setup/update-check`.

## Iroh Relay Policy

Параметр `NEXUS_IROH_RELAY_MODE` задає relay-режим sidecar:

- `default` — стандартні production relay-налаштування Iroh;
- `staging` — staging relay для тестових мереж;
- `disabled` — пряме з'єднання без relay fallback.

Для публічної мережі значення relay mode фіксується в env-файлі. За використання власної relay-інфраструктури версія Iroh і relay map оновлюються разом із release manifest.

## Update Checker

Release manifest публікується через HTTPS. Приклад:

```powershell
UPDATE_MANIFEST_URL=https://updates.example.com/nexus/update-manifest.json
```

Формат manifest:

```json
{
  "nodeVersion": "0.1.0",
  "protocolVersion": "blockchain.proto:v1",
  "irohSidecarVersion": "0.1.0"
}
```

Нода порівнює manifest з поточними значеннями `NodeVersion`. UI показує попередження за наявності нової версії або несумісності protocol/sidecar.

## Smoke Tests

Локальний Docker-сценарій описаний у `docs/docker-p2p-smoke-runbook.md`.

Сценарій запуску двох фізичних машин описаний у `docs/two-machine-install-runbook.md`.
