# SpaceAccess.Wpf — Starter
Minimal WPF + .NET 8 access logger. Logs student check-in/out to SQLite and shows live occupancy.

## Build
1. Install .NET 8 SDK and Visual Studio 2022+ with .NET Desktop workload.
2. Open `SpaceAccess.Wpf.csproj` in Visual Studio, or run:

```powershell
dotnet restore
dotnet run -c Release
```

## Publish single-file .exe
```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```
Output: `bin\Release\net8.0-windows\win-x64\publish\SpaceAccess.Wpf.exe`

## Data
SQLite DB at `%LOCALAPPDATA%\SpaceAccess\access.sqlite`.

## Notes
- Manual entry simulates a scanner. Replace calls in `RecordEvent` with device SDK callback.
- Export CSV button writes `access_log.csv` to Desktop.
