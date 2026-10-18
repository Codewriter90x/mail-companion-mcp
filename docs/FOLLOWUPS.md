# Questioni da seguire — registro locale SQLite

Implementato in C# / .NET 10 con Microsoft.Data.Sqlite: nessun database server aggiuntivo. Il registro funziona senza internet e senza credenziali IMAP. I client Codex e Open WebUI devono essere riavviati/aggiornare la discovery dei tool dopo l'aggiornamento.

## Cosa viene seguito

Una questione può coinvolgere più parti e non avere un responsabile certo. Esempio inventato: una porta di rete non funziona, due fornitori devono capire chi interviene e chi coordina vuole verificarne l'esito.

- Titolo e risultato atteso.
- Partecipanti multipli, “in attesa di” facoltativo (vuoto = da chiarire).
- Stato `open`, `waiting`, `needs_review` oppure `closed`.
- Data del prossimo controllo; non è necessariamente una scadenza promessa da un fornitore.
- Riferimenti email (account/cartella/UID/UIDVALIDITY e Message-ID se disponibile).
- Note sintetiche e storico con date, valori prima/dopo e motivo di chiusura.

Non conservare corpi completi, allegati, password o token nelle note. Le identità email sono riferimenti, non una copia offline della casella. I riferimenti UID diventano obsoleti dopo spostamento o cambiamento UIDVALIDITY; il Message-ID aiuta a ricercare, ma questa versione non ricolloca automaticamente messaggi.

## Come chiedere al client

> Tieni aperta la questione della porta di rete: aspettiamo una conferma di ripristino e una prova di comunicazione. Parti coinvolte A, B, C e D; non sappiamo chi farà il prossimo passo. Ricontrolliamola il 23 ottobre 2026 alle 10, ora italiana.

Il client deve risolvere la data con offset corretto e chiarire ambiguità prima di salvarla. Per esempio il 23 ottobre 2026 alle 10 in Europe/Rome è `2026-10-23T10:00:00+02:00`. In caso di cambio ora, chiarire quale occorrenza si intende.

> Cosa devo ricontrollare? Mostrami le questioni scadute.

> Quali controlli sono previsti nei prossimi sette giorni?

> Aggiungi una nota: il fornitore sta ancora facendo verifiche. Non chiudere la questione e conserva la data del controllo.

> Rinvia il controllo al 27 ottobre alle 15.

> Chiudi la questione: collegamento ripristinato e test di comunicazione verificato.

Nessuna questione reale viene creata dal programma senza una richiesta. I test usano dati inventati e cartelle temporanee.

## Tool MCP

| Tool | Uso |
|---|---|
| `create_followup` | Titolo, risultato atteso, prossimo controllo, partecipanti, attesa, riferimenti e nota |
| `list_followups` | Elenco attive/chiuse e controlli scaduti/prossimi, ordinati per data |
| `get_followup` | Dettaglio, revisione corrente e storico paginato |
| `update_followup` | Nota e aggiornamenti, senza modificare la scadenza automaticamente |
| `snooze_followup` | Rinvia a una data futura, mantenendo vecchia e nuova data nello storico |
| `close_followup` | Chiusura esplicita con motivo, conservando tutti i dati |

Filtri elenco: `status=active|all|open|waiting|needs_review|closed`, `due=all|overdue|upcoming`, `days=1..365`, `limit=1..50`, `offset=0..10000`. `upcoming` esclude i già scaduti; `overdue` include i controlli previsti esattamente all'istante corrente. Le chiuse non compaiono nei controlli da fare.

Scritture: `request_id` unico per operazione (per esempio UUID), da riutilizzare identico nei retry. Stesso ID/parametri restituisce l'esito originale senza duplicati; stesso ID con parametri diversi viene rifiutato. Non deduplica automaticamente due richieste diverse sullo stesso argomento: cercare le questioni esistenti prima di crearne una.

Aggiornamenti: passare `expected_revision` dalla risposta più recente. Una revisione superata viene rifiutata, senza sovrascrivere modifiche di un altro client. Le liste specificate in `update_followup` sostituiscono quelle precedenti; ometterle per conservarle. `waiting_for=''` svuota l'assegnazione. Il solo campo `status=waiting` non impone un responsabile: un'attesa può essere verso più parti o ancora da chiarire.

Una questione chiusa è consultabile ma non modificabile in questa prima versione. Non esistono eliminazione, riapertura o ricorrenze. La chiusura richiede `close_followup`; il generico update non può aggirare il motivo obbligatorio.

## Indipendenza dalle email

- Leggere o segnare un'email come letta non chiude/rinvia la questione.
- Creare, rinviare o chiudere una questione non chiama IMAP.
- Una risposta interlocutoria non chiude la questione né sposta il controllo.
- Questa versione non cerca automaticamente nuove risposte: si può chiedere al client di cercarle, leggerle e proporre un aggiornamento, da approvare.
- Date e fatti non vengono dedotti dal solo stato letto/non letto o dall'essere in copia.

## Dove sono i dati

Esecuzione locale C#: `data/followups.sqlite3` accanto ai sorgenti. Creazione alla prima scrittura; le letture di un registro assente restituiscono un elenco vuoto senza creare file.

Override: `ARUBA_MAIL_DB_PATH` per un file SQLite su disco locale. La cartella deve essere scrivibile e il file avere permessi 600. Per cartelle nuove il bridge usa permessi 700. SQLite usa transazioni atomiche; storico, mutazione e chiave di idempotenza vengono salvati nella stessa transazione. Una versione database futura/sconosciuta viene rifiutata senza modifiche.

Il database e i file di journal sono esclusi da Git; l'allowlist Docker esclude tutti i dati. SQLite non cifra automaticamente il file: permessi privati non sostituiscono cifratura disco e backup protetti. Usare un disco locale, non una share SMB/NFS per condividere lo stesso file tra computer.

### Docker stdio e HTTP standalone

Gli script `run-container.sh` e `run-http-container.sh` creano la cartella `data/` sul computer e la montano in `/data`, unica area persistente scrivibile oltre a `/tmp`. `ARUBA_MAIL_DATA_DIR` consente di scegliere un'altra cartella locale. Usando la stessa cartella, entrambi i trasporti consultano lo stesso registro, anche con processi separati.

Il file resta sul computer quando il container viene rimosso. Non usare un database dentro il filesystem effimero del container.

### Docker Compose/Open WebUI

Prima di avviare il compose:

```sh
(umask 077; mkdir -p data)
export LOCAL_UID="$(id -u)" LOCAL_GID="$(id -g)"
docker compose -f compose.openwebui.yml up -d --build
```

Con immagine già scaricata e taggata `mail-companion-mcp:local`, usare `--no-build` al posto di `--build`. Il compose monta `./data:/data`; la directory deve appartenere all'utente UID/GID specificato. Non ricreare la directory se contiene già il registro e non cancellare i volumi durante gli aggiornamenti.

### Backup

Salvare il registro fuori dal repository in un backup privato. A processo fermo, copiare la directory dati; a processo attivo usare l'API SQLite di backup o il comando `.backup` del client SQLite, non copiare soltanto il file principale durante una transazione. Verificare il ripristino su una copia separata prima di sostituire l'originale. Questa versione non programma backup automatici.

## Avvisi: servizio Apple opzionale

È un registro di scadenze **consultabile su richiesta**. Il registro da solo non avvia timer in background, non invia Telegram, non contatta i partecipanti e non sollecita nessuno. È disponibile il [collegamento opzionale a Promemoria Apple](APPLE_REMINDERS.md): un servizio separato sul Mac crea e aggiorna gli avvisi nella lista scelta.
Il client può dire “controllo salvato per quella data”, ma non “ti avviserò”. Le date passate restano visibili tra gli scaduti alla prossima consultazione, anche dopo computer spento/riavviato. Notifiche locali indipendenti dalla chat saranno un incremento separato.

## Test

```sh
dotnet restore tests/ArubaMail.Tests --locked-mode
dotnet test tests/ArubaMail.Tests --no-restore
```

Test su database temporanei: persistenza in un nuovo processo, più parti senza responsabile, retry/concorrenza, revisione superata, storico e paginazione, confini temporali e cambio ora, rinvio, motivo di chiusura, transazioni/permessi/versione DB, riferimenti invalidi, assenza di accessi IMAP e indipendenza da `mark_as_read`. Prove MCP stdio con riavvio server e HTTP autenticato. Nessuna password o casella reale necessaria.
