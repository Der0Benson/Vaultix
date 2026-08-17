# Vaultix 0.1

Vaultix ist ein privates Backup-, Snapshot- und Recovery-System für einen Windows-PC und einen eigenen Ubuntu-Storage-Server. Der aktuelle Vertikalschnitt sichert ausgewählte Ordner durch den unabhängigen Windows-Dienst, speichert Inhalte dedupliziert auf dem Server, erstellt unveränderliche Snapshots und stellt Dateien bytegenau wieder her.

## Enthalten

- moderne WPF-Desktop-App mit Dashboard, Backup-Ordnern, Snapshot-Browser, Restore, Aktivitäten, Einstellungen und Tray
- Windows Worker Service als Source of Truth, Named-Pipe-IPC, `FileSystemWatcher`, 10-Sekunden-Debounce und stündlicher Kontrollscan
- persistente SQLite-Queue mit Zuständen und exponentiellem Retry
- Streaming-SHA-256 und Stabilitätsprüfung vor dem Upload
- ASP.NET Core API mit Device-Pairing und Device-Secret
- content-addressed Storage, File-Level-Deduplizierung, atomare Uploads und transaktionale Snapshot-Metadaten
- Restore-Streaming in einen Service-Stagingbereich; die Desktop-App kopiert anschließend mit Benutzerrechten an das gewählte Ziel
- sechs automatisierte Tests einschließlich vollständigem HTTP-Upload/Snapshot/Restore und echter Service-Pipeline

## Lokal starten

Voraussetzung ist das .NET 10 SDK.

```powershell
dotnet build Vaultix.sln
dotnet test Vaultix.sln --no-build
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

Unter **Einstellungen** `http://localhost:5192` verbinden, einen Ordner hinzufügen und ein Backup starten. HTTP wird absichtlich nur für Loopback akzeptiert; im LAN ist HTTPS erforderlich.

## Produktionsbetrieb

Server und Dienst zuerst mit `dotnet publish` veröffentlichen. Beispiele für Docker, systemd und Windows-Service-Registrierung liegen unter `deployment/`. Vor LAN-Betrieb ein echtes TLS-Zertifikat konfigurieren und den Server per Firewall ausschließlich für das Heimnetz freigeben.

Weitere Details: [Architektur](ARCHITECTURE.md), [Entwicklung](DEVELOPMENT.md), [API](API.md), [Repository-Format](REPOSITORY_FORMAT.md), [Sicherheit](SECURITY.md).

## Stand von 0.1

Der erste zuverlässige Datei-zu-Repository-zu-Restore-Pfad ist implementiert. Noch nicht Teil dieses Meilensteins sind VSS, Retention/GC, Resumable Uploads, Chunk-Deduplizierung, Client-seitige Verschlüsselung, Installer-GUI und vollständige Bare-Metal-Recovery.
