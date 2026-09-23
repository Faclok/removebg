# Удалифон API

CPU-only сервис удаления фона на **ASP.NET Core 10** с веб-кабинетом на обычном
**React + TypeScript**. API принимает JPEG/PNG/WebP, запускает BiRefNet через ONNX
Runtime и возвращает прозрачный PNG. Python и GPU не используются.

Новый аккаунт автоматически получает **1000 ₽**. Деньги списываются только после
успешной обработки; ошибочные запросы бесплатны.

## Реализовано

- чистый .NET 10, ASP.NET Core Minimal API;
- MySQL 8.4 и EF Core 10 с автоматическими migrations при запуске;
- `birefnet-general-lite.onnx` через `Microsoft.ML.OnnxRuntime` CPU package;
- декодирование и формирование PNG через SkiaSharp с MIT-лицензией;
- аккаунты, несколько API-ключей и хранение только SHA-256-хешей ключей;
- баланс в копейках, ledger и журнал успешных/неуспешных обработок;
- атомарное списание после успешного получения результата;
- помесячные тарифные ступени 0,60 / 0,45 / 0,30 / 0,20 ₽;
- защищённое ручное пополнение баланса до подключения эквайринга;
- Swagger UI, Docker Compose, health-check и интеграционные тесты;
- полноразмерный React-кабинет: пакетная очередь до 100 файлов, дашборд,
  фильтруемая история с CSV-экспортом, управление ключами, тарифный калькулятор,
  административное пополнение и настройки сессии;
- Nginx как единая точка входа и reverse proxy без отдельной настройки CORS.

## Архитектура

`docker compose` запускает три контейнера:

- `mysql` — MySQL 8.4 с постоянным volume;
- `api` — ASP.NET Core API, доступный только внутри Docker-сети на порту 8000;
- `web` — production-сборка React в Nginx; локальный override публикует её на порту
  8000 хоста.

Nginx отдаёт кабинет с `/` и перенаправляет `/v1`, `/docs`, `/swagger` и `/health`
в контейнер API. Поэтому браузер и внешние клиенты используют один адрес.

## Структура репозитория

- `server/` — .NET solution, исходники API, тесты и Dockerfile сервера;
- `client/` — React-приложение, Nginx и Dockerfile клиента;
- `docs/` — документация проекта;
- в корне находятся только конфигурационные файлы Compose, Git, env и Makefile.

## Запуск в Docker

```bash
cp .env.example .env
# Замените пароли MySQL и Admin__Token в .env
docker compose -f compose.yaml -f compose.local.yaml up --build
```

### Развёртывание в Coolify

Создайте ресурс типа **Docker Compose** из репозитория и укажите только `compose.yaml`
(локальный override `compose.local.yaml` в Coolify подключать не нужно).
Публичный домен назначайте сервису `web` на внутренний порт `80`; сервис `api`
должен оставаться внутренним. Постоянные данные находятся в volumes
`removebg-mysql` и `removebg-models`. Сервису `mysql` публичный домен или host-port
назначать не нужно.

В переменных окружения Coolify задайте как минимум:

```env
ASPNETCORE_ENVIRONMENT=Production
MYSQL_DATABASE=removebg
MYSQL_USER=removebg
MYSQL_PASSWORD=замените-на-длинный-пароль
MYSQL_ROOT_PASSWORD=замените-на-другой-длинный-пароль
Admin__Token=замените-на-длинную-случайную-строку
```

Переменные объявлены непосредственно в `compose.yaml`, поэтому отдельный файл `.env`
на сервере не требуется. После разбора Compose они появятся в разделе
**Environment Variables** ресурса Coolify.

Dockerfile автоматически публикует нативные зависимости только для архитектуры
сервера (`amd64` или `arm64`). Это существенно уменьшает размер API-образа и расход
диска во время сборки.

После запуска:

- веб-кабинет: <http://localhost:8000/>
- Swagger UI: <http://localhost:8000/docs>
- OpenAPI JSON: <http://localhost:8000/swagger/v1/swagger.json>
- readiness: <http://localhost:8000/health/ready>

В кабинете доступны регистрация, оперативный дашборд, пакетная загрузка и скачивание
результатов, история до 200 последних запросов с поиском, фильтрами, деталями и
CSV-экспортом, создание и отзыв API-ключей, калькулятор тарифов, настройки подключения,
а также ручное административное пополнение. Очередь отправляет изображения
последовательно, чтобы не создавать неконтролируемую нагрузку на CPU. API-ключ
сохраняется в `localStorage` текущего браузера; admin-токен не сохраняется.

При первой обработке сервер скачивает ONNX-модель в volume `removebg-models` и проверяет
её контрольную сумму. Последующие запуски используют сохранённый файл.

## Локальная разработка

Нужны .NET SDK 10, Node.js 24+ и MySQL 8.4. Проще всего сначала запустить MySQL
из Compose:

```bash
cp .env.example .env
docker compose -f compose.yaml -f compose.local.yaml up -d mysql
```

Для запуска API вне Docker замените `Database__ConnectionString` на строку с
`Server=localhost` или используйте значение из `appsettings.json`.

```bash
cd server
dotnet restore RemoveBg.slnx
dotnet run --project src/RemoveBg.Api
```

Во втором терминале:

```bash
cd client
npm install
npm run dev
```

Кабинет будет доступен на <http://localhost:5173>; Vite перенаправляет API-запросы на
<http://localhost:5000>. Модель сохраняется в `models/`. При запуске API EF Core
автоматически применяет недостающие migrations к MySQL.

## Первый запрос

### Регистрация

```bash
curl -sS http://localhost:8000/v1/accounts/register \
  -H 'Content-Type: application/json' \
  -d '{"name":"ООО Ромашка","email":"api@example.ru"}'
```

Ответ содержит `apiKey` и `balanceRub: 1000.00`. Секрет ключа показывается только один
раз.

### Удаление фона

```bash
curl -sS http://localhost:8000/v1/remove-background \
  -H 'X-API-Key: rb_live_ВАШ_КЛЮЧ' \
  -F 'image=@product.jpg' \
  --output product-no-bg.png \
  --dump-header -
```

Вместо `X-API-Key` поддерживается `Authorization: Bearer rb_live_...`. В заголовках
ответа возвращаются `X-Charged-Rub`, `X-Balance-Rub`, размеры оригинала и
`X-Request-ID`.

Полный перечень методов находится в [API.md](API.md).

## Конфигурация

Настройки можно задавать в `appsettings.json` или переменными окружения .NET с двойным
подчёркиванием:

| Переменная | По умолчанию | Назначение |
|---|---:|---|
| `Database__ConnectionString` | MySQL на `localhost:3306` | строка подключения MySQL |
| `MYSQL_DATABASE` | `removebg` | база, создаваемая MySQL-контейнером |
| `MYSQL_USER` | `removebg` | пользователь MySQL |
| `MYSQL_PASSWORD` | обязательна | пароль приложения |
| `MYSQL_ROOT_PASSWORD` | обязательна | root-пароль MySQL |
| `Accounts__StartingBalanceKopecks` | `100000` | 1000 ₽ новому аккаунту |
| `Removal__ModelPath` | `models/birefnet-general-lite.onnx` | локальный ONNX-файл |
| `Removal__MaxUploadBytes` | `15728640` | максимум 15 MiB |
| `Removal__MaxImagePixels` | `40000000` | предел количества пикселей |
| `Removal__CpuWorkers` | `2` | параллельные инференсы |
| `Removal__IntraOpThreads` | `4` | CPU-потоки одного инференса |
| `Admin__Token` | пусто | включает административное пополнение |

## Тарификация

Цена следующей обработки зависит от успешного объёма аккаунта в текущем календарном
месяце UTC:

| Объём | Цена |
|---:|---:|
| 0–9 999 | 0,60 ₽ |
| 10 000–99 999 | 0,45 ₽ |
| 100 000–499 999 | 0,30 ₽ |
| от 500 000 | 0,20 ₽ |

## Проверка

```bash
cd server
dotnet build RemoveBg.slnx
dotnet test RemoveBg.slnx
dotnet format RemoveBg.slnx --verify-no-changes
cd ..
cd client && npm run build
```

Интеграционные тесты подменяют ML-обработчик и используют временную SQLite только как
тестовый provider, поэтому не требуют MySQL и не скачивают модель.

## Перед публичным запуском

Открытая регистрация сейчас намеренно начисляет каждому email 1000 ₽. Для закрытого
пилота это удобно, но перед публичным запуском необходимо добавить подтверждение email,
CAPTCHA/лимиты регистрации, rate limiting, регулярное резервное копирование MySQL и
наблюдаемость.
