# Project: vahter-bot

## Testing

- Run tests with: `dotnet test --logger "console;verbosity=detailed"`
- When tests fail, always check container logs in `test-artifacts/` folder (app.log, postgres.log, flyway.log) for the actual error

## F# conventions

- Use `%` prefix operator instead of `|> ignore` for discarding values
