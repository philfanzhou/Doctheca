# Contributing to Doctheca

Thank you for improving Doctheca. Keep changes focused, preserve public contracts unless the change is explicitly breaking, and write contributor-facing text in English.

## Development workflow

1. Direct pushes to `main` are blocked; every change lands through a pull request.
2. Build and test the solution and the admin console.
3. Add or update tests for behavior changes.
4. Update documentation and configuration examples when behavior changes.
5. Open a pull request describing the problem, the approach, compatibility impact, and verification performed.

```bash
dotnet build src/Doctheca.sln --configuration Release
dotnet test src/Doctheca.sln --configuration Release --no-build
cd frontend && npm ci && npm run build
```

Docker is required to build the container image.

## Commits and pull requests

Commit messages and pull request titles use the [Conventional Commits](https://www.conventionalcommits.org/) format in English, for example `feat: filter documents by subject` or `fix: keep parse blocks consistent after a re-parse`. Use a body to explain why the change is needed when the subject alone does not.

Use the issue and pull request templates. Issue titles and the bodies of issues and pull requests are written in Chinese.

## Database changes

Doctheca does not use EF Core migrations. The PostgreSQL schema is maintained by the SQL-based `DatabaseInitializer` in `src/Database` (idempotent table creation plus column migrations). Schema changes must keep the initializer idempotent and existing databases upgradeable in place.

## Security

Do not open public issues for vulnerabilities or include live secrets in tests, logs, screenshots, or pull requests. Follow [SECURITY.md](SECURITY.md) for private reporting.
