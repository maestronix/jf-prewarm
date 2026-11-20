# 🚀 Episode Prewarm — Roadmap

> **Ziel:** Relevante Media Items vorausschauend in den Dateisystem-/Storage-Cache laden, damit der eigentliche Playback-Start möglichst direkt aus dem Cache bedient werden kann.
>
> Jellyfin **12.4** · .NET **10** · Standard-Preload **1 MiB** · Cooldown **6 Stunden pro Media Item**

## 📍 Status

| Bereich | Status |
|---|---|
| Playback-Progress-Trigger bei konfigurierbarem Schwellwert | ✅ Implementiert |
| Nächste Episode ermitteln (inkl. Staffelwechsel) | ✅ Implementiert |
| Asynchroner Worker und zentrale Preload-Verarbeitung | ✅ Implementiert |
| Persistenter, globaler Cooldown pro Episode | ✅ Implementiert |
| Jellyfin-API für Session-Start und User-Wiedergabestatus geprüft | ✅ Verifiziert |
| Detailseiten-Trigger für alle unterstützten Media-Bibliotheken | 🟡 Geplant |
| Prewarm beim Start einer neuen Session / Rückkehr online | 🟡 Geplant |
| Vereinheitlichte Kandidatenauswahl für alle Trigger | 🟡 Geplant |

Legende: ✅ vorhanden / verifiziert · 🟡 geplant · 🔄 in Arbeit · ⬜ offen

---

## 🎯 Trigger-Roadmap

### 1. Playback-Progress — bestehendes Verhalten

- [x] Auf Playback-Progress-Events hören.
- [x] Bei Erreichen des konfigurierbaren Schwellwerts (Standard: **90 %**) die nächste verfügbare Episode ermitteln.
- [x] Die Episode asynchron in die Preload-Queue stellen.
- [x] Staffelwechsel berücksichtigen.
- [x] Keine weitere Episode laden, wenn keine nächste Episode existiert.

### 2. Detailseite einer Serie

Wenn ein User die Detailseite einer Serie öffnet:

- [ ] Bei einer Serie ohne Wiedergabeverlauf die erste verfügbare Episode laden (bevorzugt **S01E01**).
- [ ] Ist S01E01 nicht vorhanden oder nicht lesbar, die nächste verfügbare Episode suchen.
- [ ] Bei vorhandenem Wiedergabeverlauf den Kandidaten anhand des letzten Episoden-Fortschritts bestimmen:
  - Unter 90 %: zuletzt geschaute Episode.
  - Ab 90 %: nächste verfügbare Episode.

### 3. Detailseite einer Episode

- [ ] Beim Öffnen der Detailseite einer Episode genau diese Episode prewarmen.
- [ ] Unabhängig davon, ob sie bereits teilweise oder vollständig geschaut wurde.

### 4. Detailseite eines Films

- [ ] Beim Öffnen der Film-Detailseite genau diesen Film prewarmen.
- [ ] Auch Filme mit vorhandenem Wiedergabefortschritt unterliegen dem normalen globalen Item-Cooldown.

### 5. Detailseiten weiterer Media Items / Bibliotheken

- [ ] Detailseiten-Trigger nicht auf Serien beschränken.
- [ ] Alle unterstützten, dateibasierten Media Items berücksichtigen (z. B. Filme, Episoden und weitere abspielbare Bibliotheksinhalte).
- [ ] Bei Container-Items ohne eigene Mediendatei (z. B. Staffeln, Sammlungen oder Serien) einen passenden konkreten Media-Kandidaten bestimmen, statt den Container selbst zu öffnen.
- [ ] Reine Metadaten-, Poster-, Backdrop- und sonstige Bildabrufe nicht als eigenständigen Preload-Kandidaten behandeln.

### 6. Neue Session / User kommt wieder online

Bei jeder neu gestarteten Jellyfin-Session wird bewusst ein Preload-Kandidat ermittelt. Das ist ausdrücklich **kein** passiver Trigger, der nur bei unmittelbar bevorstehender Wiedergabe greift.

**Serien / Episoden:**

- [ ] Letzte geschaute Episode unter 90 %: genau diese Episode erneut prewarmen.
- [ ] Letzte geschaute Episode ab 90 %: nächste verfügbare Episode prewarmen.
- [ ] Kein Wiedergabeverlauf: sofern eine Serie als Kontext bekannt ist, erste verfügbare Episode laden (S01E01, andernfalls nächste verfügbare Episode).

**Filme:**

- [ ] Letzter Film unter 90 %: denselben Film erneut prewarmen.
- [ ] Letzter Film ab 90 %: nichts weiter prewarmen.

**Allgemein:**

- [ ] Pro Session nur einen passenden Kandidaten bestimmen und in die zentrale Queue stellen.
- [ ] Keine userbezogenen Cache-Einträge anlegen: Der Cache und Cooldown bleiben global pro Media Item.

---

## ⚙️ Gemeinsame technische Leitplanken

Diese Regeln gelten für **jeden** Trigger — unabhängig davon, ob er aus Playback, einer Detailseite oder dem Session-Start kommt.

- [x] **Eine zentrale Queue / ein Worker:** Trigger melden nur einen Kandidaten; die eigentliche Dateiarbeit läuft im Hintergrund.
- [x] **Globaler Cooldown pro Media Item:** 6 Stunden, nicht pro User und nicht pro Client.
- [x] **In-Flight-Deduplizierung:** Ein bereits laufender Preload desselben Items wird nicht parallel erneut gestartet.
- [ ] Cooldown nur nach erfolgreichem Preload setzen bzw. aktualisieren.
- [ ] Fehlgeschlagene Preloads dürfen bei einem späteren Trigger erneut versucht werden.
- [ ] Vor dem Lesen prüfen, ob das Item eine konkrete, erreichbare Mediendatei besitzt.
- [ ] Alle Trigger auf dieselbe Kandidaten- und Queue-API routen; keine separaten Preload-Implementierungen pro Trigger.
- [ ] Trigger-Quelle intern nachvollziehbar halten (Playback, Detailseite, Session-Start), ohne unnötig ausführliche Logs zu erzeugen.

**Cache-Semantik:** Ein erfolgreicher Preload wärmt das Media Item serverseitig. Der Nutzen ist für alle User und Clients gleich. Es gibt keine getrennten Cooldowns oder Preload-Daten pro User.

**Ressourcenprofil:** Pro erfolgreichem Preload werden weiterhin nur bis zu **1 MiB** gelesen. Die Trigger blockieren weder HTTP-Requests noch Playback-Event-Handler.

---

## 🧭 Umsetzungsphasen

### Phase A — Technische Grundlagen prüfen

- [x] Jellyfin-12.4-API für Session-Start und User-Wiedergabestatus verifizieren.
- [x] Geeigneten Hook für HTTP-Detailseiten-Aufrufe verifizieren (ohne Response-Body-Manipulation).
- [ ] Relevante Request-Varianten der Clients berücksichtigen: direkte Item-Abfrage und Einzel-ID-Abfrage über die Items-Collection.
- [ ] Sicherstellen, dass nur echte Einzel-Item-Detailabfragen triggern — keine Bibliothekslisten, Suche, Bilder oder Playback-Requests.

#### Verifizierter HTTP-Hook für Detailseiten

- **Plugin-Einstiegspunkt:** Jellyfin ruft `IPluginServiceRegistrator.RegisterServices` während `CoreAppHost.Init` auf — also bevor der Webhost gebaut und gestartet wird. Der Plugin-Registrator kann damit einen ASP.NET-Core-`IStartupFilter` in der ServiceCollection registrieren.
- **Pipeline-Einbindung:** Jellyfin 12.4 verwendet eine reguläre ASP.NET-Core-`Startup`-Klasse. Ein `IStartupFilter` kann die bestehende `Startup.Configure`-Pipeline um eine Middleware umschließen, ohne Jellyfins Startup-Klasse oder Controller zu ersetzen.
- **Verhalten:** Die Middleware kann den Request vor `next` klassifizieren und nach `next` ausschließlich bei erfolgreichem HTTP-Status (2xx) einen Kandidaten an die vorhandene Queue melden. Kein Response-Body muss gelesen, gepuffert oder verändert werden.
- **Abhängigkeit:** Das Plugin benötigt für die Compile-Time-Typen `IStartupFilter` / `IApplicationBuilder` eine Referenz auf das ASP.NET-Core-Shared-Framework (voraussichtlich `FrameworkReference Include="Microsoft.AspNetCore.App"`). Das muss beim Plugin-Build und im Jellyfin-Plugin-Load-Kontext verifiziert werden.

**Ergebnis:** Ein eigener HTTP-Middleware-Hook ist auf Jellyfin 12.4 grundsätzlich möglich; ein Austausch von Jellyfins `Startup` oder ein Eingriff in den Response-Body ist nicht erforderlich. Die konkreten Request-Matcher und der Build-/Runtime-Test bleiben offen.

#### Verifizierte Jellyfin-12.4-APIs

- **Session-Start:** `ISessionManager.SessionStarted` ist vorhanden und liefert `SessionEventArgs`; über `args.SessionInfo` stehen unter anderem `UserId`, `UserName`, `DeviceId` und `Client` zur Verfügung.
- **Semantik:** Jellyfin feuert `SessionStarted` beim Erzeugen einer neuen Session. Die Session wird anhand von Client/App, Device-ID und User-ID identifiziert. Normale Aktivitätsmeldungen innerhalb derselben Session lösen dieses Event nicht erneut aus. Mehrere Geräte eines Users können daher jeweils einen eigenen Trigger erzeugen.
- **User-Auflösung:** `IUserManager.GetUserById(Guid)` liefert den zugehörigen User.
- **Wiedergabestatus:** `IUserDataManager.GetUserData(User, BaseItem)` liefert `UserItemData` mit `PlaybackPositionTicks`, `Played` und `LastPlayedDate`.
- **Kandidatenabfrage:** Jellyfin unterstützt userbezogene Bibliotheksabfragen über `new InternalItemsQuery(user)`, inklusive `IncludeItemTypes`, `OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)]` und `Limit`. Damit lässt sich die zuletzt gespielte Episode gezielt abfragen, statt die komplette Bibliothek im Plugin zu durchsuchen.

**Wichtige Einschränkung:** Die API-Verfügbarkeit ist verifiziert. Die konkrete Query muss bei der Implementierung noch gegen echte Resume- und Completed-Fälle getestet werden — insbesondere, ob Jellyfins DatePlayed-Sortierung für den gewünschten „letzten Episodenkontext“ alle relevanten Fälle korrekt abbildet. Für die Unterscheidung unter/ab 90 % muss zusätzlich die gespeicherte Position gegen die Laufzeit des konkreten Items ausgewertet werden; `Played` allein ist dafür kein zuverlässiger Ersatz.

### Phase B — Einheitliche Trigger- und Kandidatenlogik
- [ ] Gemeinsames Trigger-/Request-Modell für Playback, Detailseite und Session-Start definieren.
- [ ] Kandidatenauswahl für Serienverlauf, Resume-Fälle und Filme zentralisieren.
- [ ] Serien-Episodenfolge inklusive fehlender Dateien und Staffelwechsel robust auflösen.
- [ ] Globale Cooldown- und In-Flight-Prüfung vor dem Queue-Eintrag vereinheitlichen.

### Phase C — Trigger implementieren
- [ ] Detailseiten-Middleware bzw. geeigneten Jellyfin-Hook integrieren.
- [ ] Detailseiten-Trigger für Serien, Episoden, Filme und weitere unterstützte Media Items aktivieren.
- [ ] Session-Start-Trigger integrieren.
- [ ] Bestehenden 90-%-Playback-Trigger auf die gemeinsame Queue umstellen bzw. daran anbinden.

### Phase D — Verifikation
- [ ] Zwei User öffnen dasselbe Item: nur ein globaler Preload innerhalb des Cooldowns.
- [ ] Detailseite einer ungesehenen Serie: S01E01 bzw. nächste verfügbare Episode wird geladen.
- [ ] Detailseite einer Episode / eines Films: genau dieses Item wird geladen.
- [ ] Session-Start mit Episode unter 90 %: dieselbe Episode wird geladen.
- [ ] Session-Start mit Episode ab 90 %: nächste verfügbare Episode wird geladen.
- [ ] Session-Start mit Film unter 90 %: derselbe Film wird geladen.
- [ ] Session-Start mit Film ab 90 %: kein Preload.
- [ ] Fehlende S01E01-Datei: nächste verfügbare Episode wird gewählt.
- [ ] Parallele Trigger erzeugen keine doppelten Reads.
- [ ] Fehlgeschlagener Read setzt keinen Cooldown.
- [ ] Playback und HTTP-Detailseiten-Aufrufe werden nicht durch Prewarm-Arbeit blockiert.

---

## 🚫 Bewusst nicht Teil dieses Umfangs

- Kein Prewarm kompletter Serien, Staffeln oder Bibliotheken.
- Keine getrennten Caches oder Cooldowns pro User.
- Kein zusätzlicher ffprobe-Aufruf und keine Transcodierung.
- Keine Änderung am eigentlichen Playback- oder Streaming-Pfad.
- Keine unnötige Vorab-Abfrage von Postern/Bildern als Ersatz für einen Item-Detailseiten-Trigger.

---

## 📝 Entscheidungslog

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
| Detailseite | Das konkrete Media Item laden; Container auf einen konkreten Kandidaten auflösen |
| Cache-Isolation | Keine User-Isolation; ein Item-Cache für alle |

*Diese Roadmap dokumentiert das gewünschte Verhalten und den geplanten Implementierungsumfang. Geplante Punkte sind noch keine Aussage darüber, dass die entsprechenden Jellyfin-Hooks bereits technisch verifiziert oder implementiert sind.*
