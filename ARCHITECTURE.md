# Vaultix Architektur

## Komponenten

```text
Vaultix.App -- Named Pipe --> Vaultix.Service --> HTTPS --> Vaultix.Server
                                  |                         |
                             Queue SQLite             Repository SQLite
                                  |                         |
                         Scanner / Watcher          objects/aa/bb/hash
```

- `Vaultix.App`: WPF/MVVM. Zeigt ausschließlich vom Dienst bestätigte Zustände; führt keine Backups direkt aus.
- `Vaultix.Service`: Windows-Service, Konfiguration, Queue, Scanner, Watcher, Hashing, Upload, Snapshot-Finalisierung und Restore-Staging.
- `Vaultix.Core`: UI- und transportfreie Modelle, Policies und Ports.
- `Vaultix.Infrastructure`: Dateisystemimplementierungen und HTTP-Client.
- `Vaultix.Storage`: atomarer Object Store und transaktionale Repository-Metadaten.
- `Vaultix.Server`: versionierte ASP.NET Core API.
- `Vaultix.Shared`: reine Transportverträge für HTTP und IPC.

## Backup-Ablauf

1. Watcher-Ereignisse werden zehn Sekunden gebündelt; zusätzlich läuft ein Kontrollscan.
2. Ein Scan erzeugt einen persistenten Run und Queue-Einträge.
3. Vor dem Hashing werden Größe und Änderungszeit erneut geprüft.
4. SHA-256 wird über einen asynchronen `FileStream` berechnet.
5. Der Server beantwortet den Object-Check. Nur fehlende Hashes werden gestreamt.
6. Der Server hasht den Stream beim Schreiben, synchronisiert die temporäre Datei und verschiebt sie atomar.
7. Erst nach erfolgreichem Upload wird die lokale Dateiversion als abgeschlossen markiert.
8. Sind alle Einträge bestätigt, erstellt der Server den Snapshot in einer DB-Transaktion.
9. Erst danach zeigt Vaultix „Alles gesichert“.

Netzwerkfehler hinterlassen Queue-Einträge als `RetryScheduled`. Aktive Zustände werden beim Neustart auf `Pending` zurückgesetzt. Alte Server-Snapshots werden durch lokale Löschungen nicht verändert.

## IPC

Die App sendet eine JSON-Anfrage pro Verbindung über `Vaultix.Service.v1`. Der Dienst akzeptiert Status-, Konfigurations-, Backup- und Restore-Befehle. Restore-Ziele werden nicht mit Dienstrechten beschrieben: Der Dienst lädt in einen kontrollierten Stagingordner, die App kopiert anschließend mit Benutzerrechten.
