# Vaultix Architektur

## Komponenten

```text
Vaultix.App -- Befehls-Pipe ------> Vaultix.Service ---- HTTPS ----> Vaultix.Server
            <-- Status-Stream -----       |                            |
                                      Queue/Metrics SQLite        Repository SQLite
                                          |                            |
                                  Scanner / Watcher / EWMA       objects/aa/bb/hash
```

- `Vaultix.App`: WPF/MVVM; zeigt nur vom Dienst bestätigte Zustände und berechnet selbst keine Transfermetriken.
- `Vaultix.Service`: Source of Truth für Konfiguration, Queue, Scanner, Watcher, Hashing, Upload, Sessions, Metriken, Snapshot-Finalisierung und Restore-Staging.
- `Vaultix.Core`: transportfreie Modelle sowie EWMA- und ETA-Berechnung.
- `Vaultix.Infrastructure`: Dateisystemimplementierungen und HTTP-Streaming-Client.
- `Vaultix.Storage`: atomarer Object Store und transaktionale Repository-Metadaten.
- `Vaultix.Server`: versionierte ASP.NET Core API.
- `Vaultix.Shared`: Transportverträge für HTTP und IPC.

## Intelligenter Backup-Ablauf

1. Watcher-Ereignisse werden pro Ordner für die konfigurierte Ruhezeit gebündelt. Ein konfigurierbarer Reconciliation Scan rekonstruiert verpasste Ereignisse.
2. Ein Scan erzeugt einen persistenten Run. `RelativePath`, Größe und `LastWriteTimeUtc` werden mit `file_versions` verglichen.
3. Unveränderte Dateien werden nur als gesehen markiert. Neue oder geänderte Dateien gelangen in die Queue.
4. Vor dem Hashing prüft der Dienst nach 750 ms erneut Größe und Änderungszeit. Noch aktive Dateien werden mit Backoff erneut versucht.
5. Nur relevante Dateien werden per Streaming-SHA-256 gehasht. Ist der alte Hash identisch, entfällt sogar der Server-Check.
6. Bei neuem Hash fragt der Dienst den Content-Addressed Store. Nur fehlende Objekte werden gestreamt; Kopien und Umbenennungen erzeugen lediglich neue Referenzen.
7. Der Server hasht den Stream beim Schreiben, synchronisiert die temporäre Datei und verschiebt sie atomar. Erst die Bestätigung schließt den Queue-Job ab.
8. Automatische Checkpoints persistieren den aktuellen Serverzustand. Sichtbare Snapshots folgen dem Intervall; ohne Änderungen werden sie optional übersprungen.
9. Erst nach bestätigtem Objekt, Metadaten-Commit und Snapshot-Finalisierung wechselt die Session zu `Protected` und darf 100 % anzeigen.

Ein Run besitzt ein persistiertes `scan_completed`-Merkmal. Nach einem Abbruch während des Scans wird derselbe Run vollständig neu abgeglichen; ein partieller Snapshot ist dadurch ausgeschlossen. Netzwerkfehler lassen Jobs als `RetryScheduled` bestehen. Alte Snapshots werden durch lokale Löschungen nicht verändert.

## Metriken und ETA

`VaultixMetricsService` ist die einzige Berechnungsquelle für Upload, Restore, Dateien/s, Queue, Phase, Fortschritt und ETA. Ein Sample pro Sekunde wird im Speicher auf fünf Minuten begrenzt und zusätzlich als Minutenaggregat für 30 Tage gespeichert.

Momentangeschwindigkeiten verwenden eine exponentiell gewichtete gleitende Mittelung (EWMA, Upload/Download α = 0,25). Sie reagiert auf reale Änderungen, dämpft aber einzelne Spitzen. Die ETA nutzt eine zweite EWMA (α = 0,2), erscheint erst nach mindestens drei verwertbaren Samples und wird bei Stillstand bewusst als unbekannt gemeldet.

## IPC

Die Befehls-Pipe `Vaultix.Service.v1` verarbeitet kurze JSON-Anfragen. Die separate Pipe `Vaultix.Status.v1` streamt alle 250 ms kompakte Status-Snapshots. Teure Datenbank- und Dateisystemarbeit bleibt vollständig im Dienst; die WPF-App aktualisiert nur gebundene, virtualisierte Collections und höchstens 300 Live-Graphpunkte.
