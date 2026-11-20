# 🚀 Episode Prewarm — Roadmap

> **Ziel:** Relevante Media Items vorausschauend in den Dateisystem-/Storage-Cache laden, damit der eigentliche Playback-Start möglichst direkt aus dem Cache bedient werden kann.
>
> Jellyfin **12.4** · .NET **10** · Standard-Preload **1 MiB** · Cooldown **6 Stunden pro Media Item**

## Status

| Bereich | Status |
|---|---|
| Playback-Progress-Trigger bei konfigurierbarem Schwellwert | ✅ Implementiert |
| Nächste Episode ermitteln (inkl. Staffelwechsel) | ✅ Implementiert |
| Asynchroner Worker und zentrale Preload-Verarbeitung | ✅ Implementiert |
| Persistenter, globaler Cooldown pro Episode | ✅ Implementiert |
| Jellyfin-API für Session-Start und User-Wiedergabestatus geprüft | ✅ Verifiziert |
| HTTP-Detailseiten-Hook und Client-Request-Varianten untersucht | ✅ Verifiziert (Build/Runtime noch offen) |
| Detailseiten-Trigger | 🟡 Geplant |
| Prewarm beim Start einer neuen Session / Rückkehr online | 🟡 Geplant |
| Vereinheitlichte Kandidatenauswahl für alle Trigger | 🟡 Geplant |

Legende: ✅ implementiert/verifiziert · 🟡 geplant · 🔄 in Arbeit · ⬜ offen

---

## Trigger-Roadmap

### 1. Playback-Progress — bestehendes Verhalten

- [x] Auf Playback-Progress-Events hören.
- [x] Bei Erreichen des konfigurierbaren Schwellwerts (Standard: **90 %**) die nächste verfügbare Episode ermitteln.
- [x] Die Episode asynchron in die Preload-Queue stellen.
- [x] Staffelwechsel berücksichtigen.
- [x] Keine weitere Episode laden, wenn keine nächste Episode existiert.

### 2. Detailseite einer Serie

- [ ] Ohne Wiedergabeverlauf die erste verfügbare Episode laden (bevorzugt **S01E01**).
- [ ] Ist S01E01 nicht vorhanden oder nicht lesbar, die nächste verfügbare Episode suchen.
- [ ] Mit Wiedergabeverlauf anhand des letzten Episoden-Fortschritts entscheiden:
  - Unter 90 %: zuletzt geschaute Episode.
  - Ab 90 %: nächste verfügbare Episode.

### 3. Detailseite einer Episode

- [ ] Beim Öffnen genau diese Episode prewarmen, unabhängig vom Wiedergabestatus.

### 4. Detailseite eines Films

- [ ] Beim Öffnen genau diesen Film prewarmen.
- [ ] Es gilt der normale globale Item-Cooldown.

### 5. Detailseiten weiterer Media Items / Bibliotheken

- [ ] Detailseiten-Trigger auf alle unterstützten, dateibasierten Media Items erweitern.
- [ ] Bei Container-Items ohne eigene Mediendatei (z. B. Staffeln, Sammlungen oder Serien) einen konkreten Media-Kandidaten bestimmen.
- [ ] Reine Bildabrufe (Poster/Backdrop) nicht als eigenständigen Preload-Trigger behandeln.

### 6. Neue Session / User kommt wieder online

Bei jeder neu gestarteten Jellyfin-Session wird bewusst ein Preload-Kandidat ermittelt. Das ist ausdrücklich kein passiver Trigger, der nur bei unmittelbar bevorstehender Wiedergabe greift.

**Serien / Episoden:**
- [ ] Letzte geschaute Episode unter 90 %: dieselbe Episode prewarmen.
- [ ] Letzte geschaute Episode ab 90 %: nächste verfügbare Episode prewarmen.
- [ ] Kein Wiedergabeverlauf: sofern eine Serie als Kontext bekannt ist, erste verfügbare Episode laden (S01E01, andernfalls nächste verfügbare Episode).

**Filme:**
- [ ] Letzter Film unter 90 %: denselben Film prewarmen.
- [ ] Letzter Film ab 90 %: nichts weiter prewarmen.

**Allgemein:**
- [ ] Pro Session nur einen passenden Kandidaten bestimmen und in die zentrale Queue stellen.
- [ ] Keine userbezogenen Cache-Einträge anlegen: Cache und Cooldown bleiben global pro Media Item.

---

## Phase A — Technische Grundlagen prüfen

### A1. Detailseiten-Requests: untersuchte Clients und Matcher

Die Client-Quellen belegen folgende Request-Formen beim Aufruf einer konkreten Detailansicht:

| Client | Quellcode-Verhalten | Jellyfin-Request |
|---|---|---|
| Jellyfin Web | `apiClient.getItem(currentUserId, itemId)` in `src/apps/legacy/controllers/itemDetails/index.js` | `GET /Items/{itemId}?userId=...` |
| Streamyfin | `getItems({ ids: [itemId], userId })` in `hooks/useItemQuery.ts` | `GET /Items?ids={singleId}&userId=...` |
| Wholphin | Episode- und Movie-ViewModels rufen `api.libraryApi.getItem(itemId)` beim Initialisieren auf | `GET /Items/{itemId}` |

Quellnachweise (untersuchte Commits):
- Jellyfin Web: [itemDetails/index.js](https://github.com/jellyfin/jellyfin-web/blob/380dc4526c2a4630a24408a34124c3d9df7ab1b9/src/apps/legacy/controllers/itemDetails/index.js)
- Streamyfin: [useItemQuery.ts](https://github.com/streamyfin/streamyfin/blob/68af07d44394510b32172a65b07dd1d57a6b7295/hooks/useItemQuery.ts)
- Wholphin Episode: [EpisodeViewModel.kt](https://github.com/damontecres/Wholphin/blob/fea40c5bcf9a172a7c12a88886a7cef70353cd68/app/src/main/java/com/github/damontecres/wholphin/ui/detail/episode/EpisodeViewModel.kt)
- Wholphin Movie: [MovieViewModel.kt](https://github.com/damontecres/Wholphin/blob/fea40c5bcf9a172a7c12a88886a7cef70353cd68/app/src/main/java/com/github/damontecres/wholphin/ui/detail/movie/MovieViewModel.kt)

**Geplanter Request-Matcher:**
1. Nur HTTP `GET`.
2. Einzel-Item-Pfad mit gültiger GUID: `/Items/{itemId}` oder Legacy `/Users/{userId}/Items/{itemId}`.
3. Zusätzlich `/Items?ids={singleGuid}`, wenn `ids` genau eine gültige GUID enthält.
4. Mehrfach-IDs, normale Listen-/Suchabfragen, Bilder sowie Playback-/Stream-Endpunkte nicht berücksichtigen.
5. Erst nach erfolgreicher Antwort (2xx) den Kandidaten an die zentrale Queue melden. Request-Pipeline und Response-Body bleiben unverändert.

### A2. Bewusste Grenze: Startseiten und Listen

**Ergebnis und Entscheidung:** Startseiten- und Bibliothekslisten werden nicht als Trigger verwendet. Solche Ansichten laden typischerweise Listen über Collection-/Query-Endpunkte und nicht für jedes sichtbare Medium einzeln die Detailroute. Der oben beschriebene Matcher ignoriert diese Listen daher bewusst.

Das bedeutet ausdrücklich: Wird ein Medium direkt aus einer Startseiten- oder Listen-Kachel abgespielt, ohne dass zuvor ein passender Einzel-Item-Request erfolgt, kann dieser Prewarm-Trigger den Zugriff verpassen. Das ist eine akzeptierte Abdeckungslücke. Wir versuchen nicht, jedes sichtbare Item einer Startseite vorzuladen, da dies unnötig viele Reads und Queue-Einträge erzeugen würde.

**Wichtige Einschränkung:** Der HTTP-Endpunkt verrät nicht zuverlässig, warum ein Client ein einzelnes Item abruft. Wholphin lädt ein Item beispielsweise auch nach Änderungen des Wiedergabestatus erneut; Streamyfin verwendet dieselbe Items-API auch außerhalb einer Detailansicht. Echte Detailansichten lassen sich deshalb nicht zu 100 % von sonstigen Einzel-Item-Abfragen unterscheiden. Referer und User-Agent sind dafür keine zuverlässige clientübergreifende Lösung.

**Pragmatische Entscheidung:** Erfolgreiche Einzel-Item-Metadatenabrufe als Kandidaten behandeln; Listen, Suche, Bilder und Playback ausschließen. Die globale 6h-Cooldown- und In-Flight-Deduplizierung begrenzen Wiederholungen. Falls im Laufzeitbetrieb störende False Positives auftreten, können später gezielte Filter ergänzt werden.

### A3. Middleware-Hook in Jellyfin 12.4

- Jellyfin ruft `IPluginServiceRegistrator.RegisterServices` während `CoreAppHost.Init` auf, bevor der Webhost gebaut und gestartet wird. Darüber kann ein ASP.NET-Core-`IStartupFilter` registriert werden.
- Jellyfin 12.4 verwendet eine reguläre ASP.NET-Core-`Startup`-Pipeline. Ein `IStartupFilter` kann Middleware ergänzen, ohne Jellyfins Startup-Klasse oder Controller zu ersetzen.
- Middleware kann den Request vor `next` klassifizieren und nach `next` ausschließlich bei 2xx den Kandidaten melden. Kein Response-Body muss gelesen, gepuffert oder verändert werden.
- Für Compile-Time-Typen wie `IStartupFilter` / `IApplicationBuilder` wird voraussichtlich `FrameworkReference Include="Microsoft.AspNetCore.App"` benötigt.

**Ergebnis:** Der Hook ist auf Architektur-/Quellcode-Ebene grundsätzlich möglich. Plugin-Build und tatsächlicher Plugin-Load/Runtimetest sind noch offen; die Middleware ist damit noch nicht als lauffähig verifiziert.

### A4. Jellyfin-12.4-APIs für Session-Start und User-Wiedergabestatus

- `ISessionManager.SessionStarted` ist vorhanden und liefert `SessionEventArgs`; über `args.SessionInfo` stehen unter anderem `UserId`, `UserName`, `DeviceId` und `Client` zur Verfügung.
- Das Event tritt beim Erzeugen einer neuen Session auf. Normale Aktivitätsmeldungen innerhalb derselben Session lösen es nicht erneut aus. Mehrere Geräte eines Users können jeweils eigene Sessions erzeugen.
- `IUserManager.GetUserById(Guid)` löst den User auf.
- `IUserDataManager.GetUserData(User, BaseItem)` liefert unter anderem `PlaybackPositionTicks`, `Played` und `LastPlayedDate`.
- Userbezogene Bibliotheksabfragen sind über `InternalItemsQuery(user)` möglich, einschließlich Filterung nach Item-Typ, Sortierung nach `DatePlayed` und Limitierung.

**Offene Verifikation:** Die konkrete Query für den letzten Episodenkontext muss bei der Implementierung gegen Resume- und Completed-Fälle getestet werden. Für die 90-%-Entscheidung muss die gespeicherte Position gegen die Laufzeit des Items ausgewertet werden; `Played` allein reicht dafür nicht.

### Phase-A-Ergebnis

Die API- und Client-Quellenprüfung ist abgeschlossen. Der Detailseiten-Ansatz ist technisch plausibel, aber noch nicht gebaut oder zur Laufzeit getestet. Der Matcher deckt bewusst konkrete Einzel-Item-Abfragen ab und nicht Startseiten-/Listen-Requests. Diese Einschränkung ist eine bewusste Produktentscheidung, kein noch offener Implementierungsfehler.

---

## Phase B — Einheitliche Trigger- und Kandidatenlogik

- [ ] Gemeinsames Trigger-/Request-Modell für Playback, Detailseite und Session-Start definieren.
- [ ] Kandidatenauswahl für Serienverlauf, Resume-Fälle und Filme zentralisieren.
- [ ] Serien-Episodenfolge inklusive fehlender Dateien und Staffelwechsel robust auflösen.
- [ ] Globale Cooldown- und In-Flight-Prüfung vor dem Queue-Eintrag vereinheitlichen.
- [ ] Trigger-Quelle intern nachvollziehbar halten (Playback, Detailseite, Session-Start), ohne unnötig ausführliche Logs zu erzeugen.

## Phase C — Trigger implementieren

- [ ] Detailseiten-Middleware integrieren.
- [ ] Detailseiten-Trigger für Serien, Episoden, Filme und weitere unterstützte Media Items aktivieren.
- [ ] Session-Start-Trigger integrieren.
- [ ] Bestehenden 90-%-Playback-Trigger an die gemeinsame Queue-/Kandidatenlogik anbinden.

## Phase D — Verifikation

- [ ] Plugin-Build und Plugin-Load mit registriertem `IStartupFilter`.
- [ ] Einzel-Item-Requests der drei Clients werden erkannt.
- [ ] Listen-, Startseiten-, Such-, Bild- und Playback-Requests lösen keinen Detailseiten-Trigger aus.
- [ ] Zwei User öffnen dasselbe Item: nur ein globaler Preload innerhalb des Cooldowns.
- [ ] Ungesehene Serie: S01E01 bzw. nächste verfügbare Episode.
- [ ] Episode / Film: genau dieses Item.
- [ ] Session-Start mit Episode unter 90 %: dieselbe Episode; ab 90 %: nächste verfügbare Episode.
- [ ] Session-Start mit Film unter 90 %: derselbe Film; ab 90 %: kein Preload.
- [ ] Fehlende S01E01-Datei: nächste verfügbare Episode.
- [ ] Parallele Trigger erzeugen keine doppelten Reads.
- [ ] Fehlgeschlagener Read setzt keinen Cooldown.
- [ ] Playback und HTTP-Requests werden nicht durch Prewarm-Arbeit blockiert.

---

## Gemeinsame technische Leitplanken

- Eine zentrale Queue / ein Worker: Trigger melden nur Kandidaten; Dateiarbeit läuft im Hintergrund.
- Globaler Cooldown: 6 Stunden pro Media Item, nicht pro User oder Client.
- In-Flight-Deduplizierung: denselben laufenden Preload nicht parallel starten.
- Cooldown nur nach erfolgreichem Preload setzen/aktualisieren.
- Fehlgeschlagene Preloads dürfen bei späterem Trigger erneut versucht werden.
- Vor dem Lesen prüfen, ob das Item eine konkrete, erreichbare Mediendatei besitzt.
- Pro erfolgreichem Preload weiterhin maximal 1 MiB lesen.
- Trigger blockieren weder Playback-Event-Handler noch HTTP-Requests.

**Cache-Semantik:** Ein erfolgreicher Preload wärmt das Media Item serverseitig. Der Nutzen ist für alle User und Clients gleich. Es gibt keine getrennten Cooldowns oder Preload-Daten pro User.

---

## Bewusst nicht Teil des Umfangs

- Kein Prewarm kompletter Serien, Staffeln oder Bibliotheken.
- Keine getrennten Caches oder Cooldowns pro User.
- Kein zusätzlicher ffprobe-Aufruf und keine Transcodierung.
- Keine Änderung am eigentlichen Playback- oder Streaming-Pfad.
- Kein massenhaftes Vorladen aller auf Startseiten oder in Listen sichtbaren Medien.

## Entscheidungslog

| Entscheidung | Festlegung |
|---|---|
| Preload-Größe | Maximal 1 MiB pro erfolgreichem Preload |
| Cooldown | 6 Stunden pro Media Item, global |
| Playback-Schwellwert | Konfigurierbar, Standard 90 % |
| Session-Start | Bewusst aggressiv: pro neuer Session einen passenden Kandidaten ermitteln |
| Episode unter 90 % | Dieselbe Episode erneut laden |
| Episode ab 90 % | Nächste verfügbare Episode laden |
| Film unter 90 % | Derselbe Film erneut laden |
| Film ab 90 % | Kein weiterer Preload |
| Ungesehene Serie | S01E01, sonst nächste verfügbare Episode |
| Detailseite | Konkretes Media Item laden; Container auf konkreten Kandidaten auflösen |
| Startseite / Listen | Kein Trigger; direkte Wiedergabe ohne vorherigen Einzel-Item-Request kann verpasst werden |
| Cache-Isolation | Keine User-Isolation; ein Item-Cache für alle |
