# Episode Prewarm — Roadmap

Ziel: Relevante Media Items vorausschauend in den Dateisystem-/Storage-Cache laden, damit der eigentliche Playback-Start möglichst direkt aus dem Cache bedient werden kann.

Jellyfin 12.4 · .NET 10 · Standard-Preload 1 MiB · Cooldown 6 Stunden pro Media Item

## Status

| Bereich | Status |
|---|---|
| Playback-Progress-Trigger bei konfigurierbarem Schwellwert | Implementiert |
| Nächste Episode ermitteln (inkl. Staffelwechsel) | Implementiert |
| Asynchroner Worker und zentrale Preload-Verarbeitung | Implementiert |
| Persistenter, globaler Cooldown pro Media Item | Implementiert |
| HTTP-Detailseiten-Hook und Container-Auflösung | Implementiert (Runtime-Test offen) |
| Session-Start-Trigger mit User-Wiedergabekontext | Implementiert (Runtime-Test offen) |
| Einheitliche Trigger-/Candidate-/Queue-Architektur | Implementiert |
| Single-Source-Versionierung für Assembly und Releases | Implementiert |

## Session-Start-Verhalten

Bei jeder neu gestarteten Jellyfin-Session wird ein kleiner, userbezogener Lookup auf die zuletzt gespielten Episoden und Filme ausgeführt. Die zehn zuletzt gespielten Items werden höchstens geladen; ihre User-Daten werden gebündelt abgefragt.

- Letzte Episode unter dem konfigurierten Schwellwert: dieselbe Episode.
- Letzte Episode ab dem Schwellwert: nächste verfügbare Episode, auch über Staffelgrenzen.
- Letzter Film unter dem Schwellwert: derselbe Film.
- Letzter Film ab dem Schwellwert bzw. als abgeschlossen markiert: kein Preload.
- Kein Wiedergabeverlauf oder kein verwertbarer Kontext: kein Kandidat; es wird nicht willkürlich eine Serie aus der Bibliothek ausgewählt.
- Der globale Cooldown bleibt sechs Stunden pro Ziel-Media-Item und gilt über alle Benutzer und Trigger hinweg.

## Versionierung

Directory.Build.props ist die einzige manuell zu ändernde Versionsquelle (PluginVersion). Assembly-, File- und Informational-Version werden daraus abgeleitet. Der Release-Workflow übernimmt dieselbe Version automatisch in meta.json, build.yaml, ZIP-Dateinamen, Git-Tag und Manifest. Für einen Versionssprung muss daher nur PluginVersion geändert werden.

## Verifikation

- [ ] Plugin-Build und Plugin-Load unter Jellyfin 12.4.
- [ ] SessionStarted wird beim Anlegen einer neuen Session ausgelöst und im Plugin registriert.
- [ ] Session-Start mit Episode unter/über Schwellwert.
- [ ] Session-Start mit Film unter/über Schwellwert.
- [ ] Kein Wiedergabeverlauf führt zu keinem zufälligen Bibliotheks-Preload.
- [ ] Globaler Cooldown verhindert doppelte Reads über mehrere User/Clients.
- [ ] Detailseiten- und Playback-Trigger bleiben unverändert.
- [ ] Assembly-Version entspricht PluginVersion in Directory.Build.props.
