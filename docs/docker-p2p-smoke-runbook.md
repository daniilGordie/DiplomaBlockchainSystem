# Docker P2P Smoke Test

Документ описує локальну перевірку P2P-мережі в Docker. Сценарій запускає один bootstrap-node, дві full-node та окремий Iroh sidecar для кожної full-node.

## Склад стенда

- `bootstrap-node` — каталог відомих peer-нод;
- `full-node-a` — перша full-node;
- `full-node-a-iroh` — Iroh sidecar для першої full-node;
- `full-node-b` — друга full-node;
- `full-node-b-iroh` — Iroh sidecar для другої full-node.

Перевіряються реєстрація нод, видача peer directory, робота Iroh sidecar і readiness endpoints.

## Запуск

```powershell
docker compose -p nexus-iroh-smoke -f docker-compose.iroh-demo.yml up --build -d
```

Перевірка стану контейнерів:

```powershell
docker compose -p nexus-iroh-smoke -f docker-compose.iroh-demo.yml ps
```

Усі сервіси мають перейти у стан `healthy`.

## Перевірка статусу нод

```powershell
curl.exe -fsS http://localhost:7041/api/setup/status
curl.exe -fsS http://localhost:7042/api/setup/status
curl.exe -fsS http://localhost:7043/api/setup/status
```

Очікуваний результат:

- bootstrap-node повертає `role=Bootstrap`;
- обидві full-node повертають `role=Full`;
- для обох full-node вказано `irohEnabled=true`;
- для обох full-node заповнено `irohNodeId`.

## Перевірка готовності до оновлення та міграцій

```powershell
curl.exe -fsS http://localhost:7041/api/setup/migrations
curl.exe -fsS http://localhost:7042/api/setup/migrations
curl.exe -fsS http://localhost:7043/api/setup/migrations
```

Критичні перевірки мають бути успішними:

- `database-path`;
- `node-identity-path`;
- `sync-token`;
- `signed-registration`;
- `bootstrap-peers`;
- `iroh-sidecar` для full-node;
- `iroh-relay` для full-node.

## Перевірка журналів

```powershell
docker compose -p nexus-iroh-smoke -f docker-compose.iroh-demo.yml logs --tail=200 bootstrap-node
docker compose -p nexus-iroh-smoke -f docker-compose.iroh-demo.yml logs --tail=200 full-node-a
docker compose -p nexus-iroh-smoke -f docker-compose.iroh-demo.yml logs --tail=200 full-node-b
```

У журналах bootstrap-node мають бути записи про реєстрацію `full-node-a` і `full-node-b`.

## Зупинка стенда

```powershell
docker compose -p nexus-iroh-smoke -f docker-compose.iroh-demo.yml down
```

Видалення контейнерів разом із volume-даними:

```powershell
docker compose -p nexus-iroh-smoke -f docker-compose.iroh-demo.yml down -v
```
