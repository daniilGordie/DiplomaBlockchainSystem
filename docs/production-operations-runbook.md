# Production Operations Runbook

Документ описує підготовку, запуск і оновлення production-пакетів Nexus blockchain node.

## Початкове налаштування

Bootstrap-node:

```powershell
dotnet run --project Nexus.Setup -- setup bootstrap --public-url https://bootstrap.example.com
```

Full-node:

```powershell
dotnet run --project Nexus.Setup -- setup full-node --bootstrap https://bootstrap.example.com:7141
```

Після генерації env-файлу потрібно перевірити публічні URL, `ORACLE_PUBLIC_KEY`, токени доступу та шляхи до постійних volume.

## Запуск

Bootstrap-node:

```powershell
docker compose --env-file deploy/bootstrap-node.env -f deploy/docker-compose.bootstrap.yml up -d --build
```

Full-node з Iroh sidecar:

```powershell
docker compose --env-file deploy/full-node.env -f deploy/docker-compose.full-node.yml up -d --build
```

Перевірка контейнерів:

```powershell
docker compose --env-file deploy/bootstrap-node.env -f deploy/docker-compose.bootstrap.yml ps
docker compose --env-file deploy/full-node.env -f deploy/docker-compose.full-node.yml ps
```

Перевірка node endpoints:

```powershell
curl.exe -fsS https://bootstrap.example.com/api/setup/status
curl.exe -fsS http://localhost:7042/api/setup/status
curl.exe -fsS http://localhost:7042/api/setup/migrations
```

## Setup Wizard

Вкладка Network в UI містить майстер налаштування ноди. Майстер генерує env-файли для трьох режимів:

- local node;
- full-node з підключенням до bootstrap-node;
- bootstrap-node.

Форма перевіряє обов'язкові URL і викликає `POST /api/setup/plan`. Endpoint генерує env-вміст з новими секретами та повертає файл для завантаження.

## Update Policy

Правила оновлення описані в `deploy/update-policy.json`.

Основні вимоги:

- зберігати volume з SQLite database;
- зберігати `.NET` node identity;
- зберігати Iroh identity;
- оновлювати bootstrap-node по одному екземпляру за раз;
- оновлювати `full-node` та `iroh-sidecar` однією версією;
- тримати `P2P_ALLOW_REGISTRATION_TOKEN_FALLBACK=false` у публічних мережах;
- виконувати rollback попереднім image tag з тими самими volume.

Перед оновленням:

```powershell
docker compose --env-file deploy/full-node.env -f deploy/docker-compose.full-node.yml config
docker compose --env-file deploy/full-node.env -f deploy/docker-compose.full-node.yml pull
```

Застосування оновлення:

```powershell
docker compose --env-file deploy/full-node.env -f deploy/docker-compose.full-node.yml up -d --build
```

Перевірка після оновлення:

- node identity configured;
- sync token configured;
- Iroh enabled для full-node;
- signed registration active;
- bootstrap peer configured для full-node;
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
