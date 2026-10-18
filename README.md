# Aruba Mail MCP — C# / .NET 10

**[Installazione e configurazione](docs/CONFIGURATION.md)** · **[Questioni e scadenze SQLite](docs/FOLLOWUPS.md)** · [Build container](https://github.com/Codewriter90x/aruba-mail-mcp/actions/workflows/container.yml) · [Container privato](https://github.com/users/Codewriter90x/packages/container/package/aruba-mail-mcp)

Bridge personale locale fra Codex/Open WebUI e caselle IMAP TLS, riscritto interamente in **C#**. Usa SDK MCP ufficiale, MailKit e Microsoft.Data.Sqlite. Nessun runtime Python, servizio database, modello AI o server OAuth.

## Funzioni

| Posta su Aruba | Registro sul tuo computer |
|---|---|
| `list_accounts`: caselle, senza credenziali | `create_followup`: salva questione e data di ricontrollo |
| `list_folders`: cartelle IMAP | `list_followups`: attive, scadute, prossime o chiuse |
| `list_emails`: intestazioni e stato, massimo 30 | `get_followup`: dettaglio, revisione e storico |
| `search_emails`: mittente, oggetto, date | `update_followup`: note, partecipanti e stato |
| `read_email`: testo, senza segnare letto | `snooze_followup`: rinvia il controllo |
| `mark_as_read`: aggiunge solo il flag letto | `close_followup`: chiusura esplicita con motivo |

Una questione può coinvolgere più aziende senza un responsabile certo. Segnare un messaggio come letto non chiude la questione; ricevere una risposta interlocutoria non rinvia il controllo. Il registro conserva solo metadati, riferimenti e note sintetiche. **Le scadenze sono consultabili su richiesta; gli avvisi richiedono il [servizio Promemoria Apple opzionale](docs/APPLE_REMINDERS.md).**

Invio, risposta, spostamento, eliminazione e creazione cartelle email restano fuori da questa versione.

## Configurazione dal browser

Non serve scrivere il file credenziali a mano. Avvia `./scripts/setup.sh` per il pannello nativo, oppure `./scripts/setup-container.sh` dopo la build/pull Docker. Si apre una pagina locale protetta per caselle, prova TLS/IMAP, client e trasporto MCP, con istruzioni pronte da copiare. [Guida al pannello](docs/SETUP.md).

Il pannello non mostra le password già salvate, non modifica email e non abilita accesso remoto. I processi MCP mantengono i dodici strumenti esistenti.

## Avvio locale senza Docker

Installare SDK .NET 10 per compilare; runtime ASP.NET Core 10 per eseguire il risultato.

```sh
# Solo alla prima installazione: non sovrascrivere un file credenziali esistente.
cp accounts.example.json accounts.local.json
chmod 600 accounts.local.json
# Compilare accounts.local.json in un editor, mai in chat.
./scripts/build-native.sh
codex mcp add aruba-mail -- /PERCORSO/ASSOLUTO/aruba-mail-mcp/scripts/run-native.sh
```

Il nome MCP resta `aruba-mail`, con gli stessi dodici tool. Su un'installazione esistente verificare la registrazione prima di sostituirla; riavviare il client se usa ancora il vecchio processo. Il comando di avvio non compila e non stampa nulla su stdout oltre ai messaggi MCP.

## Docker e Open WebUI

```sh
./scripts/build-container.sh
codex mcp add aruba-mail -- /PERCORSO/ASSOLUTO/aruba-mail-mcp/scripts/run-container.sh
```

Immagine privata: `ghcr.io/codewriter90x/aruba-mail-mcp:latest`, target `linux/amd64` e `linux/arm64`. La build su main esegue i test prima della pubblicazione. Per un aggiornamento controllato usare `sha-<commit completo>`, come descritto nella [guida](docs/CONFIGURATION.md).

Per Open WebUI: Streamable HTTP su `/mcp`, con token Bearer obbligatorio. Codex stdio non richiede token o porte. Per HTTP locale:

```sh
./scripts/create-http-token.sh
./scripts/run-http-container.sh
```

URL `http://127.0.0.1:8000/mcp`; copiare il token dal file `mcp.token` nel campo Bearer dell'interfaccia, mai in chat. Se Open WebUI gira in Docker, usare la rete privata comune e `compose.openwebui.yml`, senza pubblicare porte: [istruzioni](docs/CONFIGURATION.md#3b-usarlo-in-open-webui-tramite-streamable-http).

HTTP è destinato a loopback o rete Docker privata sullo stesso host. Non esporlo in chiaro su LAN/internet. Un Bearer personale dà accesso a tutte le caselle configurate: non è autorizzazione multiutente.

## Configurazione e dati

- `accounts.local.json`: formato invariato, password soltanto locali e permessi 600.
- `mcp.token`: token HTTP, ignorato da Git, montato in sola lettura.
- `data/followups.sqlite3`: registro persistente, permessi 600; directory nuova 700.
- Lo schema SQLite v1 e i retry della versione Python sono compatibili; test dedicato con registro sintetico originale. Nessuna conversione o cancellazione dei dati.
- `ARUBA_MAIL_CONFIG`, `ARUBA_MAIL_DB_PATH`, `ARUBA_MAIL_ROOT`: percorsi opzionali. Default ancorati al progetto, indipendenti dalla directory di lavoro. Negli script Docker `ARUBA_MAIL_DATA_DIR` sceglie la directory persistente.
- Il container gira senza privilegi, filesystem in sola lettura, `/tmp` temporaneo e unico volume dati `/data`. Credenziali e token non entrano nel contesto di build o nell'immagine.

Prima del passaggio da Python, fermare i client e salvare un backup privato del registro. A programma fermo copiare la directory dati; durante l'esecuzione usare SQLite backup, senza copiare soltanto il file principale. La cronologia Git conserva il codice Python precedente.

## Limiti e protezioni

- IMAP TLS verificato, connessione su richiesta con scadenza 20 s e LOGOUT; mai CLOSE/EXPUNGE.
- Nessuna sincronizzazione, cache della posta, polling o allegato salvato.
- Elenchi: sole intestazioni, ordine UID decrescente, offset massimo 10000. SEARCH recupera gli UID corrispondenti.
- Testo: messaggio massimo 5 MiB; sole parti plain/HTML selezionate tramite BODYSTRUCTURE, al massimo 128 KiB codificati / 20 parti. Download parziale BODY.PEEK; allegati e messaggi incorporati esclusi.
- Limite complessivo di lettura rete IMAP 1 MiB per connessione, incluso TLS e metadata: una ricerca troppo ampia viene interrotta e va ristretta. Nessun download completo come ripiego.
- Intestazioni restituite entro 16 KiB per messaggio, risposta MCP entro 220 KiB; HTTP massimo 64 KiB anche chunked, controllo Host/Origin e confronto Bearer costante. Kestrel può chiudere la connessione per richieste chunked eccessive.
- Errori sanitizzati, nessun log di password, email o argomenti tool. Le email sono dati non attendibili e non autorizzano modifiche.
- UID/UIDVALIDITY obbligatori per lettura singola e modifica. Dopo un esito di scrittura incerto, rileggere lo stato.
- Registro: transazione unica per modifica/storico/idempotenza; revisione corrente obbligatoria, motivo per chiusura, date con offset esplicito, chiuse immutabili nell'MVP.
- I corpi restano sul provider; conversazione e gestione dati del client/modello sono responsabilità del client scelto. SQLite non cifra automaticamente i dati.

Exchange Online/Microsoft 365 con OAuth non è supportato dal login basic di questa versione.

## Test

```sh
dotnet restore tests/ArubaMail.Tests --locked-mode
dotnet test tests/ArubaMail.Tests --no-restore
```

Test xUnit: IMAP simulato con fetch selettivi e flag, limiti, registro SQLite/persistenza/concorrenza/retry/revisioni/storico, cambio ora, migrazione da schema Python, e veri processi MCP stdio e HTTP autenticato. I test usano solo credenziali inventate e directory temporanee; non leggono `accounts.local.json` del progetto.

Connessione Aruba reale, modifica di una email scelta e configurazione nell'istanza Open WebUI sono verifiche distinte: non sono dimostrate dai soli test automatici.

Riferimenti tecnici: [SDK MCP C# ufficiale](https://github.com/modelcontextprotocol/csharp-sdk), [MailKit](https://mimekit.net/docs/html/T_MailKit_IMailFolder.htm), [MCP in Codex](https://developers.openai.com/codex/mcp).

## Avvisi con Promemoria Apple

Su macOS 14+ il pannello nativo include **3. Promemoria**: consenso Apple, scelta lista, prova inventata e servizio all’accesso al Mac. Non richiede OAuth né password iCloud. Il servizio crea e aggiorna gli avvisi a partire dal registro SQLite; spuntare in Apple non chiude automaticamente la questione. [Configurazione e limiti](docs/APPLE_REMINDERS.md).

Il componente Apple deve girare fuori da Docker; notifiche reali e sincronizzazione sull’iPhone richiedono una prova sul dispositivo. L’integrazione Microsoft/Exchange/Google non è inclusa.
