# iScreen — architettura

Streaming del desktop Windows verso un Mac in rete locale, con priorità a **testo nitido** e
**colori identici al monitor del PC**.

## Perché Parsec perde colori e nitidezza sul tuo setup

1. **Chroma 4:2:0.** Sul PC c'è una Radeon RX 7700 XT. L'encoder hardware AMD (VCN 4) non fa
   HEVC/AV1 4:4:4, quindi Parsec trasmette il colore a 1/4 della risoluzione: testo colorato,
   linee sottili e bordi di icone diventano sbavati.
2. **Monitor wide-gamut.** Primari letti da DXGI/EDID:
   | Monitor | Primari R / G / B (CIE xy) | Area vs sRGB |
   |---|---|---|
   | Gigabyte G27Q (`GBT2709`, `\\.\DISPLAY18`) | (0.677,0.314) (0.280,0.667) (0.140,0.040) | ~133% (≈ DCI-P3) |
   | H27E6 (`SKG2738`, `\\.\DISPLAY17`, primario) | (0.683,0.315) (0.230,0.701) (0.143,0.057) | ~145% |
   | ASUS VG249 (`AUS2421`, `\\.\DISPLAY19`, verticale) | (0.642,0.329) (0.309,0.626) (0.150,0.054) | ~106% |

   Windows (SDR, senza ACM) manda i valori RGB così come sono al monitor, che li mostra con i
   *suoi* primari: i colori che vedi sul PC sono più saturi dello standard sRGB. Parsec cattura
   quei valori e il client li mostra come sRGB → sul Mac sembrano spenti/diversi.
3. **Range/matrice YUV** non controllati end-to-end (limited vs full range, BT.601 vs BT.709):
   causa tipica di neri "grigi" e tinte spostate.

## Cosa fa iScreen di diverso

| Problema | Soluzione iScreen |
|---|---|
| 4:2:0 su AMD | **4:4:4 impacchettato in 4:2:0**: un frame alto il doppio contiene tutta la crominanza; l'encoder hardware resta 4:2:0, il client ricostruisce il 4:4:4 nello shader |
| Gamut del monitor | L'host legge i primari del monitor catturato e li manda al client; il Mac tagga i pixel con quello spazio colore (ColorSync li converte per il suo schermo P3) |
| Range/matrice | Conversione RGB↔YCbCr fatta da **nostri shader** su entrambi i lati: BT.709 full range, nessuna ambiguità (il bitstream lo dichiara anche nella VUI) |
| Testo statico | Dopo che lo schermo smette di cambiare l'host ricodifica l'ultima immagine 8 volte (ogni 33 ms): la qualità converge a quasi lossless |
| Lag | Cattura DXGI Desktop Duplication → shader → AMF HEVC chiamato direttamente (≈3–5 ms per frame), senza B-frame, UDP con FEC Reed-Solomon e ritrasmissione selettiva (niente stalli TCP), cursore disegnato in locale sul Mac |

Misure sul tuo PC (pattern 2560×1440 con testo ClearType colorato, `selftest`):

| Regione | 4:2:0 | 4:4:4 iScreen |
|---|---|---|
| Testo colorato su chiaro | 23.9 dB | **49.5 dB** |
| Testo colorato su scuro | 24.3 dB | **45.5 dB** |
| Righe 1 px rosso/blu | 7.7 dB | **62.4 dB** |
| Scroll 6 px/frame (KB per frame) | 11 | 21 |

## Componenti

```
Windows (host, C# .NET 10)                         Mac (client, Swift)
┌──────────────────────────────┐                  ┌──────────────────────────────┐
│ DXGI Desktop Duplication     │                  │ Network.framework TCP        │
│   BGRA8 (valori "monitor")   │                  │ socket UDP + riassemblaggio  │
│ Shader D3D11                 │   UDP video      │   + NACK                     │
│   RGB→YCbCr BT.709 full      │ ───────────────▶ │ VideoToolbox HEVC (HW)       │
│   pack 4:4:4 → NV12 W×2H     │                  │ Metal: unpack 4:4:4,         │
│ AMF HEVC diretto (D3D11)     │                  │   YCbCr→RGB BT.709 full      │
│ UDP packetizer + cache NACK  │ ◀─────────────── │ CAMetalLayer.colorspace =    │
│ SendInput (scancode)         │   TCP controllo  │   primari monitor host       │
└──────────────────────────────┘                  └──────────────────────────────┘
```

## Formato del frame 4:4:4 impacchettato (modalità `444`)

Sorgente W×H (W, H pari). BT.709 full range, valori in [0,1]:

```
Y  = 0.2126 R + 0.7152 G + 0.0722 B
Cb = (B − Y) / 1.8556 + 0.5
Cr = (R − Y) / 1.5748 + 0.5
```

Frame codificato NV12 di larghezza `alignUp(W,16)` e altezza `F = alignUp(A + H, 16)`, con
`A = alignUp(H + 16, 64)` (per 2560×1440: A = 1472, F = 2912). Le righe tra H e A sono padding
(prima metà = ultima riga sopra, seconda metà = prima riga sotto) così deblocking/SAO del codec
non mescolano le due metà.

| Zona del frame NV12 | Contenuto |
|---|---|
| luma righe `[0, H)` | `Y[x, y]` |
| luma righe `[A, A + H)` | `Cr[x, y]` (piena risoluzione) |
| croma righe `[0, H/2)` — piano U / piano V | `Cb[2i, 2j]` / `Cb[2i+1, 2j]` |
| croma righe `[A/2, A/2 + H/2)` — piano U / piano V | `Cb[2i, 2j+1]` / `Cb[2i+1, 2j+1]` |

Così ogni componente si sposta con lo stesso motion vector dell'immagine (Y e Cr come luma,
le 4 fasi di Cb come croma a metà risoluzione) e lo scroll resta economico. Cr finisce nella luma
perché il contrasto rosso/verde è quello che l'occhio nota di più.

Ricostruzione del pixel (x, y): `Y = luma[x, y]`, `Cr = luma[x, A + y]`,
`riga = y pari ? y/2 : A/2 + (y−1)/2`, `Cb = (x pari ? pianoU : pianoV)[x/2, riga]`.

Modalità `420`: frame NV12 W×H standard (croma media 2×2, centrata), per il massimo risparmio.

## Protocollo di rete (v1, little-endian)

**TCP 52700** (controllo, `TCP_NODELAY`). Ogni messaggio: `u32 len` (byte che seguono) ·
`u8 type` · payload.

| type | dir | payload |
|---|---|---|
| 3 `Challenge` | H→C | `u16 version` · 16 byte nonce (inviato subito dopo la connessione) |
| 1 `Hello` | C→H | `u16 version` · `u16 udpPort` client · `u8 mode` (0=420, 1=444) · `u32 bitrateKbps` (0=default) · `u16 fps` (0=default) · 32 byte `HMAC-SHA256(key = PIN UTF-8, msg = nonce)` · opzionali: `u16 displayWidth` · `u16 displayHeight` (area a schermo intero del client, in pixel) · `u8 flags` (bit0 = adatta la risoluzione del PC, bit1 = manda l'audio, bit2 = appunti condivisi) |
| 4 `Reject` | H→C | `u8 reason` (1 = PIN errato) |
| 2 `SessionInfo` | H→C | `u16 version` · `u32 sessionId` · `u16 width` · `u16 height` · `u16 encWidth` · `u16 encHeight` · `u16 auxOffset` (A) · `u8 mode` · `u8 codec` (1=HEVC) · `u8 bitDepth` · `u16 fps` · 8×`f32` primari+bianco `rx ry gx gy bx by wx wy` · `f32 gamma` · `u16 udpPort` host. Rimandato se cambia la risoluzione. |
| 10 `MouseMove` | C→H | `f32 x` · `f32 y` (normalizzati 0..1 nel monitor catturato) |
| 11 `MouseButton` | C→H | `u8 button` (0 sx, 1 dx, 2 centro, 3 x1, 4 x2) · `u8 down` |
| 12 `MouseWheel` | C→H | `f32 dx` · `f32 dy` (in "notch", 1.0 = 120 unità Windows; dy>0 = su) |
| 13 `Key` | C→H | `u16 scancode` PC set 1 (bit 0x100 = prefisso E0) · `u8 down` |
| 20 `KeyframeRequest` | C→H | — |
| 30 `Ping` | C→H | `u64 clientTimeUs` (ogni secondo; l'host chiude dopo 15 s di silenzio) |
| 31 `Pong` | H→C | `u64 clientTimeUs` · `u64 hostTimeUs` |
| 60 `CursorShape` | H→C | `u16 w` · `u16 h` · `u16 hotX` · `u16 hotY` · RGBA8 non premoltiplicato w·h·4 |
| 61 `CursorState` | H→C | `u8 visible` · `f32 x` · `f32 y` (normalizzati) |
| 70 `Clipboard` | C↔H | `u8 format` (1 = testo UTF-8) · `u32 length` · byte. Inviato quando si copia su un lato (solo se bit2 dell'`Hello`); chi lo riceve lo mette negli appunti senza rimandarlo indietro |

**UDP 52701** (video). Il client, dopo `SessionInfo`, manda ogni 500 ms un `Register`
(`u8 0x80` · `u32 sessionId`) dalla sua porta UDP: l'host impara l'endpoint e risponde con un IDR.

Pacchetto video, protocollo **v2** (header 28 byte + uno shard da 1372 byte):

| offset | campo | tipo |
|---|---|---|
| 0 | `kind` | u8 = 1 |
| 1 | `flags` | u8 (bit0 keyframe) |
| 2 | `shardIndex` | u16: `< dataShards` = dati, altrimenti parità |
| 4 | `dataShards` | u16 (k) |
| 6 | `hostDelay` | u16, cattura→invio in unità di 100 µs |
| 8 | `frameId` | u32 (consecutivi) |
| 12 | `frameSize` | u32 (byte totali dell'access unit) |
| 16 | `captureTimeUs` | u64 (clock host, QPC) |
| 24 | `parityShards` | u16 |
| 26 | `fecPercent` | u8 (0 = niente FEC) |
| 27 | `fecBlock` | u8, shard di dati massimi per blocco (32) |

Payload = bitstream HEVC Annex-B (VPS/SPS/PPS su ogni IDR). Lo shard di dati `i` porta i byte
`[i·1372, …)` dell'access unit (l'ultimo non è riempito: il ricevente lo completa con zeri).

**FEC (Reed-Solomon).** Gli shard di dati sono divisi in `ceil(k/32)` blocchi **interlacciati**
(lo shard `i` va nel blocco `i % blocchi`, così una raffica di perdite consecutive si spalma).
Ogni blocco con `k_b` dati ha `max(1, ceil(k_b·fecPercent/100))` shard di parità, numerati dopo
tutti i dati, blocco per blocco. Codice sistematico su GF(2⁸) (polinomio 0x11D) con matrice di
Cauchy: parità `j` = Σᵢ Cⱼᵢ·Dᵢ con `Cⱼᵢ = 1/((k_b + j) xor i)`; qualunque `k_b` shard del blocco
ricostruiscono i dati. Ordine di invio: tutti i dati, poi la parità a giro sui blocchi. Il client
ricostruisce appena un blocco ha abbastanza shard e chiede (NACK) solo i dati dei blocchi che la
FEC non può recuperare; l'host ritrasmette solo dati. Default host: 20% di parità.
Misurato in loopback con il 5% di pacchetti persi: NACK da 195 a 6, latenza p95 da 11,6 a 6,5 ms,
banda +28% (i frame piccoli hanno comunque almeno uno shard di parità).

**Encoder.** AMF chiamato direttamente (interfaccia C di amfrt64.dll, nel driver AMD) invece che
tramite FFmpeg 7.1: `HevcQueryTimeout` fa sì che `QueryOutput` ritorni appena il frame è pronto
(FFmpeg 7.1 interroga a vuoto con pause da 1 ms), e `HevcForcePictureType = IDR` dà keyframe su
richiesta. Codifica per frame: 4:2:0 2560×1440 da 15,7 a **3,0 ms**, 4:4:4 2560×2912 da 17,7 a
**5,2 ms**. FFmpeg resta come alternativa (`--codec hevc_amf`) e per la decodifica nei test.

Pacing: i frame grandi partono a raffiche di 8 pacchetti distribuite a 250 Mbps, non a 1 Gbps
dal cavo del PC: altrimenti l'access point Wi-Fi butta via pacchetti e il Mac deve aspettare le
ritrasmissioni (freeze). Il buffer VBV dell'encoder è di 2 frame, così un singolo frame non esplode.

Pacchetto audio (stessa porta UDP, se il client ha messo bit1 nei flag dell'`Hello`): header 20 byte
`u8 kind = 2` · `u8 channels = 2` · `u16 frames` · `u32 seq` · `u32 sampleRate` · `u64 captureTimeUs`,
poi `frames` campioni PCM 16 bit stereo interleaved (5 ms per pacchetto). Catturato con WASAPI
loopback dal dispositivo di uscita predefinito di Windows (downmix di 5.1/7.1 a stereo). Mai
ritrasmesso: sul Mac un buffer di ~30 ms assorbe il jitter, oltre 120 ms di ritardo salta avanti.
Se cambia l'uscita predefinita (es. cuffie G733 ↔ altoparlanti) o il dispositivo sparisce, la
cattura riparte da sola sul nuovo dispositivo.

NACK client→host: `u8 0x81` · `u32 sessionId` · `u32 frameId` · `u16 count` · `count × u16
fragIndex`; `count = 0` = "rimanda tutto il frame". L'host tiene in cache gli ultimi 64 frame.
Il client consegna i frame in ordine; se un buco non si ripara entro 80 ms chiede un keyframe e
riparte dal successivo IDR completo.

**Niente tempeste di ritrasmissioni.** Il 28/10 su Wi-Fi congestionato il client richiedeva gli
stessi pacchetti ogni 4 ms e l'host rispondeva sempre: fino a 590 000 ritrasmissioni in 10 s,
600-760 Mbps, cioè freeze. Ora: il client richiede lo stesso frame al massimo 4 volte, a 20 ms
l'una dall'altra; l'host non rimanda lo stesso shard più di una volta ogni 30 ms (un frame intero
ogni 50 ms) e le ritrasmissioni hanno un tetto di 40 Mbps (token bucket). In loopback con 25% di
perdita e 40 ms di ritardo: ~2 Mbps di ritrasmissioni, lo stream continua a 40 fps.

**Errori della GPU e sospensione.** Se encoder o cattura falliscono (es. AMF non restituisce il
frame entro 500 ms), la sessione riparte da sola su un device D3D11 nuovo e il Mac riceve un nuovo
`SessionInfo` e un keyframe; dopo 3 fallimenti in un minuto l'host chiude la connessione invece di
lasciare un'immagine congelata. Prima un'eccezione del genere chiudeva tutto iScreen. Il driver AMD
impiega 5 s a chiudere un encoder (`Terminate`; anche via FFmpeg, `Drain` non aiuta): la chiusura
avviene su un thread a parte, così disconnessione e ripristino della risoluzione sono immediati.

**GPU persa per il processo.** Il 30/10, dopo un blocco del motore video della Radeon (lo stesso che
faceva diventare neri i launcher), AMF smetteva di restituire frame; circa 50 s dopo DXGI rimuoveva
il device ("device instance has been suspended") e da lì il processo vedeva la RX 7700 XT con
**0 monitor**, mentre un processo nuovo li vedeva tutti. Ogni collegamento falliva con un
incomprensibile `'0' cannot be greater than -1`. Ora: se DXGI non mostra monitor per 3 s, o il
riavvio dello stream dà `DEVICE_REMOVED/HUNG/RESET`, o lo stream fallisce 4 volte in un minuto,
`ControlServer` alza `GpuLost`; l'app nella barra si chiude e avvia una copia di sé con
`--gpu-restart <pid>` (che prende il mutex e termina la vecchia se resta appesa al driver), e mostra
"iScreen riavviato": dal Mac basta ricollegarsi. Se succede di nuovo entro 10 minuti dal riavvio
non riprova: il motore video è bloccato per tutto il sistema e serve riavviare il PC (avviso
"La GPU non risponde"). Quando mancano i monitor, il log elenca adattatori e uscite DXGI.
Il riavvio dello stream durante un blocco può restare fermo fino a ~1 minuto prima che il driver
rimuova il device.

Prima che il PC vada in sospensione (`PowerRegisterSuspendResumeNotification`) l'host chiude la
sessione e ripristina la risoluzione; dopo il risveglio basta ricollegarsi dal Mac.

## Adattamento della risoluzione

Se il client lo chiede (bit0 di `flags` nell'`Hello`), l'host sceglie tra le modalità supportate dal
monitor quella con le proporzioni più vicine allo schermo del client, la più grande che non lo
supera, e la applica con `ChangeDisplaySettingsEx(CDS_FULLSCREEN)`: temporanea, mai scritta nelle
impostazioni di Windows. Viene ripristinata alla disconnessione, al cambio di monitor e all'uscita;
se l'host si chiude male, la ripristina al successivo avvio (`ChangedDisplay` in `host.json`).
Desktop Duplication perde l'accesso, si ricrea alla nuova dimensione e l'host rimanda `SessionInfo`.
Sul Mac, se le proporzioni differiscono meno del 3% l'immagine riempie tutto lo schermo.

Esempio sul tuo PC per un MacBook con area 3420×2160 (16:10 circa): H27E6 e G27Q passano da
2560×1440 a **1920×1200**. Meno pixel di 2560×1440: il testo è un po' meno nitido, in cambio niente
bordi neri. Un monitor virtuale alla risoluzione esatta del Mac eviterebbe il compromesso (da fare).

## Modalità amministratore (servizio SYSTEM)

Due limiti di Windows per un'app normale (livello di integrità medio):

- **UIPI**: `SendInput` non raggiunge le finestre di processi con integrità più alta (app aperte come
  amministratore). La chiamata non fallisce: l'input sparisce.
- **Desktop sicuro**: richiesta UAC, schermata di blocco e Ctrl+Alt+Canc stanno sul desktop "Winlogon".
  Desktop Duplication (`DuplicateOutput1` → `E_ACCESSDENIED`) e `SendInput` funzionano solo da un thread
  agganciato a quel desktop, e solo SYSTEM può aprirlo.

La soluzione è quella di Parsec (`src/iScreen.Tray/SystemService.cs`, `AdminMode.cs`, `Sessions.cs`):

1. Il servizio `iScreen` (LocalSystem, sessione 0, `iScreen.exe --service`) non trasmette niente: ogni
   secondo controlla la sessione della console (`WTSGetActiveConsoleSessionId`). Se c'è un utente
   collegato (`WTSQueryUserToken`) e la barra delle applicazioni è pronta, avvia `iScreen.exe
   --from-service` come SYSTEM in quella sessione: duplica il proprio token, ne cambia la sessione
   (`SetTokenInformation(TokenSessionId)`) e usa `CreateProcessAsUser` su `winsta0\default`.
2. Quel processo è la solita app nel vassoio. `InputDesktop.Sync` (`OpenInputDesktop` +
   `SetThreadDesktop`) sposta il thread di cattura quando `DuplicateOutput1` va ricreato (accesso perso
   al cambio di desktop) e quello dell'input quando `SendInput` restituisce 0. Così segue la richiesta
   UAC e la schermata di blocco e poi torna su "Default".
3. Uscita con codice 0 (Esci dal menu) = chiusa dall'utente: il servizio non la riavvia fino al
   prossimo accesso o finché l'utente non riapre `iScreen.exe`, che chiede al servizio il controllo
   128. Altri codici = crash: riavvio con attesa crescente (2 s … 60 s). Se perde la GPU, iScreen
   chiede al servizio il controllo 129 e il servizio avvia quello nuovo con `--gpu-restart`. Per farla
   chiudere bene (risoluzione ripristinata) il servizio segnala l'evento `Global\iScreen.Quit.<pid>`,
   poi dopo 8 s termina il processo.
4. Sicurezza: i file stanno in `C:\Program Files\iScreen`, perché un eseguibile che gira come SYSTEM in
   una cartella scrivibile dall'utente sarebbe un'escalation di privilegi. Impostazioni e log stanno in
   `C:\ProgramData\iScreen`, dove l'installer lascia scrivere solo SYSTEM e Administrators. Un
   processo SYSTEM che scrive in `%APPDATA%` può essere dirottato con junction o link a sovrascrivere
   file di sistema. Il log, il Blocco note e il programma che riparte dopo la disinstallazione vengono
   aperti come utente (`WTSQueryUserToken`), mai come SYSTEM. Il DACL del servizio concede anche agli
   utenti interattivi l'avvio (RP) e i controlli 128/129 (CR).
5. Installazione (`--install-service`, elevata, dal menu): copia, regola firewall per le due porte
   solo su rete privata (le regole seguono il percorso dell'eseguibile), servizio con avvio automatico
   e riavvio se va in crash, rimozione della voce Run per utente, poi prova a collegarsi alla porta
   52700. Se dopo 40 s non risponde, annulla tutto e riavvia l'app per utente. Serve proprio una
   connessione: su questo PC (Windows build 26300) la tabella TCP (`netstat`, `GetActiveTcpListeners`)
   omette il listener di iScreen e mostra stati assurdi, e il 1/11 ha fatto annullare
   un'installazione riuscita.
   `--uninstall-service` fa il contrario e riparte dalla copia per utente
   (`Parameters\UserApp` nella chiave del servizio).

Non provato prima del primo uso reale: senza diritti di amministratore non si può eseguire niente come
SYSTEM. Se qualcosa non va lo dicono `C:\ProgramData\iScreen\service.log` e `host.log` (righe "now on
the Winlogon desktop" / "cannot move to").

## Limiti noti (da fare)

- Monitor ruotati (il VG249 verticale) non ancora gestiti; HDR non gestito (cattura 8 bit SDR).
- Stream non cifrato (solo autenticazione col PIN): va bene in LAN di casa, non su Internet.
- Desktop sicuro (UAC, schermata di blocco) solo in modalità amministratore; Ctrl+Alt+Canc non si
  può inviare (servirebbe `SendSAS`).
- Clipboard condivisa, microfono del Mac verso il PC: fase 2.
