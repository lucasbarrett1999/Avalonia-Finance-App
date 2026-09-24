# Build environment notes

## Agent sandbox (Linux container)

- .NET SDK is installed per-user at `/root/.dotnet` (10.0.401). Every shell must export:
  `export PATH=/root/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`
- Only Linux can be verified here. Windows and macOS are verified by the GitHub Actions matrix in `.github/workflows/ci.yml`.
- Headless Avalonia tests run without a display. Do not try to launch the windowed app here.
- Outbound HTTPS goes through a proxy; NuGet restore works. Plaid sandbox calls also work if keys are present in the environment.

## Developer machines

- Install the .NET 10 SDK. `global.json` pins the major version.
- `dotnet build`, `dotnet test`, `dotnet run --project src/Keel.Desktop`.
