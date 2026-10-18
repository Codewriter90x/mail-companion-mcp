# Follow-up con avvisi in Promemoria Apple

Il registro delle questioni rimane nel database SQLite locale. Il componente macOS crea un promemoria per ogni questione attiva e lascia ad Apple la gestione degli avvisi. Non legge email, non invia messaggi e non richiede password iCloud né OAuth.

## Requisiti

- macOS 14 o successivo, .NET 10 e strumenti di sviluppo Apple (Xcode o Command Line Tools) per compilare il componente Swift.
- App Promemoria configurata. Per l’iPhone, scegliere una lista iCloud già sincronizzata e verificare le notifiche dell’app sul telefono.
- Il piccolo componente Apple e il servizio devono girare **sul Mac, fuori dal container Linux**. Per un bridge Docker, il servizio nativo deve puntare allo stesso file SQLite montato nel container e agli stessi parametri locali. Docker da solo non fornisce questa integrazione.

## Attivazione dal pannello

Dalla cartella del repository:

```sh
./scripts/build-native.sh
./scripts/build-apple-reminders.sh
./scripts/setup.sh
```

1. Aprire **3. Promemoria** e premere **Autorizza accesso Apple**.
2. Accettare personalmente il consenso di macOS. Apple richiede accesso completo a Promemoria: non offre un permesso limitato a una singola lista. Il componente usa la lista scelta e gli elementi che riconosce come propri.
3. Scegliere una lista dedicata, per esempio **Follow-up email**, già creata in Promemoria. Il menu mostra anche l’origine della lista: preferire iCloud per il telefono.
4. Impostare **Attivo** e salvare. Nessun promemoria viene ancora creato dal solo salvataggio.
5. Premere **Crea prova · avviso tra 3 minuti** e confermare. Viene creato un solo promemoria inventato; il registro delle questioni non viene modificato. Controllarlo sul Mac e sull’iPhone e verificare l’avviso; poi eliminarlo se desiderato.
6. Premere **Avvia anche all’accesso al Mac**. Questo installa un LaunchAgent del proprio utente e lo avvia: il servizio controlla il registro ogni 30 secondi, indipendentemente dalle conversazioni Codex/Open WebUI. Chiudere il pannello non ferma il servizio.

Per la prima prova non è necessario aprire o modificare email. L’effettiva consegna della notifica sull’iPhone deve essere verificata dall’utente: test automatici e salvataggio in EventKit non dimostrano sincronizzazione iCloud o impostazioni del telefono.

## Comportamento

- **Creazione:** copia titolo, risultato atteso, eventuale interlocutore atteso e data/ora di ricontrollo. Non copia corpi email, allegati o credenziali. Se la lista è iCloud, questi brevi dati vengono sincronizzati da Apple: il registro completo resta locale.
- **Niente duplicati ordinari:** ogni questione ha una chiave stabile nel collegamento del promemoria; l’esito è registrato in una tabella derivata nello stesso SQLite. Un retry cerca il promemoria esistente prima di crearne uno. Se trova più copie della stessa chiave, segnala errore anziché sceglierne una arbitrariamente.
- **Rinvio dal bridge:** aggiorna lo stesso promemoria e riattiva l’avviso alla nuova data. Una semplice nota nel registro non genera un altro avviso.
- **Spunta in Apple:** non chiude la questione in SQLite. Anche correggendo titolo o risultato dal bridge, la spunta rimane; un successivo rinvio esplicito può riaprire il promemoria.
- **Chiusura nel bridge:** completa il promemoria collegato e rimuove gli avvisi. Una questione già chiusa prima dell’attivazione non genera promemoria.
- **Modifiche manuali in Apple:** non vengono importate. Un aggiornamento successivo dal bridge può sovrascrivere titolo, note e data. Se si elimina manualmente un promemoria, non viene ricreato fino al successivo cambiamento rilevante della questione.
- **Scadenze già passate:** alla prima sincronizzazione viene impostato un avviso a breve; non viene ripetuto a ogni controllo. Attivare su un registro con molti arretrati può quindi generare più avvisi.
- **Mac in stop/spento:** nessun nuovo aggiornamento fino alla ripresa. Un promemoria già sincronizzato sul telefono viene gestito da Apple anche con Mac spento. Dopo il logout il servizio riparte al successivo accesso dell’utente, non prima.
- **Errore/permesso revocato:** il registro rimane intatto; il servizio riprova. Nel pannello, ricaricare la sezione per lo stato dell’ultimo controllo. `retry_pending` significa sincronizzazione non completata, non avviso consegnato.

Il servizio non esegue modelli AI e non controlla le caselle in background. La direzione della sincronizzazione è soltanto **registro → Promemoria**.

## Arresto e configurazione

**Ferma servizio e avvio automatico** rimuove il LaunchAgent. **Disattivato** sospende la sincronizzazione ma non elimina i promemoria già creati, che possono ancora notificare: gestirli in Apple quando necessario.

La configurazione privata è `apple-reminders.local.json` accanto al file delle caselle. Il componente compilato è `artifacts/apple/Mail Companion Reminders.app`. Il LaunchAgent è `~/Library/LaunchAgents/it.mailcompanion.followups.plist`. I metadati di sincronizzazione sono nella tabella `apple_reminder_sync` del registro; lo stato del servizio è nel file privato con suffisso `.apple-status.json` accanto al database. Nessuno di questi dati locali entra in Git o nel container.

Per eseguire temporaneamente il servizio in primo piano, senza installarlo all’accesso:

```sh
./scripts/run-native.sh --apple-reminders-worker
```

Per Docker, avviare il pannello **nativo** sul Mac con `ARUBA_MAIL_CONFIG` e `ARUBA_MAIL_DB_PATH` impostati sugli stessi file host usati dai volumi Docker. Il LaunchAgent conserva questi percorsi; dopo averli cambiati, reinstallarlo dal pannello. Non montare socket Docker o database tramite cartelle cloud sincronizzate.

Non spostare o eliminare il checkout mentre il servizio è installato. Dopo un aggiornamento ricompilare C# e componente Apple, quindi riavviare il servizio dal pannello. La build di sviluppo usa una firma ad hoc: macOS può richiedere nuovamente il consenso dopo modifiche al binario. Una futura distribuzione a terzi richiederà packaging e firma stabile; questa versione è per uso locale da sorgente.

## Verifica tecnica e limiti

I test C# usano un sostituto del componente Apple: verificano persistenza, assenza di duplicati nei retry, rinvio, chiusura, consenso/configurazione e mancata modifica delle questioni in caso di errore. La compilazione Swift verifica le API native. Accesso reale a EventKit, avvio automatico, iCloud e notifica sul telefono sono verifiche distinte, da completare sul Mac dell’utente.

Riferimenti ufficiali: [EventKit](https://developer.apple.com/documentation/eventkit), [accesso ai promemoria](https://developer.apple.com/documentation/eventkit/ekeventstore/requestfullaccesstoreminders(completion:)), [avvisi](https://developer.apple.com/documentation/eventkit/setting-an-alarm).
