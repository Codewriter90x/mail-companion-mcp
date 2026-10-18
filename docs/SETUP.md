# Configurazione guidata locale

Il pannello permette di aggiungere e modificare caselle IMAP, verificare l'accesso e preparare il collegamento con Codex, Open WebUI o un altro client MCP. Non richiede scrivere JSON a mano. Nessun OAuth viene implementato e non aggiunge supporto nativo a Exchange.

## Avvio

### Senza Docker

Serve .NET 10. Dalla cartella del clone:

```sh
./scripts/build-native.sh
./scripts/setup.sh
```

Il secondo comando apre il browser autenticato su `http://127.0.0.1:8765/`. Alla prima installazione può compilare il programma se manca; dopo un aggiornamento eseguire nuovamente `build-native.sh`. Ctrl+C nel terminale chiude il pannello. `./scripts/setup.sh --port 8766` sceglie una porta differente.

### Con Docker

Dopo avere costruito l'immagine (`./scripts/build-container.sh`) oppure scaricato/taggato quella privata come descritto in [CONFIGURATION.md](CONFIGURATION.md):

```sh
./scripts/setup-container.sh
```

Il comando apre il browser e pubblica soltanto `127.0.0.1:8765`. Genera automaticamente i file in `config/`, con permessi privati. Al termine tornare al terminale e premere Invio: il container di configurazione viene fermato e rimosso; i file rimangono sul computer. `ARUBA_MAIL_CONFIG_DIR` e `ARUBA_MAIL_SETUP_PORT` permettono di cambiare directory e porta.

Il pannello ha accesso in scrittura solo alla directory di configurazione montata. I container MCP continuano a montare credenziali e token in sola lettura. Il pannello non monta il socket Docker e non avvia/ferma altri servizi.

Se si usa il registry, è possibile specificare direttamente l'immagine:

```sh
ARUBA_MAIL_IMAGE=ghcr.io/codewriter90x/aruba-mail-mcp:latest ./scripts/setup-container.sh
```

I comandi generati conservano il nome dell’immagine selezionata. In alternativa taggare l’immagine come `aruba-mail-mcp:local`. Finché il package è privato il download richiede un account autorizzato.

## 1. Caselle

- **Nome:** ID univoco per riconoscere la casella nelle conversazioni; rimane fisso quando si modifica una casella esistente.
- **Email, nome utente e password:** la password non viene mostrata dopo il salvataggio. Lasciare vuoto per conservare quella corrente; gli spazi eventualmente presenti nella password vengono preservati.
- **Server IMAP e porta:** endpoint del provider, normalmente porta 993. TLS con verifica del certificato è sempre obbligatorio.
- **Prova connessione:** verifica TLS e login, senza leggere cartelle, corpi o flag. Non salva automaticamente i parametri; la prova richiede una connessione di rete.
- **Salva casella:** scrive la configurazione locale con sostituzione atomica e permessi 600. La versione precedente resta in `accounts.previous.secret` nella stessa directory, anch'essa privata.
- **Rimuovi:** richiede conferma nel pannello; rimuove soltanto la configurazione, non le email e non il registro delle questioni.

La lettura del pannello non crea un database della posta. Il registro SQLite delle questioni viene creato dal bridge alla prima richiesta di salvataggio di una questione.

In caso di conflitto fra due finestre, il salvataggio viene rifiutato anziché sovrascrivere l'altra modifica. Usare **Ricarica** e reinserire le modifiche desiderate.

## 2. Collegamento

Scegliere **Codex**, **Open WebUI** o **Altro client MCP**, poi il trasporto.

- **stdio:** il client avvia un processo locale e comunica su stdin/stdout. Nessuna porta MCP o Bearer. Codex propone questa modalità. Il pannello genera il comando di registrazione con percorso assoluto: se avviato in Docker propone il container, senza richiedere .NET sul computer; se avviato nativamente propone l’eseguibile .NET e include il percorso del runtime. È disponibile anche l’alternativa con container.
- **Streamable HTTP:** il bridge espone `/mcp` con Bearer obbligatorio. Scegliere la porta locale; salvando il profilo viene creato un token MCP casuale se manca, senza ruotare quello già esistente. **Copia token MCP** lo copia negli appunti per inserirlo nel client; non incollarlo nelle chat.
- **Open WebUI:** viene scelto HTTP e stdio non è selezionabile. Indicare se Open WebUI gira sul computer oppure in Docker: nel secondo caso l'indirizzo è `http://aruba-mail:8000/mcp` sulla rete privata comune, senza porte pubblicate. La porta interna Docker rimane 8000.

Il pannello genera i passi di configurazione e il comando di avvio adeguato. Dopo una modifica delle credenziali ricreare i container MCP che montano il file in sola lettura: un mount di file potrebbe ancora riferirsi alla versione precedente sostituita atomicamente. Il comando Compose generato include `--force-recreate` per il solo bridge. Non cambia automaticamente la registrazione di un client e non riavvia i processi già attivi.

In Open WebUI: **Settings → Admin → Integrations → External Tool Servers → Add Connection**, tipo **MCP (Streamable HTTP)**, URL indicato e autenticazione **Bearer** con token nel campo **Key**. Serve un amministratore e un modello capace di chiamare gli strumenti. [Documentazione ufficiale Open WebUI](https://docs.openwebui.com/features/extensibility/mcp/).

Per Codex HTTP il token deve essere disponibile nell'ambiente del client come `ARUBA_MAIL_MCP_TOKEN`; il comando generato salva soltanto il nome della variabile, mai il segreto. Per evitare gestione di variabili e processi HTTP, usare stdio. [Documentazione Codex](https://developers.openai.com/codex/mcp).

## Impostazioni salvate e compatibilità

`connection.local.json` conserva client, trasporto, porta e posizione Open WebUI. Non contiene credenziali. L'avvio normale rimane stdio per non cambiare le registrazioni esistenti. Per avviare nativamente il trasporto salvato:

```sh
./scripts/run-native.sh --configured
```

Le opzioni esplicite `--transport`, `--host` e `--port` hanno precedenza sul profilo. Il bind nativo HTTP predefinito resta `127.0.0.1`; il pannello non abilita accesso remoto.

Il pannello nativo usa la stessa configurazione del bridge, rispettando `ARUBA_MAIL_CONFIG`. Gli script e il bridge riconoscono `config/accounts.local.json` creato dal wizard Docker; se non esiste continuano a usare il precedente `accounts.local.json` nella radice. Con directory personalizzate usare i comandi generati, che includono il percorso corretto. `ARUBA_MAIL_PROFILE_PATH` e `ARUBA_MAIL_MCP_TOKEN_FILE` possono scegliere altri percorsi.

Non creare due configurazioni per le stesse caselle senza decidere quale usare: la cartella `config/`, se presente, ha precedenza sul file nella radice. Non viene effettuata una migrazione silenziosa delle credenziali esistenti. Il database `data/followups.sqlite3` resta indipendente e invariato.

## Accesso e protezioni

- Il pannello è una modalità separata `--setup`, con token amministrativo `setup.token` diverso dal token MCP. Non espone alcuno strumento MCP di configurazione o gestione password.
- Gli script aprono un collegamento con il codice nel frammento del browser, che non viene trasmesso nell'URL HTTP. La pagina lo rimuove subito dalla cronologia e lo tiene solo in memoria: niente localStorage o cookie. Dopo un ricaricamento completo della pagina, riaprire dal comando o usare il codice locale; il pulsante **Ricarica** aggiorna i dati senza perdere la sessione.
- Il codice non viene stampato nei log. Se il browser non si apre, aprire l'indirizzo locale e copiare il codice dal file generato `setup.token` in un editor locale. Non occorre creare o modificare quel file.
- **Blocca pannello** cancella la sessione in memoria. Non revoca il codice generato: per revocarlo chiudere il pannello, rimuovere il solo `setup.token` e riavviare. Non rimuovere il distinto `mcp.token` per questa operazione.
- API protette da Bearer, confronto costante, verifica Host e Origin, rifiuto richieste cross-site e form HTML, CSP senza risorse esterne, `no-store`, limiti di richieste e test IMAP concorrenti. Dimensione HTTP massima invariata a 64 KiB.
- I file di configurazione, backup e token restano fuori da Git e dalle immagini. Permessi 600 e directory nuove 700; SQLite e i file credenziali non sono cifrati automaticamente.
- HTTP è per il computer locale o la rete Docker privata sullo stesso host. Non esporre il pannello su LAN o internet. Nel container il bind interno `0.0.0.0` è ammesso solo nella modalità esplicita usata dallo script, che pubblica sulla sola loopback.

Il nome del repository, il package Docker e l'identificativo MCP `aruba-mail` restano invariati in questo incremento. Il pannello usa il nome descrittivo Mail Companion, senza interrompere i collegamenti esistenti.

## Promemoria Apple

Il pannello nativo su macOS include la terza sezione **Promemoria**. La [guida dedicata](APPLE_REMINDERS.md) spiega consenso, scelta lista, avviso inventato e avvio indipendente dalla chat. Il pannello Docker mostra il requisito del componente nativo Mac.
