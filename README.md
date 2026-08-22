# Vaultix 0.2

Vaultix ist ein privates Backup-, Snapshot- und Recovery-System für einen Windows-PC und einen eigenen Ubuntu-Storage-Server. Der unabhängige Windows-Dienst überwacht ausgewählte Ordner dauerhaft, sichert Änderungen intelligent und stellt Dateien aus unveränderlichen Snapshots bytegenau wieder her.

## Enthalten

- Continuous Protection mit `FileSystemWatcher`, konfigurierbarem Debounce und Reconciliation Scan (Standard: 30 Minuten)
- inkrementeller Metadatenvergleich; SHA-256 und Upload erfolgen nur bei tatsächlich relevanten Änderungen
- persistente SQLite-Queue, Wiederanlauf nach Abbruch und kontrollierte Wiederholungen bei Offline-Servern
- content-addressed Storage mit File-Level-Deduplizierung, atomaren Uploads und transaktionalen Snapshot-Metadaten
- automatische Snapshots/Checkpoints mit konfigurierbarem Intervall und Unterdrückung leerer Snapshots
- zentrale Session- und Transfermetriken mit EWMA-Glättung, stabilisierter ETA und Minutenaggregaten
- WPF-Dashboard mit echtem Live-Graph, Upload-/Restore-Geschwindigkeit, Fortschritt, Queue, Zeitplanung und Sitzungsverlauf
- kompakter Named-Pipe-Statusstrom mit vier UI-Aktualisierungen pro Sekunde; die App liest keine Service-Datenbank
- automatisierte Core-, Server- und Integrationstests für Deduplizierung, intelligente Erkennung, Queue-Persistenz, Metriken und Restore

## Lokal starten

Voraussetzung ist das .NET 10 SDK auf Windows 10/11.

```powershell
dotnet restore Vaultix.sln
dotnet build Vaultix.sln --no-restore
dotnet test Vaultix.sln --no-build --no-restore
dotnet run --project src/Vaultix.Server --urls http://localhost:5192
```

In einem zweiten Terminal:

```powershell
$env:VAULTIX_DATA_DIR="$PWD/.dev/service"
dotnet run --project src/Vaultix.Service
```

Danach:

```powershell
dotnet run --project src/Vaultix.App
```

Unter **Einstellungen** `http://localhost:5192` verbinden und unter **Backup** einen Ordner hinzufügen. HTTP wird absichtlich nur für Loopback akzeptiert; im LAN ist HTTPS erforderlich. Continuous Protection und der erste Kontrollscan starten danach automatisch.

## Produktionsbetrieb

Server und Dienst zuerst mit `dotnet publish` veröffentlichen. Beispiele für Docker, systemd und Windows-Service-Registrierung liegen unter `deployment/`. Vor LAN-Betrieb ein echtes TLS-Zertifikat konfigurieren und den Server per Firewall ausschließlich für das Heimnetz freigeben.

Weitere Details: [Architektur](ARCHITECTURE.md), [Entwicklung](DEVELOPMENT.md), [API](API.md), [Repository-Format](REPOSITORY_FORMAT.md), [Sicherheit](SECURITY.md), [Drittanbieterhinweise](THIRD_PARTY_NOTICES.md).

Noch nicht Teil dieses Meilensteins sind VSS, Retention/GC, Resumable Uploads, Chunk-Deduplizierung, clientseitige Verschlüsselung, Installer-GUI und Bare-Metal-Recovery.
