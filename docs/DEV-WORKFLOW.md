# Development workflow

How features are built, isolated, reviewed and released. Product spec: `docs/SPEC.md`; visual language:
`docs/DESIGN.md` (read "No AI clichés"); motion and gestures: `docs/MOTION.md`; components: `docs/UI-KIT.md`.

## Branches

| Branch | Meaning |
|---|---|
| `main` | The released version. Phones and the server update themselves from it (see "Releases"). Only the integrator merges into it, when a release is approved by the owner. |
| `develop` | Integration of finished features. Always builds and passes the tests. |
| `feature/<name>` | One feature, branched from `develop`, merged back into `develop` by the integrator after review. |

Rules: small focused commits; messages in English, imperative, explaining why; end every commit message with
`Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push to `main` from a feature; never rewrite
published history; never commit secrets, APKs or build output.

## Working on a feature in parallel with others

Each feature runs in its own git worktree and is isolated at runtime too:

- **Build output**: `--artifacts-path artifacts/<feature>` (never another feature's folder, never `artifacts/lead-api`).
- **Database**: its own PostgreSQL database on the shared dev server:
  `ConnectionStrings__Default="Host=localhost;Port=5432;Database=skymessage_<feature>;Username=skymessage;Password=skymessage"`.
  Migrations apply on startup (`Server:MigrateOnStartup`).
- **API port**: the one assigned to the feature (never 8080 — that is the lead's preview server).
- **Run**:
  ```bash
  export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
  dotnet build src/Server/SkyMessage.Api/SkyMessage.Api.csproj --artifacts-path artifacts/<feature> -v q -nologo
  cd artifacts/<feature>/bin/SkyMessage.Api/debug && ASPNETCORE_ENVIRONMENT=Development \
    ConnectionStrings__Default="...Database=skymessage_<feature>..." dotnet SkyMessage.Api.dll --urls http://127.0.0.1:<port>
  python3 tools/dev/seed-dev.py --base http://127.0.0.1:<port>      # dev accounts + a short chat
  ```
- **Look at the UI**: `node tools/ui-shot/shot.mjs --base http://127.0.0.1:<port> --out <dir> --steps steps.json --theme light|dark`
  (header of the script documents the steps; it logs in as artur_dev). `python3 tools/ui-shot/as-mama.py "text" --base ...`
  sends an incoming message. Read the PNGs; check light and dark, phone and `--width 1280 --height 800`.
- Stop your server when done. Never touch the Android emulator, adb or any real server unless your brief says so.

## Server changes

- Layers: `SkyMessage.Domain` (entities, rules) → `SkyMessage.Application` (use cases, interfaces) →
  `SkyMessage.Infrastructure` (EF Core, storage, LiveKit) → `SkyMessage.Api` (controllers, SignalR hub).
  DTOs and route constants live in `src/Shared/SkyMessage.Contracts` (`ApiRoutes.cs`).
- One EF migration per feature, named after it:
  `dotnet tool restore && dotnet ef migrations add <Feature> -p src/Server/SkyMessage.Infrastructure -s src/Server/SkyMessage.Api -o Persistence/Migrations`.
  Never edit an existing migration. If `develop` gained a migration meanwhile, the integrator re-creates yours on top.
- Realtime: new events go through the existing SignalR hub and client event dispatch; keep them idempotent.
- Privacy: anything about a person (birthday, last seen, avatar, stories) honours that person's privacy settings and blocks.

## Client changes

- `SkyMessage.Client.Core`: API clients, stores, services (no UI). `SkyMessage.Client.UI`: Razor components, `wwwroot`
  JS/CSS. `SkyMessage.App`: MAUI Android/iOS host and platform services. `SkyMessage.Web`: the WebAssembly host.
- Blazor: `@bind:get/@bind:set`, never `value=@x` with a manual `@oninput`. Scoped CSS: `::deep` for child components,
  no CSS nesting. Reuse kit components and tokens; add new shared pieces to the kit only when the brief allows.
- Text: every string through `Localization/{ru,en}/*.json`, same keys in both (small Edit insertions, never rewrite files
  other features also touch).
- Platform features (Android) go behind an interface in `SkyMessage.Client.Core.Platform` with a web fallback.

## Tests

```bash
for t in UnitTests Client.Core.Tests Api.IntegrationTests; do
  dotnet build tests/SkyMessage.$t --artifacts-path artifacts/<feature>-tests -v q -nologo
done
dotnet test --project tests/SkyMessage.UnitTests --no-build -p:ArtifactsPath=$PWD/artifacts/<feature>-tests
dotnet test --project tests/SkyMessage.Client.Core.Tests --no-build -p:ArtifactsPath=$PWD/artifacts/<feature>-tests
dotnet test --project tests/SkyMessage.Api.IntegrationTests --no-build -p:ArtifactsPath=$PWD/artifacts/<feature>-tests  # Docker
```

`global.json` runs tests on Microsoft.Testing.Platform: `dotnet test` takes the project with `--project` and forwards
any option it does not know (`-nologo`, `--artifacts-path`, ...) to the test app, which rejects it (exit code 5) and
the run reports "Zero tests ran". Build with your options first, then `dotnet test --project ... --no-build` (the
`ArtifactsPath` property tells it where that build is). `dotnet run --project tests/<X>` also runs a suite.

Every feature adds tests for its server rules and client stores. A feature is done when it builds with 0 errors and
0 warnings, all three test projects pass, the UI has been looked at in both themes, and the branch is committed.

## Releases

Merging `develop` into `main` is a release (only with the owner's go-ahead). CI (`.github/workflows/ci.yml`) builds
and tests every push and pull request; on `main`, `release.yml` runs CI, builds the signed APK and publishes it with
its version manifest, then deploys the server; installed apps and open web pages notice the new version and update
themselves. The release number is the commit count of `main` (version `0.2.<n>`, Android versionCode `<n>`), so `main`
is never rewritten. Secrets, the server side and the whole flow: `deploy/vps/README.md`, "CI, releases and auto-update".
