# Configurazione da zero

[Registro locale SQLite: questioni e scadenze](FOLLOWUPS.md). Dopo un aggiornamento, ricaricare la discovery: sono esposti dodici strumenti (sei IMAP e sei follow-up). Avvisi opzionali tramite [Promemoria Apple](APPLE_REMINDERS.md), con servizio nativo separato.

Il bridge legge caselle reali e può segnarne i messaggi come letti. Non invia, sposta o elimina email. Funziona in locale senza OAuth. Le password non sono incluse nel repository né nell'immagine.

## 1. Scaricare il progetto privato

Serve un account GitHub autorizzato al repository (per un collaboratore, il proprietario deve prima concedere accesso).

```sh
gh auth login
gh repo clone Codewriter90x/aruba-mail-mcp
cd aruba-mail-mcp
```

Installare e avviare Docker, poi scegliere tra immagine pronta e build locale.

### Immagine pronta da GHCR

Package: [aruba-mail-mcp](https://github.com/users/Codewriter90x/packages/container/package/aruba-mail-mcp).
Build e diagnostica: [GitHub Actions](https://github.com/Codewriter90x/aruba-mail-mcp/actions/workflows/container.yml).

L'immagine è `ghcr.io/codewriter90x/aruba-mail-mcp:latest`, con supporto Linux `amd64` e `arm64` (Mac Apple Silicon incluso tramite Docker).
Il registry è privato: l'accesso al repository non sostituisce automaticamente l'autenticazione Docker. In GitHub creare un personal access token classic con il solo scope `read:packages` e accesso autorizzato al package. Inserirlo **localmente al prompt password**, non nel comando o in chat:

```sh
docker login ghcr.io --username IL_TUO_USERNAME_GITHUB
docker pull ghcr.io/codewriter90x/aruba-mail-mcp:latest
docker tag ghcr.io/codewriter90x/aruba-mail-mcp:latest aruba-mail-mcp:local
```

La tag locale permette di usare gli script senza altre variabili. Per riproducibilità si può scaricare il tag immutabile `sha-<commit completo>` indicato dalla build. `latest` segue main.

### Alternativa senza token del registry

```sh
./scripts/build-container.sh
```

Lo script costruisce l'immagine `aruba-mail-mcp:local` dal clone ed esegue i test offline. Non richiede password IMAP.

## 2. Inserire le credenziali localmente

**Opzione guidata:** `./scripts/setup-container.sh` (Docker) oppure `./scripts/setup.sh` (.NET nativo) apre il pannello locale protetto. Gestisce caselle, prova connessione e scelta Codex/Open WebUI con stdio o HTTP. [Istruzioni complete](SETUP.md). I passaggi manuali seguenti restano disponibili come alternativa.


```sh
cp accounts.example.json accounts.local.json
chmod 600 accounts.local.json
```

Aprire `accounts.local.json` con un editor locale:

| Campo | Valore |
|---|---|
| `id` | Nome breve univoco, per esempio `ufficio` |
| `email` | Indirizzo della casella |
| `username` | Login IMAP, normalmente indirizzo completo |
| `password` | Password della casella o password applicativa richiesta dal provider |
| `imap_host` | Per Aruba su dominio, normalmente `imaps.aruba.it` |
| `imap_port` | Normalmente `993`, TLS obbligatorio |

Aggiungere un oggetto all'array `accounts` per ogni casella. Conservare il JSON valido: virgolette e backslash nella password vanno escapati. Non incollare il file in chat o issue. Non cambiare nome al file per aggirare le esclusioni Git. Il bridge rilegge le credenziali a ogni richiesta.

[Parametri Aruba ufficiali](https://guide.aruba.it/hosting-e-domini/email/configurazione-posta/parametri-configurazione-posta). PEC e altri servizi hanno endpoint differenti. Exchange Online non funziona con il login basic di questa versione.

## 3A. Usarlo in Codex tramite stdio

Dalla cartella del clone, ricavare il percorso assoluto con `pwd`, poi:

```sh
codex mcp add aruba-mail -- /PERCORSO/ASSOLUTO/aruba-mail-mcp/scripts/run-container.sh
codex mcp get aruba-mail
```

Se esiste già una registrazione `aruba-mail`, verificarla prima di sostituirla. Aprire una nuova chat/riavviare il client se necessario.
Non avviare il server HTTP per questo uso: Codex crea il container e usa stdin/stdout. Nessuna porta viene pubblicata.

### Senza Docker: C# nativo

Installare SDK .NET 10, poi:

```sh
./scripts/build-native.sh
codex mcp add aruba-mail -- /PERCORSO/ASSOLUTO/aruba-mail-mcp/scripts/run-native.sh
```

La configurazione JSON e il registro SQLite sono gli stessi della versione Python. Nessun runtime Python richiesto. `ARUBA_MAIL_DOTNET` può indicare il percorso assoluto di `dotnet` se il client grafico non lo trova nel PATH. Durante un aggiornamento conservare credenziali e directory dati; prima salvare un backup del registro a client fermo. I nomi dei dodici strumenti restano invariati.

## 3B. Usarlo in Open WebUI tramite Streamable HTTP

Serve Open WebUI con supporto MCP Streamable HTTP e un modello capace di tool calling. Token MCP e password email sono due segreti diversi.

```sh
./scripts/create-http-token.sh
```

Il token casuale è salvato in `mcp.token`, permessi 600, senza stamparlo. Aprire il file in un editor locale e copiarne il valore nel campo Bearer di Open WebUI. Non copiarlo in chat, issue o URL. Lo script non sovrascrive un token esistente.

### Open WebUI eseguito direttamente sull'host

```sh
./scripts/run-http-container.sh
```

Il server resta in primo piano (Ctrl+C lo ferma) e pubblica solo `127.0.0.1:8000`.
Nel pannello amministrazione Open WebUI, sezione strumenti esterni/connessioni (il nome dipende dalla versione):

1. Aggiungere una connessione **MCP Streamable HTTP**.
2. URL: `http://127.0.0.1:8000/mcp`.
3. Autenticazione: **Bearer**, valore del file `mcp.token`; non scegliere OAuth.
4. Verificare la connessione e la discovery dei dodici strumenti.
5. Limitare l'accesso al solo utente autorizzato alle caselle e abilitare gli strumenti nella chat.

### Open WebUI già in Docker

`localhost` dentro Open WebUI indica il container Open WebUI, non il bridge. Usare una rete privata comune. Se non esiste ancora una rete adatta:

```sh
docker network create ai-local
docker network connect ai-local NOME_CONTAINER_OPENWEBUI
(umask 077; mkdir -p data)
export LOCAL_UID="$(id -u)" LOCAL_GID="$(id -g)"
docker compose -f compose.openwebui.yml up -d --no-build
```

Il compose usa la tag locale creata al passo 1. Se si preferisce costruire dal sorgente, sostituire `--no-build` con `--build`.
Se si riusa un'altra rete, impostare `ARUBA_MAIL_DOCKER_NETWORK` con il suo nome. Non ricreare o ricollegare una rete già configurata.
Usare in Open WebUI URL **`http://aruba-mail:8000/mcp`**, con lo stesso Bearer. Nessuna porta del bridge è pubblicata sull'host in questa modalità. Il collegamento manuale di Open WebUI alla rete va reso persistente nel suo compose prima di ricrearlo; non montare il socket Docker in Open WebUI.

Stato e arresto del solo bridge:

```sh
docker compose -f compose.openwebui.yml ps
docker compose -f compose.openwebui.yml down
```

Per usare Docker tra host differenti serve una configurazione HTTPS e firewall dedicata; non pubblicare l'HTTP in chiaro sulla LAN o su internet. Lo stream non è un sito web: aprire `/mcp` nel browser non costituisce un test MCP.

## 4. Prima prova, senza modifiche

Chiedere al client:

> Usa aruba-mail: elenca le caselle configurate.

> Elenca le cartelle di ufficio e mostrami le ultime 10 email non lette, senza modificarle.

> Leggi una delle email, senza segnarla come letta.

Verificare separatamente discovery, login IMAP e lettura MIME. Solo se desiderato, autorizzare esplicitamente una prova `mark_as_read` su un messaggio scelto; quella modifica è reale. UID e UIDVALIDITY devono provenire dalle risposte del bridge.

## Problemi comuni

| Sintomo | Controllo |
|---|---|
| `unauthorized` durante pull | Docker login GHCR con `read:packages`; autorizzazione al package |
| HTTP 401 | Bearer mancante/errato; riavviare HTTP dopo rotazione token |
| HTTP 421 | Host non consentito; verificare URL/rete, non disattivare indiscriminatamente i controlli |
| Configurazione assente/incompleta | Compilare JSON, ID corretto, mount presente, permessi 600 |
| Permission denied sul file | UID/GID container uguale al proprietario host; `LOCAL_UID`/`LOCAL_GID` nel compose |
| Connessione rifiutata in Open WebUI | URL relativo al posto in cui gira Open WebUI; container e rete corretti |
| MIME o dimensione non supportati | Il bridge restituisce un errore senza scaricare tutto come ripiego |

Gli script sono per shell POSIX su macOS/Linux. Windows richiede adattare percorsi/mount e gestione permessi, oppure usare WSL2: non è stato collaudato.
