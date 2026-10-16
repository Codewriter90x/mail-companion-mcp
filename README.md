# Aruba Mail MCP

Bridge locale tra un client MCP (per esempio Codex) e caselle IMAP con TLS.
Python 3.11+, SDK MCP ufficiale/FastMCP, trasporti **stdio** e **Streamable HTTP autenticato**. Prima versione: lettura e marcatura come letto. Nessun OAuth richiesto; HTTP opzionale solo con token Bearer.

## Funzioni

- `list_accounts`: ID e indirizzo, senza credenziali.
- `list_folders`: cartelle IMAP.
- `list_emails`: intestazioni/stato, filtro non letti, massimo 30 e offset.
- `search_emails`: ricerca sul provider per mittente, oggetto, data.
- `read_email`: testo MIME, senza scaricare allegati e senza segnare come letto.
- `mark_as_read`: aggiunge solo il flag letto su richiesta, verificando UID/UIDVALIDITY e stato risultante.

Invio, risposta, spostamento, cancellazione e creazione cartelle non sono implementati.
Non contiene modelli AI o memoria personale/reminder. Un client deve interpretare i risultati.

## Credenziali: solo sul tuo computer

```sh
cp accounts.example.json accounts.local.json
chmod 600 accounts.local.json
```

Apri `accounts.local.json` in un editor e compila `id`, `email`, `username`, `password`, `imap_host`, `imap_port`.
Non incollare password in chat, terminale o issue. I caratteri speciali devono rispettare l'escaping JSON.
Per caselle Aruba su dominio l'endpoint iniziale è `imaps.aruba.it:993`; verificare i parametri del proprio servizio (PEC e altri provider possono differire).
Per più caselle aggiungi oggetti con ID diversi all'array `accounts`.
Il file è ignorato da Git, escluso dal contesto Docker e riletto a ogni richiesta.

## Docker

Serve Docker funzionante. Lo script costruisce l'immagine ed esegue i test offline durante la build:

```sh
./scripts/build-container.sh
```

Registrazione in Codex (sostituire il percorso con quello del proprio clone):

```sh
codex mcp add aruba-mail -- /PERCORSO/ASSOLUTO/aruba-mail-mcp/scripts/run-container.sh
```

Riavvia il client/apri una nuova sessione se necessario. Il client avvia il container via stdio, senza porte pubblicate e senza TTY.
La configurazione è montata solo a runtime in lettura; il container usa filesystem in sola lettura e l'identità UID/GID dell'utente host per accedere al file con permessi 600.
Non copiare mai il file credenziali nell'immagine. Gli script non spostano né sovrascrivono le registrazioni Codex esistenti automaticamente.

Variabili opzionali: `ARUBA_MAIL_IMAGE` (default `aruba-mail-mcp:local`), `ARUBA_MAIL_CONFIG` (percorso del file locale).
`run-container.sh` usa stdin/stdout per MCP, quindi avviato a mano attende messaggi JSON-RPC: non è un'interfaccia interattiva.

L'immagine locale si può esportare con `docker save` e importare con `docker load`; distribuire separatamente configurazione vuota e istruzioni. Non è pubblicata su alcun registry.

## Python senza Docker

```sh
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
.venv/bin/python test_mail.py
codex mcp add aruba-mail -- /PERCORSO/ASSOLUTO/aruba-mail-mcp/.venv/bin/python /PERCORSO/ASSOLUTO/aruba-mail-mcp/server.py
```

## Primo utilizzo

1. «Usa aruba-mail: elenca le caselle e le cartelle della casella scelta.»
2. «Mostrami le ultime 10 email non lette senza modificarle.»
3. «Leggi questa email», riutilizzando casella, cartella, UID e UIDVALIDITY restituiti.
4. Solo se desiderato: «Segna come letta questa email e verifica lo stato.» Questa modifica è reale.

## Limiti e protezioni

- Connessione TLS verificata per richiesta, timeout socket 20 s, chiusura LOGOUT; nessun CLOSE/EXPUNGE.
- Nessuna sincronizzazione, cache, allegato salvato, polling o log del contenuto.
- Elenco con sole intestazioni (16 KiB per messaggio), ordine UID decrescente, offset massimo 10000.
- SEARCH recupera gli UID corrispondenti, non i corpi; caselle enormi restano soggette ai limiti di risposta IMAP.
- Lettura: messaggio massimo 5 MiB, parti testuali codificate complessive massimo 128 KiB/20 parti. BODYSTRUCTURE selettivo, BODY.PEEK, Base64/quoted-printable, HTML convertito in testo senza caricare risorse remote.
- Literal IMAP eccessivi rifiutati prima del download, risposta MCP massima 220 KiB.
- MIME insoliti/literal nella struttura possono produrre un errore, senza fetch completo di ripiego; metadati nomi allegati complessi possono restare grezzi.
- UIDVALIDITY verificata prima della lettura/modifica. Prima di un retry dopo esito STORE incerto, rileggere lo stato.
- Email e documenti sono dati non attendibili: non autorizzano operazioni o istruzioni al client. Nessun tool shell esposto.
- Il bridge non archivia i corpi, ma il sistema operativo può usare swap e il client/modello può conservare la conversazione: non è una garanzia di zero retention dell'intero percorso.
- Errori applicativi come oggetti `error`, senza dettagli grezzi sensibili.

## Verifiche

`test_mail.py`: 11 test con IMAP simulato, più discovery MCP stdio e errore sicuro con casella inesistente. I test non richiedono credenziali e non contattano caselle reali, anche se un file locale è presente.
La build Docker include questi controlli. Le prove reali di connessione, lettura e modifica sono checkpoint separati da eseguire solo con autorizzazione del titolare.

Il supporto Exchange Online/Microsoft 365 non è incluso: il login IMAP basic di questa versione non sostituisce OAuth richiesto da Microsoft.

## Open WebUI — Streamable HTTP

Open WebUI standard usa MCP Streamable HTTP, non stdio. Il bridge espone `/mcp` con Bearer token obbligatorio; non implementa un authorization server OAuth. Il token ha gli stessi permessi delle caselle configurate: istanza personale, non servizio multiutente.

```sh
./scripts/create-http-token.sh
./scripts/build-container.sh
./scripts/run-http-container.sh
```

`create-http-token.sh` genera un token casuale in `mcp.token` con permessi 600 e non lo stampa. Aprire il file localmente per copiarlo nel campo Bearer di Open WebUI; non inserirlo in chat o in una URL. Riavviare il processo HTTP dopo rotazione del token. Il token è ignorato da Git ed escluso dall'immagine.

Con Open WebUI sullo stesso host: pannello amministrazione → Impostazioni → Strumenti esterni/connessioni (nome dipendente dalla versione) → aggiungi server **MCP Streamable HTTP**, URL `http://127.0.0.1:8000/mcp`, autenticazione **Bearer**, valore del file `mcp.token`. Verificare/scoprire strumenti, abilitare la connessione e usare un modello con tool calling. Non scegliere OAuth. L'accesso ai tool va limitato al solo utente autorizzato a consultare queste caselle.

Se Open WebUI è in Docker, `127.0.0.1` indica il suo container. Usare una rete Docker privata comune: il file `compose.openwebui.yml` avvia solo il bridge, senza pubblicare porte. Collegare anche il container Open WebUI alla stessa rete già esistente e impostare UID/GID dell'utente che possiede i file:

```sh
export LOCAL_UID="$(id -u)" LOCAL_GID="$(id -g)"
# La rete ai-local deve già esistere e includere Open WebUI.
docker compose -f compose.openwebui.yml up -d --build
```

In questo caso URL `http://aruba-mail:8000/mcp`, stesso Bearer. `ARUBA_MAIL_DOCKER_NETWORK` permette di usare il nome della rete esistente. Il compose non avvia Open WebUI/Ollama e non invia dati a modelli: questi dipendono dal client scelto.

L'HTTP in chiaro è destinato a loopback o rete Docker privata sullo stesso host. Per accesso LAN/remoto serve HTTPS con reverse proxy e host consentiti espliciti (`ARUBA_MAIL_ALLOWED_HOSTS`); non esporre questa porta pubblicamente. Lo script standalone pubblica solo su loopback. Nessuna connessione HTTP già avviata automaticamente da questa configurazione.

Controlli HTTP: autenticazione richiesta anche per discovery, confronto token costante, dimensione richiesta massima 64 KiB, verifica Host/Origin, access log disabilitato. `test_http.py` verifica initialize e tools/list, rifiuto senza token/token errato, limite payload e host non consentito. Il test non contatta caselle reali. La configurazione nell'istanza Open WebUI dell'utente rimane un checkpoint manuale.
