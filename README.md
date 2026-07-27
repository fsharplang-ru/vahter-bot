# VahterBanBot

Telegram bot for spam deletion and administrative functions in Russian-speaking
F# community chats.

Commands: `/ban` (delete + global ban), `/sban [hours]` (soft-ban/mute),
`/unban <user_id>`, `/ban ping` (health check).

Spam detection combines a trained ML model with an LLM triage fallback.

## ⚠️ This repository is a read-only mirror

Development happens in a private monorepo alongside two sibling bots; this repo
is synced from it automatically by a bot and exists for community visibility and
code review. **Commits pushed directly to `main` here will be overwritten by the
next sync.**

Every sync arrives as a `sync/monorepo-<sha>` pull request. CI on this repo
builds the code and runs the full test suite, so the mirror is independently
buildable and testable — but it does not deploy anything. Deployment happens
exclusively from the monorepo.

Contributions and bug reports are welcome as issues and pull requests: a
maintainer ports accepted changes into the monorepo, and they flow back here on
the next sync.

## Building and testing

Requires the .NET SDK pinned in `global.json` and a working Docker daemon (the
integration suite spins up PostgreSQL, Flyway, the bot itself, and fake
Telegram/Azure APIs via Testcontainers).

```
dotnet build -c Release
dotnet test tests/VahterBanBot.Unit.Tests/VahterBanBot.Unit.Tests.fsproj -c Release
dotnet test tests/SerializationCompat.Tests/SerializationCompat.Tests.fsproj -c Release
dotnet test tests/VahterBanBot.Tests/VahterBanBot.Tests.fsproj -c Release
```

`dotnet test` accepts only one project per invocation, hence the three commands.
When an integration test fails, container logs are written to
`test-artifacts/<ProjectName>/<Fixture>/`.

## Layout

```
src/BotInfra/       shared bot infrastructure library
src/VahterBanBot/   the application
src/vahter-bot/     Helm chart + Flyway migrations
src/Dockerfile.bot  multi-stage image build (BOT_PROJECT build arg)
tests/              integration, unit and serialization-compat suites,
                    shared test infra, and the fake Telegram/Azure APIs
```
