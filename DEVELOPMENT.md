# Vaultix Entwicklung

## Toolchain

- .NET SDK 10.0.400 oder kompatibler Patch der 10.0-Linie
- Windows 10/11 für App und Service
- Linux oder Windows für den Server

## Qualitätsprüfung

```powershell
dotnet restore Vaultix.sln
dotnet build Vaultix.sln --configuration Debug --no-restore
dotnet test Vaultix.sln --configuration Debug --no-build
dotnet list Vaultix.sln package --vulnerable --include-transitive
```

Nullable, aktuelle Analyzer und Warnungen-als-Fehler sind solutionweit aktiv. Tests liegen getrennt in Core-, Server- und Integration-Projekten.

Die WPF-App zielt wegen der nativen SkiaSharp-WPF-Abhängigkeiten auf `net10.0-windows10.0.19041.0`. Live-Graphen verwenden LiveCharts2; Lizenz- und Versionshinweise stehen in `THIRD_PARTY_NOTICES.md`.

Transfermetriken werden intern sekündlich erfasst, live auf 300 Punkte begrenzt und dauerhaft nur minutenweise aggregiert. Geschwindigkeits- und ETA-Berechnung gehören in `VaultixMetricsService` beziehungsweise `Vaultix.Core`, nicht in ViewModels.

## Lokale Daten

`VAULTIX_DATA_DIR` überschreibt den Service-Datenpfad. Ohne Override nutzt der Windows-Dienst `%ProgramData%\Vaultix`. `VAULTIX_REPOSITORY` überschreibt den Repository-Pfad des Servers.

Keine echten Backupdaten, Queue-Datenbanken, Secrets oder Repositoryobjekte committen.
