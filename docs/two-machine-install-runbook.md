# Two Machine Install Runbook

Документ описує запуск спільної Nexus blockchain-мережі на двох машинах. Перша машина розміщує bootstrap-node, друга запускає full-node та Iroh sidecar.

## Топологія

- Machine A: bootstrap-node.
- Machine B: full-node та Iroh sidecar.
- Bootstrap-node має бути доступним з Machine B через HTTPS/gRPC.
- Full-node реєструється на bootstrap-node з підписом постійної node identity.
- Iroh sidecar забезпечує P2P-транспорт і relay fallback.

Для постійної мережі bootstrap-node розміщується на сервері зі стабільним DNS, TLS і збереженими volume-даними.

## Machine A: Bootstrap Node

Створити env-файл:

```powershell
dotnet run --project Nexus.Setup -- setup bootstrap --public-url https://bootstrap.example.com
```

Перевірити значення в `deploy/bootstrap-node.env`:

- `P2P_PUBLIC_URL=https://bootstrap.example.com`;
- `NODE_HTTP_PORT=7041`;
- `NODE_GRPC_PORT=7141`;
- `P2P_ALLOW_REGISTRATION_TOKEN_FALLBACK=false`;
- `ORACLE_PUBLIC_KEY` відповідає ключу мережі.

Запустити bootstrap-node:

```powershell
docker compose --env-file deploy/bootstrap-node.env -f deploy/docker-compose.bootstrap.yml up --build -d
```

Перевірити стан:

```powershell
docker compose --env-file deploy/bootstrap-node.env -f deploy/docker-compose.bootstrap.yml ps
curl.exe -fsS https://bootstrap.example.com/api/setup/status
```

Контейнер має бути `healthy`, endpoint `/api/setup/status` має повертати `role=Bootstrap`.

## Machine B: Full Node

Створити env-файл:

```powershell
dotnet run --project Nexus.Setup -- setup full-node --bootstrap https://bootstrap.example.com:7141
```

Перевірити значення в `deploy/full-node.env`:

- `P2P_BOOTSTRAP_GRPC_URL=https://bootstrap.example.com:7141`;
- `IROH_RELAY_MODE=default`;
- `P2P_ALLOW_REGISTRATION_TOKEN_FALLBACK=false`;
- `ORACLE_PUBLIC_KEY` відповідає ключу мережі.

Запустити full-node:

```powershell
docker compose --env-file deploy/full-node.env -f deploy/docker-compose.full-node.yml up --build -d
```

Перевірити стан:

```powershell
docker compose --env-file deploy/full-node.env -f deploy/docker-compose.full-node.yml ps
curl.exe -fsS http://localhost:7042/api/setup/status
curl.exe -fsS http://localhost:7042/api/setup/migrations
```

Очікуваний результат:

- контейнер `full-node` перебуває у стані `healthy`;
- контейнер `iroh-sidecar` перебуває у стані `healthy`;
- `/api/setup/status` повертає `role=Full`;
- `/api/setup/status` повертає `irohEnabled=true`;
- `/api/setup/status` містить непорожній `irohNodeId`;
- `/api/setup/migrations` не містить неуспішних обов'язкових перевірок.

## UI

Запустити UI та вказати HTTP URL full-node:

```text
http://localhost:7042
```

Вкладка Network має відображати:

- режим ноди;
- fingerprint node identity;
- стан Iroh sidecar;
- relay status;
- migration readiness checks;
- список відомих peer-нод.

## Додавання наступних full-node

Для кожної нової машини повторюється налаштування Machine B. Кожна нода має власні постійні identity-файли:

- `P2P_IDENTITY_KEY_PATH`;
- `IROH_SECRET_KEY_PATH`.

Ці файли не копіюються між машинами. Втрата identity-файлу призводить до появи нової node identity.

## Експлуатаційні параметри

Параметри мережі:

- публічний bootstrap-node з DNS і TLS;
- постійні volume для SQLite database, node identity та Iroh identity;
- HTTPS-адреса update manifest;
- relay policy в env-файлах;
- healthcheck endpoints для контейнерів.
