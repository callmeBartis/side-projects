# side-projects

[![Build & Publish](https://github.com/callmeBartis/side-projects/actions/workflows/build.yml/badge.svg)](https://github.com/callmeBartis/side-projects/actions/workflows/build.yml)

Eine Sammlung kleiner, eigenständiger Side-Projects. Jedes Projekt liegt in
seinem eigenen Ordner, bringt ein eigenes `Dockerfile` mit und wird von der CI
automatisch gebaut und als Docker-Image veröffentlicht.

> **Komplett kostenlos & kommerziell nutzbar.** Alles in diesem Repository darf
> ohne Einschränkung verwendet werden — privat, kommerziell, verändert oder
> weiterverteilt. Keine Lizenzgebühren, keine Namensnennungspflicht. Nutzung auf
> eigenes Risiko, ohne Gewährleistung.

## Inhaltsverzeichnis

- [GeoBingo](#geobingo) — Echtzeit-Multiplayer „Finde es in Street View"
- [Allgemeines](#allgemeines)
  - [Wie die CI funktioniert](#wie-die-ci-funktioniert)
  - [Neues Projekt hinzufügen](#neues-projekt-hinzufügen)

---

## GeoBingo

📁 [`geobingo/`](./geobingo) · 🐳 `ghcr.io/callmebartis/geobingo`

Ein Echtzeit-Multiplayer-Spiel: Der Host schreibt eine Liste von Begriffen
(z. B. *„rote Tür, Graffiti, ein Hund, ein Brunnen"*), teilt einen Raum-Link,
und alle suchen die Begriffe irgendwo auf Google Street View. Am Ende stimmen
die Spieler über die Funde ab und es gibt eine Rangliste.

**Stack:** Go-Backend (API + Echtzeit über SSE, SQLite) das ein statisch
exportiertes Next.js-Frontend einbettet → **ein einziges, eigenständiges Binary
bzw. Docker-Image**. Einzige externe Abhängigkeit zur Laufzeit: ein Google-Maps-
API-Key.

### Voraussetzungen: Google-Maps-API-Key

GeoBingo nutzt Google Street View und braucht daher einen API-Key. So bekommst du
einen (kostenloses Google-Kontingent ist für privates Spielen i. d. R. mehr als
ausreichend):

1. [Google Cloud Console](https://console.cloud.google.com/) öffnen und ein
   Projekt anlegen (oder ein bestehendes wählen).
2. **Abrechnung aktivieren** (Billing) — Google verlangt eine Zahlungsmethode,
   auch wenn du im Free-Tier bleibst.
3. Unter **APIs & Dienste → Bibliothek** diese beiden APIs aktivieren:
   - **Maps JavaScript API**
   - **Geocoding API**
4. Unter **APIs & Dienste → Anmeldedaten → Anmeldedaten erstellen → API-Schlüssel**
   einen Key erzeugen.
5. **Key absichern** (wichtig, da der Key im Browser sichtbar ist):
   - *Anwendungseinschränkungen* → **HTTP-Referrer** → deine Domain(s)
     eintragen (z. B. `http://localhost:10200/*` und/oder `https://deine-domain.tld/*`).
   - *API-Einschränkungen* → auf die beiden oben genannten APIs begrenzen.
6. Den Key als Umgebungsvariable `MAPS_API_KEY` setzen (siehe unten).

### Setup mit `docker run`

```bash
docker run -d --name geobingo \
  -p 10200:10200 \
  -e MAPS_API_KEY=dein_google_maps_key \
  -v geobingo-data:/data \
  ghcr.io/callmebartis/geobingo:latest
```

Danach im Browser **http://localhost:10200** öffnen.

- `-v geobingo-data:/data` macht die SQLite-DB (Räume) über Neustarts hinweg
  persistent. Ohne Volume ist alles flüchtig — für ein Spiel zwischendurch völlig okay.
- `-p 10200:10200` mappt den Port; der Container-Port lässt sich über
  `-e PORT=...` ändern.

### Setup mit Docker Compose

`docker-compose.yml`:

```yaml
services:
  geobingo:
    image: ghcr.io/callmebartis/geobingo:latest
    # Alternativ lokal aus dem Quellcode bauen statt das Image zu ziehen:
    # build: ./geobingo
    ports:
      - "10200:10200"
    environment:
      MAPS_API_KEY: ${MAPS_API_KEY}   # aus deiner .env oder Shell
      PORT: "10200"
    volumes:
      - geobingo-data:/data
    restart: unless-stopped

volumes:
  geobingo-data:
```

Starten:

```bash
export MAPS_API_KEY=dein_google_maps_key   # Windows PowerShell: $env:MAPS_API_KEY="..."
docker compose up -d
```

### Selbst bauen (ohne Registry)

```bash
docker build -t geobingo ./geobingo
docker run --rm -p 10200:10200 -e MAPS_API_KEY=dein_key geobingo
```

Mehr Details (lokale Entwicklung, Konfiguration, Spielablauf) stehen im
[GeoBingo-README](./geobingo/README.md).

---

## Allgemeines

### Wie die CI funktioniert

Der Workflow [`.github/workflows/build.yml`](./.github/workflows/build.yml) ist
für alle Projekte gemeinsam:

1. Bei jedem Push/PR wird **erkannt, welche Top-Level-Ordner geändert wurden**
   und ein `Dockerfile` besitzen.
2. Für jedes geänderte Projekt wird das **Docker-Image gebaut**.
3. Bei Pushes auf `main` wird das Image in die GitHub Container Registry
   **veröffentlicht**: `ghcr.io/callmebartis/<projekt>:latest` (und `:<commit-sha>`).
   Pull Requests bauen nur zur Prüfung — sie veröffentlichen nichts.

Die Erkennung ist vollständig dynamisch — es gibt keine pro Projekt zu pflegende
Liste.

### Neues Projekt hinzufügen

1. Neuen Top-Level-Ordner anlegen, z. B. `mein-projekt/`.
2. Ein `Dockerfile` hinzufügen, das die App baut und startet.
3. Committen und pushen. Die CI übernimmt es automatisch und veröffentlicht
   `ghcr.io/callmebartis/mein-projekt`.

Fertig — keine Workflow-Änderungen nötig.

> **Hinweis (öffentliches Repo):** Secrets gehören in `.env`-Dateien (repo-weit
> git-ignoriert) — committe niemals echte Keys, jedes Projekt liefert dafür eine
> `.env.example` als Vorlage. Datenbanken (`*.db`), Build-Artefakte und
> `node_modules/` sind ebenfalls ignoriert.
