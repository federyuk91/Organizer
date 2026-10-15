# File Organizer

Organizza automaticamente i file su **Windows 10/11 x64**, con regole ordinate e un’interfaccia grafica per configurarle. Il motore lavora separatamente dalla GUI: puoi chiudere il configuratore e continuare a organizzare le cartelle.

## Funzionalità

- Più cartelle monitorate, ciascuna con regole indipendenti e priorità modificabile.
- Filtri per estensione, nome tramite glob (`*gatt*`, `*.pdf`) ed esclusioni.
- Destinazioni relative o assolute, anche su altri dischi.
- Riclassificazione dei file già nelle destinazioni quando cambiano le regole, inclusi quelli inseriti manualmente.
- Gestione dei duplicati, simulazione senza spostamenti e log a dimensione limitata.
- Configurazione TOML condivisa tra GUI e CLI, con validazione e salvataggio atomico.
- Avvio con Windows per l’utente corrente, configurabile dalla GUI senza amministratore.
- Monitoraggio a eventi, coda limitata e nessuna scansione periodica durante il funzionamento normale.

## Installazione

1. Scarica `FileOrganizer-1.0.1-win-x64-Setup.exe` dalla sezione [Releases](https://github.com/federyuk91/Organizer/releases), quando disponibile. Compilando il repository, il file viene prodotto in `artifacts/installer/`.
2. Avvia il setup con un doppio clic. Installa motore, configuratore e runtime necessari: sul PC destinatario non servono .NET, PowerShell o Visual Studio.
3. Mantieni il percorso proposto, `%LOCALAPPDATA%\Programs\FileOrganizer`, oppure scegline uno scrivibile dal tuo utente. L’installazione è per utente e non richiede privilegi amministrativi.
4. Apri **File Organizer** dal menu Start; puoi scegliere anche il collegamento sul desktop durante il setup.

L’icona trasparente della cartella viene assegnata automaticamente al programma, alle finestre, al setup e ai collegamenti creati durante l’installazione.

Il setup non crea una configurazione attiva, non avvia il motore e non registra l’avvio automatico. `Config.Example.toml` viene installato solo come esempio. Il pacchetto attuale non è firmato digitalmente: Windows potrebbe mostrare un avviso di editore sconosciuto; verifica provenienza e hash SHA-256 prima di eseguirlo.

### Prima configurazione

1. Premi **New** nel configuratore: la simulazione (`dry_run`) è attiva e la nuova cartella è disabilitata.
2. Scegli la sorgente con **Browse**, abilitala e configura le regole. La prima regola attiva che corrisponde determina la destinazione. Estensioni e pattern, se entrambi specificati, devono entrambi corrispondere.
3. Usa **Filename Preview** e **Validate** per controllare filtri e percorsi.
4. Salva con **Save As** in `%LOCALAPPDATA%\FileOrganizer\config.toml`. Crea la cartella `FileOrganizer` se il dialogo lo richiede.
5. Dal menu Start, apri **File Organizer → Scansione adesso**. Con la simulazione attiva non viene spostato nulla. Per leggere l’output anche dopo la fine del comando, vedi gli esempi CLI sotto.
6. Dopo la verifica, disattiva `dry_run`, salva e usa **Avvia organizzazione** per il monitoraggio continuo. La finestra del motore deve restare aperta; `Ctrl+C` lo arresta.

I collegamenti **Scansione adesso** e **Avvia organizzazione** usano la configurazione predefinita sopra indicata. Per un altro TOML, usa la CLI con `--config`. La GUI modifica la configurazione, ma non contiene un pulsante di scansione e non avvia il motore.

### Avvio con Windows

Nel configuratore, salva una configurazione valida e abilita **Start with Windows**. Il motore installato in `engine\FileOrganizer.exe` viene individuato automaticamente; puoi selezionarne un altro con **Browse**. Il controllo legge e modifica la vera registrazione HKCU e segnala comandi diversi o errori di accesso.

Il toggle avvia il motore al prossimo accesso a Windows, non immediatamente. Per partire subito usa **Avvia organizzazione**. Non eseguire contemporaneamente più istanze sulla stessa configurazione. Restrizioni di Windows o Gestione attività possono impedire l’avvio anche quando la voce è registrata.

### Aggiornamento, disinstallazione e trasferimento

- Arresta il motore con `Ctrl+C` e chiudi la GUI prima di aggiornare o disinstallare. Il setup non forza la chiusura dei processi durante uno spostamento.
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

### Riclassificazione

Aggiungendo una regola `*gatt*` prima della regola generica delle immagini, con destinazione `Images\Cats`, vengono spostati anche i file corrispondenti già in `Images` e nelle sue sottocartelle. Riportando la regola generica al primo posto, tornano in `Images`.

Le destinazioni sono sempre gestite ricorsivamente, anche se la sorgente ha `recursive=false`. I file inseriti manualmente seguono le stesse regole; non esiste un archivio della loro provenienza. I file già direttamente nella destinazione vincente vengono ignorati prima di attese e hash, evitando copie o cancellazioni su se stessi.

Salvare nuove regole può quindi spostare subito file già organizzati, se il motore è attivo e osserva il TOML. Un cambiamento di destinazione mantiene gestita quella precedente per la sessione corrente. Dopo un riavvio rimangono gestite solo le destinazioni attuali o quelle ancora coperte da una sorgente ricorsiva/destinazione attiva. Destinazioni sovrapposte appartenenti a monitor diversi vengono rifiutate. Se un suffisso generato per un duplicato cambia la destinazione vincente, il file sorgente viene preservato e il conflitto segnalato nel log.

## CLI e output

Dal terminale, nel percorso di installazione:

```powershell
.\engine\FileOrganizer.exe validate
.\engine\FileOrganizer.exe scan
.\engine\FileOrganizer.exe run
.\engine\FileOrganizer.exe install
.\engine\FileOrganizer.exe uninstall
.\engine\FileOrganizer.exe --help

# Un TOML diverso dal predefinito; salva l’output della simulazione:
.\engine\FileOrganizer.exe scan --config "C:\percorso\config.toml" 2>&1 | Tee-Object scan.txt
```

`install` e `uninstall` gestiscono esclusivamente la registrazione di avvio automatico, non l’installazione/disinstallazione dei programmi. Tutti i comandi accettano `--config`. Il percorso predefinito è `%LOCALAPPDATA%\FileOrganizer\config.toml`.

La simulazione scrive in console senza creare destinazioni o log. Nell’esecuzione normale i log sono in `%LOCALAPPDATA%\FileOrganizer\logs\<config-id>`: un file da 1 MiB più tre archivi. Il livello predefinito è `warning`; scegli `info` per vedere anche gli spostamenti riusciti. Codici di uscita: `0` successo, `2` configurazione/argomenti invalidi, `3` errore operativo o istanza già attiva, `130` annullamento.

## Configuratore

Puoi aggiungere, eliminare, duplicare, abilitare/disabilitare e riordinare cartelle e regole; modificare filtri, esclusioni, destinazioni, duplicati e impostazioni generali. I percorsi possono contenere variabili d’ambiente.

**Save** valida l’intero documento con il parser del motore, conserva commenti e impostazioni e scrive atomicamente, mantenendo una copia `.bak`. Le modifiche esterne impediscono di sovrascrivere una versione più recente: usa **Reload** o **Save As**. La scheda TOML consente modifiche dirette; applicale con **Apply TOML** prima di riprendere l’editing dei campi. Proprietà sconosciute vengono conservate, ma bloccano il salvataggio se il motore non le accetta.

Scorciatoie: `Ctrl+S` salva, `Ctrl+O` apre, `Ctrl+N` crea. Salvare con un nuovo nome non trasferisce una registrazione di avvio del vecchio TOML. Il toggle autorun non indica se il processo è attualmente in esecuzione.

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
./scripts/build-installer.ps1 -Version 1.0.1

# Fallback senza toolchain C++:
./scripts/build-installer.ps1 -Version 1.0.1 -EngineMode Standard

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

Verifica del 15 ottobre 2026, Windows x64/.NET SDK 10.0.401: **94 test superati** (90 core/integrazione, 4 WPF), zero errori o avvisi, incluso il trasferimento C: → D:. Verificati watcher, riclassificazione, duplicati, collisioni, salvataggi e conflitti TOML, permessi e registrazioni HKCU su chiavi isolate. Accesso reale a Windows, altre versioni Windows e dialoghi nativi richiedono ancora una verifica manuale.

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
