# Удалифон API

CPU-only сервис удаления фона на **ASP.NET Core 10** с веб-кабинетом на обычном
**React + TypeScript**. API принимает JPEG/PNG/WebP, запускает BiRefNet через ONNX
Runtime и возвращает прозрачный PNG. Python и GPU не используются.

Новый аккаунт автоматически получает **1000 ₽**. Деньги списываются только после
успешной обработки; ошибочные запросы бесплатны.

## Реализовано

- чистый .NET 10, ASP.NET Core Minimal API;
- `birefnet-general-lite.onnx` через `Microsoft.ML.OnnxRuntime` CPU package;
- декодирование и формирование PNG через SkiaSharp с MIT-лицензией;
- аккаунты, несколько API-ключей и хранение только SHA-256-хешей ключей;
- баланс в копейках, ledger и журнал успешных/неуспешных обработок;
- атомарное списание после успешного получения результата;
- помесячные тарифные ступени 0,60 / 0,45 / 0,30 / 0,20 ₽;
- защищённое ручное пополнение баланса до подключения эквайринга;
- Swagger UI, Docker Compose, health-check и интеграционные тесты;
- React-кабинет со всем функционалом API;
- Nginx как единая точка входа и reverse proxy без отдельной настройки CORS.

## Архитектура

`docker compose` запускает два контейнера:

- `api` — ASP.NET Core API, доступный только внутри Docker-сети на порту 8000;
- `web` — production-сборка React в Nginx, опубликованная на порту 8000 хоста.

Nginx отдаёт кабинет с `/` и перенаправляет `/v1`, `/docs`, `/swagger` и `/health`
в контейнер API. Поэтому браузер и внешние клиенты используют один адрес.

## Запуск в Docker

```bash
cp .env.example .env
# Замените Admin__Token в .env на случайную длинную строку
docker compose -f compose.yaml -f compose.local.yaml up --build
```

### Развёртывание в Coolify

Создайте ресурс типа **Docker Compose** из репозитория и укажите только `compose.yaml`
(локальный override `compose.local.yaml` в Coolify подключать не нужно).
Публичный домен назначайте сервису `web` на внутренний порт `80`; сервис `api`
должен оставаться внутренним. Постоянные данные находятся в volumes
`removebg-data` и `removebg-models`.

В переменных окружения Coolify задайте как минимум:

```env
ASPNETCORE_ENVIRONMENT=Production
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

В кабинете доступны регистрация, баланс и тарифы, загрузка и скачивание изображения,
история запросов, создание и отзыв API-ключей, а также ручное административное
пополнение. API-ключ сохраняется в `localStorage` текущего браузера; admin-токен не
сохраняется.

При первой обработке сервер скачивает ONNX-модель в volume `removebg-models` и проверяет
её контрольную сумму. Последующие запуски используют сохранённый файл.

## Локальная разработка

Нужны .NET SDK 10 и Node.js 24+.

```bash
dotnet restore RemoveBg.slnx
dotnet run --project src/RemoveBg.Api
```

Во втором терминале:

```bash
cd web
npm install
npm run dev
```

Кабинет будет доступен на <http://localhost:5173>; Vite перенаправляет API-запросы на
<http://localhost:5000>. По умолчанию сервер использует `runtime/removebg.db`, а модель
сохраняет в `models/`.

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
| `Database__ConnectionString` | `Data Source=runtime/removebg.db` | строка подключения SQLite |
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
dotnet build RemoveBg.slnx
dotnet test RemoveBg.slnx
dotnet format RemoveBg.slnx --verify-no-changes
cd web && npm run build
```

Интеграционные тесты подменяют ML-обработчик, поэтому не скачивают модель.

## Перед публичным запуском

Открытая регистрация сейчас намеренно начисляет каждому email 1000 ₽. Для закрытого
пилота это удобно, но перед публичным запуском необходимо добавить подтверждение email,
CAPTCHA/лимиты регистрации, rate limiting, EF Core migrations, резервное копирование и
наблюдаемость. Для нескольких экземпляров API SQLite нужно заменить на PostgreSQL.
