# MAGI · Backend

API ASP.NET Core del **sistema MAGI**: tre intelligenze artificiali di provider diversi votano su una proposizione, come i tre supercomputer di *Neon Genesis Evangelion*.

| Unità | Sfaccettatura | Provider | Modello di default |
|---|---|---|---|
| **MELCHIOR-1** | Scienziata: evidenze, logica, fattibilità | Anthropic | `claude-opus-5-5` |
| **BALTHASAR-2** | Madre: protezione, etica, conseguenze sulle persone | OpenAI | `gpt-6.1-sol` |
| **CASPER-3** | Donna: desiderio, intuito, autenticità | Google | `gemini-3.1-pro-preview` |

### Come funziona una deliberazione

1. **Round 1, voto indipendente.** Le tre unità ricevono la proposizione in parallelo. Ognuna risponde con un JSON vincolato da schema: `verdict` (APPROVE/REJECT), `confidence`, `summary`, `reasoning` e `conditions`.
2. **Round 2, dibattito** (facoltativo). Parte solo se il voto non è unanime. Ogni unità legge le posizioni delle altre due e può confermare o cambiare voto. Il prompt scoraggia di proposito il conformismo.
3. **Risoluzione.** Conta l'ultimo voto valido di ogni unità:
   - `MAJORITY`: servono 2 voti su 3.
   - `UNANIMOUS`: servono 3 voti su 3, e un solo REJECT fa da veto.
   - Un'unità in errore non vota e non può far pendere l'esito. Se non si raggiunge la soglia, il risultato è `INCONCLUSIVE`.

Gli eventi arrivano in tempo reale con Server-Sent Events. Se ci si collega in ritardo si riceve prima tutta la storia, poi gli eventi live.

### Struttura

```
src/
├── Program.cs                 composizione dei servizi e pipeline HTTP
├── Configuration/             lettura e validazione delle variabili d'ambiente
├── Models/ Data/              entità EF Core, DbContext, migration MySQL
├── Cores/                     i tre adapter LLM + prompt e schema di voto comuni
├── Deliberations/             motore dei round, regola di risoluzione, coda, hub SSE
├── Auth/                      JWT, hashing BCrypt, seed dell'account operatore
├── Endpoints/                 Minimal API + rate limiting
└── Contracts/                 DTO esposti al frontend
```

## Prerequisiti

- .NET SDK 8.0
- MySQL 8.0 (oppure Docker)
- Strumento EF Core, solo per creare nuove migration: `dotnet tool install -g dotnet-ef`
- Almeno una chiave API tra Anthropic, OpenAI e Google AI Studio (le unità senza chiave risultano OFFLINE)

## Installazione

```bash
git clone git@github.com:v-heissu/magi-backend.git
cd magi-backend
cp .env.example .env      # poi compila i valori
dotnet restore
```

Le migration vengono applicate in automatico all'avvio.

## Variabili d'ambiente

| Variabile | Obbligatoria | Descrizione |
|---|---|---|
| `PORT` | no (8080) | Porta HTTP su cui ascolta l'API |
| `ASPNETCORE_ENVIRONMENT` | no | `Development` / `Production` |
| `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_USER`, `DB_PASSWORD` | sì | Connessione MySQL |
| `JWT_SECRET` | sì | Chiave HMAC dei token, almeno 32 caratteri (`openssl rand -base64 48`) |
| `JWT_EXPIRATION` | no (43200) | Durata del token in secondi |
| `ADMIN_EMAIL`, `ADMIN_PASSWORD` | sì al primo avvio | Account operatore. Non c'è registrazione pubblica: ogni deliberazione consuma credito API. Se cambi la password e riavvii, la password viene aggiornata. Minimo 12 caratteri |
| `CORS_ORIGINS` | no | Origini CORS separate da virgola. Di norma vuota: il browser non chiama mai il backend direttamente |
| `ANTHROPIC_API_KEY`, `ANTHROPIC_MODEL`, `ANTHROPIC_EFFORT` | no | MELCHIOR-1. Effort: `low` / `medium` / `high` / `max` |
| `OPENAI_API_KEY`, `OPENAI_MODEL`, `OPENAI_REASONING_EFFORT` | no | BALTHASAR-2. Effort: `low` / `medium` / `high` |
| `GEMINI_API_KEY`, `GEMINI_MODEL`, `GEMINI_THINKING_LEVEL` | no | CASPER-3. Thinking level: `low` / `high` |
| `DELIBERATIONS_PER_HOUR` | no (30) | Limite di deliberazioni per utente all'ora |
| `MAX_CONCURRENT_DELIBERATIONS` | no (3) | Deliberazioni eseguite in parallelo |
| `CORE_TIMEOUT_SECONDS` | no (240) | Attesa massima per la risposta di una singola unità |
| `ANTHROPIC_BASE_URL`, `OPENAI_BASE_URL`, `GEMINI_BASE_URL` | no | Endpoint alternativi (proxy, mock nei test) |

> Le variabili sono "piatte" (`DB_HOST`, non `Db__Host`) e vengono lette all'avvio. Se manca una variabile obbligatoria il processo si ferma subito con un messaggio esplicito.

## Avvio in sviluppo

```bash
# MySQL locale usa-e-getta
docker run -d --name magi-mysql -e MYSQL_ROOT_PASSWORD=dev -e MYSQL_DATABASE=magi \
  -e MYSQL_USER=magi -e MYSQL_PASSWORD=dev -p 127.0.0.1:3306:3306 mysql:8.0

# carica .env nella shell e avvia
set -a && source .env && set +a
dotnet run
```

Endpoint principali (tutti sotto JWT Bearer, tranne login e health):

| Metodo | Percorso | |
|---|---|---|
| `GET` | `/health` | Stato del servizio |
| `POST` | `/api/auth/login` | `{ email, password }` → `{ token, expiresAt, email }` |
| `GET` | `/api/auth/me` | Utente corrente |
| `GET` | `/api/system/cores` | Le tre unità, con provider, modello e stato online |
| `POST` | `/api/deliberations` | `{ proposition, context?, mode: majority\|unanimous, rounds: 1\|2 }` → 202 `{ id }` |
| `GET` | `/api/deliberations?page=&pageSize=` | Storico paginato |
| `GET` | `/api/deliberations/{id}` | Dettaglio con tutti i voti |
| `GET` | `/api/deliberations/{id}/events` | Stream SSE: `snapshot`, `round_started`, `core_started`, `core_voted`, `round_completed`, `resolved`, `failed`, `end` |
| `DELETE` | `/api/deliberations/{id}` | Elimina una deliberazione conclusa |

Per aggiungere una migration:

```bash
dotnet ef migrations add NomeMigration --output-dir src/Data/Migrations
```

## Build e deploy

```bash
dotnet publish -c Release -o ./publish
PORT=8080 dotnet ./publish/Magi.Api.dll
```

In produzione l'API non va esposta su Internet. La chiama solo il server SvelteKit (`magi-frontend`) sulla rete interna. HTTPS lo termina il reverse proxy davanti al frontend.

## Deploy con Docker

```bash
docker build -t magi-backend .
docker run --env-file .env -p 127.0.0.1:8080:8080 magi-backend
```

Lo stack completo (frontend + backend + MySQL) si avvia dal `docker-compose.yml` nella cartella che contiene entrambi i repository:

```
magi/
├── magi-frontend/
├── magi-backend/
├── docker-compose.yml
└── .env                 # credenziali DB e porta pubblicata (vedi .env.example)
```

```bash
docker compose up -d --build
docker compose logs -f backend
```

Dopo una modifica al codice ricrea il container: `docker compose up -d --build backend`. Un semplice `restart` continuerebbe a usare la vecchia immagine.
