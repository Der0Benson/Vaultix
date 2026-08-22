# Vaultix Repository Format 0.1

```text
repository/
├── objects/<sha[0..2]>/<sha[2..4]>/<sha256>
├── database/vaultix.db
├── temp/*.upload
├── metadata/
└── logs/
```

Storage Objects sind unveränderliche Rohbytes. Der Dateiname ist der kleingeschriebene SHA-256-Wert des vollständigen Inhalts. Ein Upload gilt erst nach serverseitiger Hashprüfung, Flush und atomarem Move als vorhanden.

SQLite enthält `devices`, `storage_objects`, `snapshots` und `snapshot_entries`. Mehrere Einträge dürfen dasselbe Objekt referenzieren. Snapshot-Einträge speichern relativen Pfad, Hash, Größe, Zeitstempel und Attribute. `snapshots.is_checkpoint` unterscheidet interne Continuous-Protection-Checkpoints von sichtbaren Intervall-Snapshots. Es gibt absichtlich keine Remote-Löschoperation.

Das Format ist als Version 0.1 zu behandeln. Künftige Migrationen müssen vor einer Formatänderung transaktional und rückwärts prüfbar implementiert werden.
