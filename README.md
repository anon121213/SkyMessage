# SkyMessage

Частный мессенджер для семьи и друзей: личные и групповые чаты, фото/видео/файлы, голосовые и
видеосообщения-«кружки», аудио- и видеозвонки (1:1 и групповые), гибкие настройки. Сервер — свой
(VPS), весь контент хранится в PostgreSQL.

## Состав

| Часть | Технологии |
|---|---|
| Сервер | ASP.NET Core 10 (слои Domain / Application / Infrastructure / Api), EF Core + PostgreSQL, SignalR, LiveKit (звонки), FFmpeg |
| Клиенты | .NET MAUI Blazor Hybrid — Android, iOS, macOS (общий UI на Razor) + веб-версия (Blazor WebAssembly) |
| Инфраструктура | Docker Compose: postgres, livekit, api |

Подробная техническая спецификация: [docs/SPEC.md](docs/SPEC.md).

## Запуск локально

Нужны Docker Desktop и .NET SDK 10 (`~/.dotnet`).

```bash
cp .env.example .env            # и поставь LIVEKIT_NODE_IP = IP этого Mac в локальной сети
docker compose up -d db livekit # PostgreSQL + сервер звонков
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet run --project src/Server/SkyMessage.Api   # API + веб-клиент на http://localhost:8080
```

Или целиком в Docker: `docker compose up -d --build`.

Android (эмулятор видит компьютер как `10.0.2.2`):

```bash
dotnet build src/Client/SkyMessage.App -f net10.0-android -t:Run \
  -p:AndroidSdkDirectory=$HOME/Library/Android/sdk \
  "-p:JavaSdkDirectory=/Applications/Android Studio.app/Contents/jbr/Contents/Home"
```

Отладочная сборка использует Fast Deployment: сборки .NET заливаются на устройство отдельно, поэтому
APK из `bin/` нельзя просто поставить через `adb install`, он закроется при старте. Ставьте через `-t:Run`
или `-t:Install`. APK, который можно переслать на телефон, собирается в Release с адресом сервера:
`-c Release -p:SkyMessageDefaultServerUrl=https://ваш-домен/`. Это понадобится, когда появится VPS.

На реальном телефоне в той же сети: `adb reverse tcp:8080 tcp:8080`, и приложение увидит сервер как `localhost:8080`.

Для ручной проверки на локальном сервере удобно завести два аккаунта, например в браузере и на
эмуляторе. Логины тестовых аккаунтов локальной базы лежат в `tools/e2e/dev-accounts.json`.

## Тесты

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet test --project tests/SkyMessage.UnitTests               # сервер: логика
dotnet test --project tests/SkyMessage.Api.IntegrationTests    # сервер: API на настоящем PostgreSQL (нужен Docker)
dotnet test --project tests/SkyMessage.Client.Core.Tests       # клиент: хранилища, авторизация, медиа
python3 tools/e2e/smoke.py                                     # живой прогон против запущенного API (нужен ffmpeg)
```

## Почему не заблокируют и не засудят

- Свой бренд, свои иконки (Lucide, ISC), свои обои и звуки — ничего от Telegram.
- Только свободные лицензии (MIT/Apache/BSD/ISC); FFmpeg — внешний процесс на сервере.
- Весь трафик — обычный HTTPS на 443 к одному домену: REST, SignalR (WebSocket → SSE → long polling),
  медиа и сигналинг звонков. Несколько адресов сервера с автопереключением, свой адрес и прокси в
  настройках. Никакого VPN и доступа к чужим ресурсам — только собственный сервис.
- Трафик шифруется TLS, медиа на диске сервера — AES-256-GCM. Сквозное шифрование (секретные чаты)
  заложено в план.

## Дорожная карта

v1 — всё, что описано выше, локально в Docker. Дальше: развёртывание на VPS (домен, TLS, TURN на 443),
push-уведомления (FCM/APNs), системный экран входящего звонка, iOS/macOS-полировка, секретные чаты,
каналы, стикеры, опросы, папки чатов.
