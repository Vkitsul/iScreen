# iScreen

Desktop remoto Windows → Mac in rete locale, pensato per **lavoro**: testo nitido in 4:4:4 anche
su GPU AMD e **colori come sul monitor del PC**. Dettagli tecnici e protocollo in
[docs/ARCHITETTURA.md](docs/ARCHITETTURA.md).

| Parte | Cartella | Stato |
|---|---|---|
| App Windows nella barra delle applicazioni (`iScreen.exe`) | `src/iScreen.Tray` → `dist/iScreen` | compilata e testata su questo PC |
| Strumento a riga di comando (`iscreen-host.exe`, diagnostica) | `src/iScreen.Host` → `dist/iScreen` | compilato e testato |
| Motore condiviso (cattura, codifica, rete) | `src/iScreen.Core` | — |
| Client Mac (Swift, Metal, VideoToolbox) | `clients/mac` | scritto, **da compilare sul Mac** (primo build: probabile qualche errore da sistemare) |

## 1. Sul PC Windows

Fai doppio clic su **`dist\iScreen\iScreen.exe`**. Non si apre nessuna finestra: compare
un'icona a forma di monitor vicino all'orologio (blu = in attesa, verde = Mac collegato).

- **Si avvia da sola** a ogni accensione di Windows: alla prima apertura attiva "Avvia con Windows",
  che puoi togliere dal menu dell'icona. Se sposti la cartella `dist\iScreen`, riapri `iScreen.exe`
  e riattiva la voce.
- **Menu dell'icona** (clic sinistro o destro): stato e banda, **PIN** (clic per copiarlo), **schermo
  da trasmettere** (cambia anche durante il collegamento: il Mac si adatta da solo), Avvia con
  Windows, controllo delle app da amministratore, log, Esci.
- La prima volta Windows chiede il permesso del firewall per `iScreen.exe`: consenti sulle **reti
  private**. In alternativa, da un PowerShell amministratore:
  `New-NetFirewallRule -DisplayName iScreen -Direction Inbound -Program "C:\Users\user\Desktop\iScreen\dist\iScreen\iScreen.exe" -Action Allow -Profile Private`
- Il PIN e lo schermo scelto sono in `%APPDATA%\iScreen\host.json`, il log in `%APPDATA%\iScreen\host.log`.
- **Chiudi Parsec mentre usi iScreen**: condividono l'encoder video della Radeon. iScreen chiama AMF
  direttamente: circa 3 ms per frame in 4:2:0 e 5 ms in 4:4:4. I pacchetti persi in Wi-Fi vengono
  ricostruiti dal Mac con la FEC (20% di parità) senza doverli richiedere.
- **Finestre da amministratore e richieste UAC**: Windows non lascia che una normale app comandi le
  finestre aperte "come amministratore", e la richiesta di Controllo account utente (come la schermata
  di blocco) sta su un desktop protetto che solo SYSTEM può vedere. Per usarle dal Mac, una volta,
  **davanti al PC**: menu dell'icona → *Controlla anche le app da amministratore…* → OK → Sì alla
  richiesta di Windows. iScreen viene copiato in `C:\Program Files\iScreen` e installato come servizio
  di Windows (come fa Parsec), che lo fa girare come SYSTEM sul tuo desktop. Si torna indietro dal menu:
  *Disattiva il controllo delle app da amministratore…*. In questa modalità impostazioni e log stanno
  in `C:\ProgramData\iScreen` (il servizio scrive in `service.log`), "Avvia con Windows" decide se il
  servizio parte all'accensione, e dopo una ricompilazione l'aggiornamento si installa con
  `Start-Process dist\iScreen\iScreen.exe -ArgumentList --install-service -Verb RunAs`.
- Se l'encoder video della Radeon si blocca, iScreen **si riavvia da solo** (avviso "iScreen
  riavviato"): ricollegati dal Mac. Se si blocca di nuovo subito dopo compare "La GPU non risponde":
  riavvia il PC.

Riga di comando (solo per diagnostica; non insieme all'app, usano la stessa porta):
`dist\iScreen\iscreen-host.exe list | serve --monitor 1 | selftest | testclient`. In `serve`, scrivere
il numero di un monitor + Invio cambia lo schermo trasmesso.

## 2. Sul Mac (macOS 13+)

```bash
xcode-select --install            # una volta, se non hai Xcode
cd iScreen/clients/mac            # copia qui la cartella clients/mac dal PC
./build-app.sh
open build/iScreen.app
```

Inserisci **PC Windows** = `192.168.1.57` (IP Ethernet del PC) e il **PIN**. Al primo collegamento
macOS chiede il permesso "Rete locale": va concesso.

Se `swift build` dà errori, incollameli: il client non è ancora mai stato compilato.

### Comandi

| Tasto sul Mac | Arriva su Windows come |
|---|---|
| ⌘ (sinistro e destro) | Ctrl (⌘C, ⌘V, ⌘Z… funzionano come sul Mac) — disattivabile nel form |
| ⌃ sinistro | tasto Windows (menu Start, Win+Shift+S, Win+E…) |
| ⌥ sinistro / ⌥ destro | Alt / AltGr (per `@ # [ ]` sulla tastiera italiana) |
| F13 / F14 | Stamp / Bloc Scorr |
| ⌃⌘F · ⌃⌘1 · ⌘Q | restano al Mac: schermo intero · dimensione reale 1:1 · esci |

La barra del titolo mostra fps, latenza cattura→schermo (e quanta è sul PC) e banda.
Il cursore è disegnato localmente dal Mac, quindi segue il mouse senza ritardo.

**Adatta la risoluzione del PC a questo Mac** (nel form, attivo di default): durante il collegamento
il PC passa alla risoluzione con le proporzioni del Mac (per il tuo MacBook 1920×1200 invece di
2560×1440) così l'immagine riempie lo schermo senza bordi neri, e la ripristina quando ti scolleghi.
I monitor del PC nel frattempo mostrano bande nere. Toglila se preferisci la massima nitidezza.

**Appunti condivisi** (nel form, attivo di default): copi un testo sul Mac e lo incolli sul PC, e viceversa,
come in Parsec. Solo testo e solo mentre sei collegato.

**Audio del PC** (nel form, attivo di default): il Mac riproduce quello che suona sul PC, preso dal
dispositivo di uscita predefinito di Windows (oggi le cuffie G733). Ritardo aggiuntivo circa 30–40 ms.

**Colori** (nel form): *Come il monitor del PC* usa i primari letti dal monitor catturato, così il
Mac riproduce la saturazione che vedi sul PC; *sRGB standard* mostra i colori "corretti";
*Nessuna conversione* manda i valori diretti al pannello del Mac.

## Strumenti di verifica (sul PC)

```powershell
cd C:\Users\user\Desktop\iScreen\dist\iScreen
.\iscreen-host.exe selftest --monitor 1 --save C:\temp\iscreen   # qualità 4:2:0 vs 4:4:4, PNG di confronto
.\iscreen-host.exe testclient --seconds 8 --loss 0.03            # client di test (con host avviato), simula perdite
```

Risultati misurati su questo PC (loopback, pattern 2560×1440): testo colorato 49.5 dB in 4:4:4 contro
23.9 dB in 4:2:0; 60 fps; con il 3% di pacchetti persi tutti i frame recuperati via NACK.

## Ricompilare l'host

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" publish src\iScreen.Tray -c Release -o dist\iScreen
& "$env:USERPROFILE\.dotnet\dotnet.exe" publish src\iScreen.Host -c Release -o dist\iScreen
```

## Limiti noti

Monitor ruotati (VG249 verticale) e HDR non ancora gestiti; nessuna cifratura (solo PIN, ok in LAN);
appunti solo testo (no immagini/file); UAC e schermata di blocco solo con il controllo delle app da
amministratore attivo; Ctrl+Alt+Canc non si può inviare.
