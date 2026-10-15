# File Organizer

Organizza automaticamente i file su **Windows 10/11 x64**, con regole ordinate e un’interfaccia grafica per configurarle. Il motore lavora separatamente dalla GUI: puoi chiudere il configuratore e continuare a organizzare le cartelle.

## Funzionalità

- Più cartelle monitorate, ciascuna con regole indipendenti e priorità modificabile.
- Filtri per estensione, nome tramite glob (`*gatt*`, `*.pdf`) ed esclusioni.
- Destinazioni relative o assolute, anche su altri dischi.
- Riclassificazione dei file già nelle destinazioni quando cambiano le regole, inclusi quelli inseriti manualmente.
- Gestione dei duplicati, simulazione senza spostamenti e log a dimensione limitata.
- Configurazione TOML condivisa tra GUI e CLI, con validazione, salvataggio atomico e rilevamento delle modifiche esterne.
- Albero interattivo dei percorsi gestiti con selezioni dirette, ricorsive ed escluse.
- Scansione e arresto sicuro dalla GUI, più quattordici categorie rapide per creare regole.
- Cronologia persistente e annullamento verificato degli spostamenti degli ultimi sette giorni.
- Avvio con Windows per l’utente corrente, configurabile dalla GUI senza amministratore.
- Monitoraggio a eventi, coda limitata e nessuna scansione periodica durante il funzionamento normale.

## Installazione

1. Scarica l’installer Windows x64 dalla sezione [Releases](https://github.com/federyuk91/Organizer/releases), insieme al checksum `.sha256` della stessa versione. Compilando il repository, il setup viene prodotto in `artifacts/installer/`.
2. Avvia il setup con un doppio clic. Installa motore, configuratore e runtime necessari: sul PC destinatario non servono .NET, PowerShell o Visual Studio.
3. Mantieni il percorso proposto, `%LOCALAPPDATA%\Programs\FileOrganizer`, oppure scegline uno scrivibile dal tuo utente. L’installazione è per utente e non richiede privilegi amministrativi.
4. Apri **File Organizer** dal menu Start; puoi scegliere anche il collegamento sul desktop durante il setup.

L’icona trasparente della cartella viene assegnata automaticamente al programma, alle finestre, al setup e ai collegamenti creati durante l’installazione.

Il setup non crea una configurazione attiva, non avvia il motore e non registra l’avvio automatico. `Config.Example.toml` viene installato solo come esempio. Il pacchetto attuale non è firmato digitalmente: Windows potrebbe mostrare un avviso di editore sconosciuto; verifica provenienza e hash SHA-256 prima di eseguirlo.

Per controllare la versione installata, apri le proprietà di `%LOCALAPPDATA%\Programs\FileOrganizer\FileOrganizer.Configurator.exe`, scheda **Dettagli → Versione file**. Dopo un aggiornamento anche il motore in `engine\FileOrganizer.exe` deve riportare la stessa versione. La voce nelle app installate viene aggiornata dal setup.

### Prima configurazione

1. Premi **New** nel configuratore: la simulazione (`dry_run`) è attiva e la nuova cartella è disabilitata.
2. Scegli la sorgente con **Browse**, abilitala e configura le regole. La prima regola attiva che corrisponde determina la destinazione. Estensioni e pattern, se entrambi specificati, devono entrambi corrispondere.
3. Usa **Filename Preview** e **Validate** per controllare i filtri. In **Managed paths**, verifica quali directory sono incluse nella bozza prima di salvare: con **Recursive** disattivato vengono elaborati solo i file diretti della sorgente e di ogni destinazione esplicita.
4. Salva con **Save As** in `%LOCALAPPDATA%\FileOrganizer\config.toml`. Crea la cartella `FileOrganizer` se il dialogo lo richiede.
5. Premi **Avvia scansione** nella GUI. Con la simulazione attiva non viene spostato nulla; l'esito appare in basso. È disponibile anche **Scansione adesso** dal menu Start.
6. Dopo la verifica, disattiva `dry_run`, salva e usa **Avvia organizzazione** per il monitoraggio continuo. La finestra del motore deve restare aperta; `Ctrl+C` lo arresta.

I collegamenti **Scansione adesso** e **Avvia organizzazione** usano la configurazione predefinita sopra indicata. La GUI può avviare una scansione singola con il TOML aperto e salvato; per il monitoraggio continuo usa **Avvia organizzazione** o la CLI con `--config`.

### Avvio con Windows

Nel configuratore, salva una configurazione valida e abilita **Start with Windows**. Il motore installato in `engine\FileOrganizer.exe` viene individuato automaticamente; puoi selezionarne un altro con **Browse**. Il controllo legge e modifica la vera registrazione HKCU e segnala comandi diversi o errori di accesso.

Il toggle avvia il motore al prossimo accesso a Windows, non immediatamente. L’avvio automatico usa `run --background`, senza mantenere un terminale aperto; i messaggi operativi rimangono nei log rotanti. Per partire subito in una finestra controllabile usa **Avvia organizzazione**; per fermare anche il motore in background usa **Arresta organizzazione** dal menu Start. È consentito un solo motore per utente Windows, anche con TOML diversi: arresta il monitoraggio prima di una scansione manuale. Restrizioni di Windows o Gestione attività possono impedire l’avvio anche quando la voce è registrata.

### Aggiornamento, disinstallazione e trasferimento

- Arresta il motore con **Arresta organizzazione** o `engine\FileOrganizer.exe stop` e chiudi la GUI prima di aggiornare o disinstallare. Per una versione precedente usa `Ctrl+C` nella finestra del motore. Il setup non forza la chiusura dei processi durante uno spostamento.
- Per aggiornare, esegui il nuovo installer sullo stesso percorso. Configurazioni e log in `%LOCALAPPDATA%\FileOrganizer` vengono conservati.
- Disinstalla da **Impostazioni → App** oppure dal menu Start. Vengono rimossi programmi, collegamenti e registrazioni di avvio che puntano al motore di questa installazione. Configurazioni, log e file organizzati rimangono.
- Per un amico o un altro PC, condividi soltanto il setup. Puoi copiare separatamente il TOML per riutilizzare le regole, adattando i percorsi prima di attivare gli spostamenti. L’avvio automatico va registrato nuovamente sul PC destinatario.

## Esempio di regole

Per una sorgente `Downloads`, le destinazioni relative vengono risolte rispetto a quella cartella:

| Priorità | Estensioni | Pattern del nome | Destinazione |
|---|---|---|---|
| 1 | `.png`, `.jpg`, `.tiff`, `.gif`, `.bmp` | `*chatgpt*`, `*gemini*` | `Images\ai` |
| 2 | `.png`, `.jpg`, `.jpeg`, `.tiff`, `.gif`, `.bmp` | qualsiasi | `Images` |
| 3 | `.pdf` | qualsiasi | `Documents` |
| 4 | `.exe` | qualsiasi | `Eseguibili` |

Più pattern nella stessa regola sono alternative. I file senza corrispondenza rimangono dove sono. [Config.Example.toml](Config.Example.toml) contiene un esempio completo con variabili d’ambiente.

### Aggiunta rapida delle regole

Nel menu a sinistra di **Add**, **New rules** crea la consueta regola vuota. **Images**, **Documents**, **Archives**, **Videos**, **Audio** e **3D** precompilano nome ed estensioni della nuova regola solo quando premi **Add**. Il tooltip mostra l'elenco completo dei formati; puoi sempre modificarli. Le categorie comprendono immagini comuni/RAW e PSD/Aseprite, documenti Office/OpenDocument/PDF/e-book, archivi compressi, video, audio, modelli/scenari e CAD 3D. Scegli la destinazione prima di salvare: resta vuota. Le regole vengono aggiunte in fondo, mantenendo la priorità di quelle esistenti.

Sono disponibili anche **Installers**, **Fonts**, **E-books**, **Subtitles**, **Torrents**, **Unity Packages**, **Source Code** e **Graphic Projects**. Per dare precedenza alle categorie specifiche, sposta E-books sopra Documents e Graphic Projects sopra Images.

### Riclassificazione

Aggiungendo una regola `*gatt*` prima della regola generica delle immagini, con destinazione `Images\Cats`, vengono spostati anche i file corrispondenti già nei percorsi inclusi di `Images`. Riportando la regola generica al primo posto, tornano in `Images`.

Dalla versione 1.2.0, `recursive=false` limita sia sorgente sia ogni destinazione esplicita ai file direttamente presenti: un progetto annidato in `Images\AI` rimane intatto. `recursive=true` include anche le sottocartelle. Le selezioni individuali di **Managed paths** hanno precedenza sul valore predefinito. I file inseriti manualmente seguono le stesse regole; non esiste un archivio della loro provenienza. I file già direttamente nella destinazione vincente vengono ignorati prima di attese e hash, evitando copie o cancellazioni su se stessi.

Salvare nuove regole può quindi spostare subito file già organizzati, se il motore è attivo e osserva il TOML. Un cambiamento di destinazione mantiene gestita quella precedente per la sessione corrente. Dopo un riavvio rimangono gestite solo le destinazioni attuali o quelle ancora coperte da una sorgente ricorsiva/destinazione attiva. Destinazioni sovrapposte appartenenti a monitor diversi vengono rifiutate. Se un suffisso generato per un duplicato cambia la destinazione vincente, il file sorgente viene preservato e il conflitto segnalato nel log.

## Albero dei percorsi

Apri **Managed paths** e scegli **Draft**, **Saved file** o **Applied engine**, poi **Load / refresh paths**. Le ultime due viste sono in sola lettura; quella applicata include anche le vecchie destinazioni ancora gestite nella sessione. Con motore fermo o precedente alla 1.2.0, il suo ambito è dichiarato non verificabile.

| Simbolo | Ambito |
|---|---|
| □ grigio | Disattivo: nessuna esplorazione dei discendenti, che restano nascosti. |
| ■ | File diretti e singoli rami eventualmente selezionati; non tutto il sottoalbero. |
| ✓ | Tutto il sottoalbero ricorsivamente, comprese nuove sottocartelle. |

Se una cartella non contiene sottocartelle, entrambi i clic alternano soltanto **□ ↔ ✓**. Una cartella già inclusa direttamente appare completa finché non ha sottocartelle; eventuali nuove cartelle seguono il modo salvato nel TOML. Negli altri casi, clic sinistro (o Spazio): **□ → ■ → ✓ → □**. Clic destro: **□ → ✓ → ■ → □**. Attivando un ramo escluso in modalità ■ vengono mostrate solo le sue sottocartelle immediate, senza attivarle. ✓ e □ sostituiscono le selezioni sottostanti; un'eccezione in un ramo ricorsivo fa apparire il simbolo ■. Espandi gli altri rami su richiesta: nessuna scansione profonda automatica della GUI o polling.

L'albero elenca sorgenti, destinazioni esplicite e percorsi selezionati, anche se annidati: ogni percorso compare una sola volta, sotto la radice configurata più breve. La catena fino alle destinazioni annidate si apre automaticamente, anche con più monitor. I livelli intermedi che collegano destinazioni esplicite non abilitano il monitoraggio dei propri file: il tooltip lo indica; vengono collegati solo i percorsi noti dal TOML. I clic modificano la **bozza**; premi **Save** per validarla e applicarla. Un monitor globalmente disabilitato va prima abilitato in **Folders and rules**: l'albero non lo riattiva implicitamente. Le selezioni sono salvate nel TOML negli array opzionali `direct_paths`, `recursive_paths`, `excluded_paths`; i percorsi sono letterali, assoluti o relativi alla sorgente, senza espansione di variabili. Le vecchie configurazioni funzionano senza questi campi. Le selezioni regolano il monitoraggio: disattivare una destinazione non disabilita le regole che vi scrivono.

Con `recursive=false` e destinazioni `Images` e `Images\AI`, i file direttamente in entrambe vengono ricontrollati, ma `Images\AI\Game_Boy_Advance_era_style._female_Character_fanta` non viene esplorata. Anche `Downloads\spesasmart` resta esclusa se non selezionata esplicitamente.

## CLI e output

Nella GUI puoi usare **Avvia scansione** per una scansione singola con il TOML salvato e validato. Avvia è disponibile solo a motore fermo; se ci sono modifiche in bozza, chiede di salvarle prima di procedere. No, annullamento del dialogo o salvataggio fallito impediscono l’avvio. Per una nuova configurazione viene richiesto Save As. Risolvi prima eventuali conflitti con il file. Se il motore è già attivo in background, arrestalo con **Arresta scansione** e attendi che risulti fermo prima di avviare la scansione singola. Il blocco per utente impedisce elaborazioni simultanee anche in caso di clic concorrenti.

**Arresta scansione** è disponibile solo durante una scansione o con un motore attivo e richiede un arresto sicuro: gli spostamenti già completati restano registrati e non vengono annullati. Per una scansione avviata dalla GUI, il configuratore attende l'uscita del processo senza terminarlo forzatamente. Per un motore già in background invia la richiesta di arresto: usa **Verify state** per verificarne la conclusione. Il motore rimane fermo. Nessun terminale viene aperto e l'esito appare in basso. Con `dry_run=true` viene eseguita una simulazione. Al termine si aggiornano cronologia e stato applicato. I pulsanti non modificano l'avvio automatico di Windows.

Dal terminale, nel percorso di installazione:

```powershell
.\engine\FileOrganizer.exe validate
.\engine\FileOrganizer.exe scan
.\engine\FileOrganizer.exe run
.\engine\FileOrganizer.exe run --background
.\engine\FileOrganizer.exe stop
.\engine\FileOrganizer.exe install
.\engine\FileOrganizer.exe uninstall
.\engine\FileOrganizer.exe --help

# Un TOML diverso dal predefinito; salva l’output della simulazione:
.\engine\FileOrganizer.exe scan --config "C:\percorso\config.toml" 2>&1 | Tee-Object scan.txt
```

`install` e `uninstall` gestiscono esclusivamente la registrazione di avvio automatico, non l’installazione/disinstallazione dei programmi. Tutti i comandi accettano `--config`. Il percorso predefinito è `%LOCALAPPDATA%\FileOrganizer\config.toml`.

La simulazione scrive in console senza creare destinazioni o log. Nell’esecuzione normale i log sono in `%LOCALAPPDATA%\FileOrganizer\logs\<config-id>`: un file da 1 MiB più tre archivi. Il livello predefinito è `warning`; scegli `info` per vedere anche gli spostamenti riusciti. Codici di uscita: `0` successo, `2` configurazione/argomenti invalidi, `3` errore operativo o istanza già attiva, `130` annullamento.

`stop` richiede l’arresto sicuro dell’unico motore dell’utente, indipendentemente dal TOML. `run --background` scollega la console: per leggere una simulazione usa `scan` in primo piano; gli errori di avvio in background sono registrati in `logs\startup`. L’aggiornamento del setup converte le registrazioni autorun già appartenenti a questa installazione alla modalità background, conservando il percorso del TOML.

Per organizzare solo i file direttamente in Downloads, lascia **Recursive** disattivato. Attivandolo si elaborano anche file interni a programmi e archivi già estratti: non distingue queste cartelle dalle altre. Anche ogni destinazione esplicita segue questo valore: con `recursive=false` vengono ricontrollati solo i suoi file diretti. Le selezioni individuali nell’albero hanno precedenza; una destinazione annidata configurata esplicitamente viene comunque controllata come percorso autonomo.

## Configuratore

Puoi aggiungere, eliminare, duplicare, abilitare/disabilitare e riordinare cartelle e regole; modificare filtri, esclusioni, destinazioni, duplicati e impostazioni generali. Sorgenti e destinazioni possono contenere variabili d’ambiente; gli override dei percorsi nell’albero sono invece letterali.

**Save** valida l’intero documento con il parser del motore, conserva commenti e impostazioni e scrive atomicamente, mantenendo una copia `.bak`. Le modifiche esterne impediscono di sovrascrivere una versione più recente: usa **Reload** o **Save As**. La scheda TOML consente modifiche dirette; applicale con **Apply TOML** prima di riprendere l’editing dei campi. Proprietà sconosciute vengono conservate, ma bloccano il salvataggio se il motore non le accetta.

Scorciatoie: `Ctrl+S` salva, `Ctrl+O` apre, `Ctrl+N` crea. Salvare con un nuovo nome non trasferisce una registrazione di avvio del vecchio TOML. Il toggle autorun non indica se il processo è attualmente in esecuzione.

### Impostazioni generali e valori predefiniti

| Campo TOML | Default del parser | Effetto |
|---|---|---|
| `scan_on_startup` | `true` | Scansione iniziale per `run`; `scan` esegue sempre una scansione singola. |
| `watch_config_changes` | `true` | Ricarica e riconcilia solo configurazioni valide; altrimenti conserva l’ultima valida. |
| `dry_run` | `false` | Se attivo, mostra le operazioni senza scrivere file o cronologia. **New** nella GUI lo imposta a `true`. |
| `log_level` | `warning` | `error`, `warning`, `info` o `debug`. |
| `file_ready_timeout_seconds` | `120` | Attesa massima di disponibilità; intervallo accettato 1–86400. |
| `max_parallel_moves` | `2` | Limite di concorrenza; intervallo accettato 1–16. |

La politica duplicati predefinita è `name_collision="increment"`, `identical_content="copy"`, `copy_suffix="_copy_{n}"`: conserva entrambi i file. `identical_content="delete"` richiede una scelta esplicita e non è annullabile tramite History.

## Sicurezza e limiti

- Nessuna sovrascrittura implicita. La cancellazione di duplicati identici richiede la relativa politica esplicita e la verifica del contenuto.
- I trasferimenti tra volumi usano un file temporaneo esclusivo, flush e verifica hash prima di eliminare la sorgente.
- Download temporanei, junction, reparse point, hard link, percorsi ambigui e condivisioni di rete non vengono elaborati. Le sorgenti devono esistere; le destinazioni vengono create quando servono.
- Il motore usa una coda limitata, deduplica eventi e controlla la stabilità dei file. Watcher sani non eseguono scansioni periodiche; quelli guasti riprovano con attesa di cinque secondi.
- Un arresto dopo il commit di una copia tra volumi può lasciare entrambe le copie complete. Non sono garantiti recupero da perdita di alimentazione, comportamento di driver ostili o scrittori tramite memory mapping.
- Schema TOML v1. Disabilitare l’osservazione del TOML richiede un riavvio per riattivarla. Il motore funziona senza GUI, database, servizi Windows o connessioni cloud.

I contratti tecnici sono in [REQUIREMENTS.md](REQUIREMENTS.md), gli algoritmi in [FUNCTIONAL.md](FUNCTIONAL.md) e la matrice di verifica in [TEST.md](TEST.md).

## Compilazione e creazione del setup

Requisiti di sviluppo: Windows x64 e .NET 10 SDK (versione in [global.json](global.json)). Native AOT richiede anche gli strumenti C++ di Visual Studio e Windows SDK; il publish standard non li richiede. Per il setup serve [Inno Setup 6.3 o successivo](https://jrsoftware.org/isdl.php).

```powershell
./scripts/build.ps1
./scripts/build-installer.ps1 -Version 1.2.5

# Fallback senza toolchain C++:
./scripts/build-installer.ps1 -Version 1.2.5 -EngineMode Standard

# Compilatore Inno in un percorso diverso:
./scripts/build-installer.ps1 -CompilerPath "C:\tools\Inno Setup 6\ISCC.exe"
```

`build-installer.ps1` pubblica motore e GUI self-contained, prepara un payload limitato agli output pubblicati, al README, all’esempio TOML e alle licenze delle dipendenze, poi genera `artifacts/installer/FileOrganizer-<version>-win-x64-Setup.exe` e il relativo file `.sha256`. `-SkipPublish` riutilizza gli output esistenti e va usato solo se aggiornati. Il setup non viene caricato automaticamente su GitHub: pubblica il `.exe` e il suo hash come asset di una Release, senza aggiungere i binari a Git.

Per i pacchetti portabili:

```powershell
./scripts/publish.ps1 -Mode Aot
./scripts/publish.ps1 -Mode Standard
./scripts/publish-configurator.ps1
./scripts/verify-publish.ps1 -Mode Aot
```

Output in `artifacts/publish/`. Per AOT basta `FileOrganizer.exe`; per motore standard e GUI distribuisci l’intera rispettiva cartella. Il configuratore portabile accetta `--config <TOML>` e permette di selezionare il motore per l’avvio automatico.

## Test e risorse

`scripts/build.ps1` compila l’intera soluzione ed esegue i test; `-EngineOnly` esclude WPF. Per includere il test fisico tra volumi, imposta `FILEORGANIZER_TEST_SECOND_VOLUME` su un percorso temporaneo scrivibile in un altro disco. Il test crea e rimuove solo una propria sottocartella univoca. In assenza di questa variabile quel test viene saltato; i test non usano le vere cartelle Download o Desktop.

Verifica del 15 ottobre 2026, Windows x64/.NET SDK 10.0.401: **152 test superati** (127 core/integrazione, 25 WPF), zero errori o avvisi, incluso il trasferimento C: → D:. Verificati watcher, riclassificazione, duplicati, collisioni, salvataggi e conflitti TOML, cronologia/undo persistenti e tra volumi, cancellazione e scritture interrotte, sincronizzazione della ricorsività, stato applicato/rifiutato, nomi contenenti percentuali, file scomparsi durante l’attesa, blocco tra TOML diversi, arresto sicuro, permessi e registrazioni HKCU su chiavi isolate. Avvio effettivo al login Windows, altre versioni Windows e dialoghi nativi richiedono ancora una verifica manuale.

Il setup è stato verificato con installazione per utente senza amministratore, collegamenti, motore installato, simulazione, avvio/chiusura GUI, reinstallazione senza perdita del TOML, disinstallazione e rimozione selettiva dell’autorun. Puoi ripetere la prova con `./scripts/verify-installer.ps1`: usa una cartella sotto `artifacts/installer`, crea temporaneamente proprie voci HKCU e le pulisce alla fine. Richiede un account senza installazioni o collegamenti File Organizer preesistenti, per non interferire con l’uso reale. I log restano negli artefatti di verifica.

Se hai già installato File Organizer, usa `./scripts/verify-installer.ps1 -Isolated`: ricompila lo stesso payload con un’identità e un gruppo di collegamenti separati, senza modificare l’installazione reale. Richiede Inno Setup e il file locale `.build.json` prodotto dalla build; per un compilatore non rilevato passa `-CompilerPath`. La verifica controlla anche l’icona assegnata ai collegamenti.

Campione della versione 1.0.0 a riposo dopo due secondi di riscaldamento, una cartella abilitata, simulazione e nessun evento:

| Motore | Eseguibile | Memoria working set | Tempo CPU nel campione |
|---|---:|---:|---:|
| Native AOT | 2.943.488 byte | 15.290.368 byte | 0 ms / 10,016 s |
| .NET self-contained | 162.304 byte apphost | 35.016.704 byte | 0 ms / 10,011 s |

Sono osservazioni su questa macchina, non garanzie. `scripts/verify-publish.ps1` salva le misure in `artifacts/verification-<mode>.json`.

## Struttura del progetto

- `src/FileOrganizer.Core`: configurazione, validazione, regole e directory gestite.
- `src/FileOrganizer.Windows`: trasferimenti sicuri, watcher, persistenza e autorun.
- `src/FileOrganizer`: CLI e motore.
- `src/FileOrganizer.Configurator`: editor WPF indipendente.
- `tests`: test core, Windows e WPF.
- `installer` e `scripts`: setup, build, publish e verifiche.

`assets/FileOrganizer.png` è il sorgente trasparente dell’icona; `scripts/build-icon.ps1` genera `assets/FileOrganizer.ico` con dieci risoluzioni da 16 a 256 pixel e canale alpha. Entrambi gli eseguibili e il setup utilizzano questo asset, senza dipendenze grafiche aggiuntive nel motore.

Tomlyn è l’unica dipendenza applicativa esterna; xUnit è usato nei test. Per problemi o proposte, apri una [issue](https://github.com/federyuk91/Organizer/issues) includendo versione, errore e una configurazione depurata da percorsi o dati personali.

## Cronologia e annullamento (dalla versione 1.1.0)

La scheda **History** mostra gli spostamenti registrati negli ultimi sette giorni. Aprendola, o premendo **Refresh history**, aggiorni l’elenco. Quando arrivano nuovi movimenti, un avviso segnala che l’elenco va aggiornato. **Show all configurations** include anche TOML rinominati o eliminati.

Seleziona una o più righe con Ctrl/Shift e premi **Undo selected**. Dopo la conferma, l’app richiede l’arresto sicuro del motore e acquisisce il blocco esclusivo per tutta l’operazione. Ripristina i movimenti dal più recente al più vecchio, compresi nomi modificati dalla gestione duplicati e trasferimenti tra volumi. Il motore rimane fermo: controlla le regole prima di riavviarlo, altrimenti potrebbero rispostare i file. **Cancel undo** interrompe l’operazione conservando i ripristini già completati.

Ogni file viene verificato tramite identità del filesystem, dimensione, data di modifica e SHA-256. Se è stato modificato, sostituito, eliminato o il percorso originale è occupato, viene saltato con una spiegazione. Non si sovrascrivono file. Junction e hard link vengono rifiutati anche durante l’annullamento.

La cronologia è disponibile dalla versione 1.1.0 e registra solo gli spostamenti eseguiti da un motore che la supporta: non ricostruisce automaticamente i movimenti precedenti, le operazioni manuali o le cancellazioni di duplicati identici. Non è un backup del contenuto dei file. Il registro contiene percorsi, identità e hash, resta nel profilo utente in `%LOCALAPPDATA%\FileOrganizer\history` e non viene usato per decidere le regole di classificazione. I metadati scaduti vengono eliminati durante le operazioni, senza timer periodici; la scadenza non elimina mai file organizzati.

Il motore salva e forza su disco il record prima di uno spostamento. Se non può registrarlo, ferma le elaborazioni. Le scritture incomplete in coda sono recuperate conservando l’ultimo record completo; un trasferimento interrotto viene annullato solo quando il suo risultato è verificabile. Lo hashing aggiunge una lettura sequenziale dei file effettivamente spostati; non vengono create copie di backup per gli spostamenti sullo stesso volume.

## Form, file e impostazioni applicate

La parte superiore del configuratore distingue sempre la **bozza**, i valori **ON DISK** del TOML e lo snapshot applicato dal **ENGINE**. Mostra esplicitamente percorso sorgente, abilitazione e **Include subfolders**. Un checkbox modificato ma non salvato rappresenta una bozza: il banner rosso ricorda che non è ancora applicata.

Se il TOML cambia esternamente e la bozza è pulita, il form viene ricaricato automaticamente. Se esistono modifiche locali, vengono conservate, il conflitto resta evidente e l’editing/sovrascrittura vengono bloccati: usa **Reload** oppure **Save As**. Il salvataggio rilegge il file realmente scritto. Il controllo viene ripetuto anche tornando sulla finestra e premendo **Verify state**, senza polling periodico.

Il motore pubblica un piccolo file diagnostico atomico, `engine-state.json`, con PID, ora di avvio, eseguibile, TOML, hash dello snapshot e impostazioni applicate. Non è una seconda configurazione. La GUI verifica PID e ora di avvio prima di considerarlo attivo. **Open engine's TOML** apre il file effettivamente usato. Un avviso sempre visibile segnala TOML diverso, modifiche non ancora applicate o reload rifiutato; in quest’ultimo caso vengono mostrate le impostazioni precedenti ancora in uso. Con motore fermo, versione precedente o stato non leggibile, la GUI dichiara lo stato non verificabile e non presenta il form come prova delle impostazioni applicate.
