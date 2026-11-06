# CLAUDE.md: gear-hub

**Gear Hub** (em português "Central de Periféricos"): um app de Windows para configurar os periféricos de marcas
diferentes num lugar só, no visual da `ilha`, aberto com o Windows e leve em segundo plano. Criado em 06/11/2026 a
pedido do Felipe ("um hub de configs para todos os periféricos e que seja leve", com engenharia reversa do Synapse, do
G HUB, do iCUE e do DSX), plano aprovado com uma mudança dele: **até ~200 MB em segundo plano está ótimo** (não precisa
de dois processos). **Repositório público** (`FelipeWASD83/gear-hub`, MIT), escolha dele. Código e comentários em
inglês; este arquivo, o CHANGELOG e os commits em português; README em inglês (repositório público, como a ilha).

## Como é feito

- C# .NET 10, WPF (janela) + WinForms só para o `NotifyIcon` da bandeja. Um processo. `App.Main` próprio (o
  `App.xaml` é `Page`, não `ApplicationDefinition`) trata a linha de comando antes do WPF subir.
- Visual copiado do `desktop-glow` (`Theme.xaml`, `Ui/Island.cs`, `Ui/Spring.cs`, `Ui/PillRadius.cs`, Quicksand nos
  títulos): cartão preto com borda acesa, pílula no topo que muda de tamanho com mola e mostra o que acabou de
  acontecer. As páginas dos aparelhos são montadas em código (`Ui/Page.cs`: seção, linha, chave, slider que só grava
  ao soltar, segmentos, cores). `Page.Work` roda a conversa com o aparelho fora da thread da janela.
- `Devices/Gear.cs`: cada aparelho tem `Key` (estável, os perfis guardam por ela), `BuildPage`, `Poll` (status da
  barra lateral a cada 20 s), `Capture`/`Apply` (perfis). `Devices/DeviceHub.cs` procura a cada 4 s comparando a lista
  de caminhos HID (barato) e só sonda de novo quando ela muda ou o DSX abre/fecha.
- Dados em `%USERPROFILE%\.gear-hub\` (`settings.json`, `profiles.json`, `gear-hub.log`, `update\`), não no AppData:
  processo aberto de dentro do app do Claude (MSIX) vê um AppData virtual (a mesma armadilha da ilha).
  `GEARHUB_HOME` troca a pasta.
- **Memória medida em 06/11/2026:** só na bandeja ~97 MB de working set (23 MB privados), CPU parada 0,02%; com a
  janela aberta ~220 MB; ao fechar a janela, coleta agressiva do GC + `SetProcessWorkingSetSize(-1,-1)` e o
  residente volta a ~21 MB (o commit privado fica ~180 MB, fora da RAM).

## Aparelhos e protocolos (o que foi lido e de onde)

- **Razer Huntsman V3 HE TKL** (`1532:02EA`, interface 3, feature report de 91 bytes): protocolo e tabela de comandos
  tirados do código do Synapse 4 em cache (`%LOCALAPPDATA%\Razer\RazerAppEngine\User Data\Default\Service Worker\CacheStorage`,
  módulos webpack com `COMMAND_*_HEADER = Uint8Array([tamanho, classe, id])`). Pacote: `[2]` transação 1..31, `[6]`
  tamanho, `[7]` classe, `[8]` id, argumentos a partir de `[9]`, `[89]` XOR de `[3..88]`; "get" = "set" | 0x80; status
  2 ok, 1 ocupado, 3 falhou, 5 sem suporte. Usados: Snap Tap `02 A7/27`, atuação múltipla `02 99/19`
  (`[0, slot, {tecla, H(2), L(2)} x até 15]`, palavras big-endian), rapid trigger múltiplo `02 9A/1A`
  (`[0, slot, {tecla, ligado, sobe(2), desce(2)} x até 13]`), polling de 8 kHz `00 C0/40` (`[perfil, código]`,
  o perfil vivo é o 1; com 0 o teclado aceita e não muda nada; `USB_HIGH_SPEED_POLLING_PERIOD_VALUE` do Synapse:
  0x01 = 8000 Hz ... 0x20 = 250 Hz, 0x40 = 125 Hz), brilho `0F 84/04` (`[1, 0, 0..255]`). **O tamanho do pacote tem que
  ser o dos dados de verdade** (2 + 5 por tecla), como o Synapse manda; com 80 o teclado responde status 3.
  Escala analógica: 0..65535 = 0..4 mm (`actuationInfo` do Synapse: `binaryRange 65535`; 1638 = 0,1 mm). Cada tecla
  guarda dois limites com 0,1 mm de diferença; **o mais fundo (L) é onde a tecla vale e o raso (H) onde solta**
  (inferido: lidos 1,1/1,2 e depois 1,6/1,7 mm). Ainda não conferido contra a tela do Synapse.
  Tudo vai para o **slot 1** (o vivo, onde o Synapse põe o perfil ativo): dura até o Synapse trocar de perfil.
  **Armadilha:** o Synapse usa a mesma interface e o comando dele sobrescreve a resposta; o `Exchange` reenvia depois
  de ~40 ms sem resposta, com `timeBeginPeriod(1)` durante a conversa. Antes disso (30 leituras de 15 ms) a página
  levava 7 s; agora ~0,1 s. Game mode (`00 88`) responde "sem suporte" neste teclado.
- **Logitech HID++ 2.0** (`Devices/Logitech/`): coleções vendor FF00/1 (curta 0x10, 7 bytes) e FF00/2 (longa 0x11, 20
  bytes); headset só FF43/0202 longa. Índice 0xFF com fio e headset, 1..6 pelo receptor. **Toda resposta chega a todos
  os handles abertos** (o G HUB usa swid 0xF): o nosso swid é 0xB e só pegamos o que volta com ele. A tabela de
  features vem do próprio aparelho (FeatureSet), então qualquer mouse ou headset Logitech ganha as seções que tiver.
  Medido: G502 X PLUS (C095) DPI 0x2201 idx 7, report rate 0x8060 idx 13, onboard 0x8100 idx 11, RGB 0x8071 idx 9,
  bateria 0x1004; G733 (0AFE) luz 0x8070 idx 4 (2 zonas), EQ 0x8310 idx 6 (10 bandas, ±12 dB), sidetone 0x8300 idx 7,
  bateria por tensão 0x1F20 idx 8. O G733 responde `ffffffff` como unit id: a chave dele cai no product id.
  Bateria por tensão usa a curva do próprio G HUB (`C:\ProgramData\LGHUB\depots\...\battery_<pid>.xml`) quando
  existe (3,625 V = 5%, igual ao G HUB). Escritas (formato do Solaar/libratbag), **conferidas no aparelho em 06/11/2026**: SetSensorDpi `fn3 [0, hi, lo]`,
  SetReportRate `fn2 [ms]`, efeitos `0x8071 fn1` / `0x8070 fn3` `[zona, índice do efeito, 10 parâmetros, 0x01]`
  (o G733 ficou vermelho), sidetone `fn1 [0..100]`. **EQ do G733: o fone recusa toda escrita** (erro 2 com só os
  ganhos e com os prefixos 00 a 03), e um escutador passivo mostrou que o G HUB não manda nada ao fone quando o EQ
  muda: o EQ do G HUB é feito no PC (driver de áudio). A página só mostra os controles de EQ se regravar os ganhos
  atuais for aceito. SetMode onboard `fn1 [1|2]` continua sem teste. **Luz do G502:** com o G HUB em `control:
  LAMPARRAY` a Iluminação Dinâmica do Windows regrava a cor o tempo todo e a nossa não aparece (o G733, em
  `FIRMWARE`, aceita); a página avisa quando o G HUB diz LAMPARRAY. **Sem fio:** o G502 dormindo responde "aparelho
  desconhecido" atrás do receptor e acordar não muda caminho HID; o hub pinga os slots do receptor vazio a cada 20 s
  (`ReceiverHasDevice`) e só então faz a busca completa. Pelo receptor ele aparece no índice 01 (unit id igual ao do cabo).
- **G HUB aberto** (`GHubClient.cs`): `ws://localhost:9010`, subprotocolo `json`, `Origin: file://`; o agente manda
  OPTIONS "/" ao conectar; `pid` do `/devices/list` é o product id em decimal (o id do G HUB é procurado pelo id USB
  atual e pelos model ids do aparelho). Sidetone vai por `SET /audio/<id>/sidetone` (testado: chegou ao fone), memória
  por `SET /onboard_profiles/<id>/onboard_mode` (sem teste). **DPI vai sempre direto pelo HID++** (testado: segura com
  o G HUB aberto até ele trocar de perfil). **Não fazer de novo:** editar o cartão MOUSE_SETTINGS (`SET /assignment`)
  só mudou a tabela do G HUB, não o mouse, e trocar "o nível igual ao antigo" pelo novo embaralhou a ordem da tabela de
  DPI dele no primeiro teste (desfeito na hora, conferido: 800, 1600, 2400, 3200, 25600, ativo 2400). `SET
  /audio/<id>/onboard` recusa o EQ do G733 ("Failed to set onboard Equalizer").
- **RAM Corsair** (`Devices/Corsair/`): o **CORSAIR Device Control Service** (LocalSystem, o "CORSAIR RGB DRAM
  Service") escreve no SMBus com o driver da própria Corsair; o iCUE.exe é só cliente dele. Protocolo lido estático da
  `CorsairDeviceControlServiceAPI.dll` e do serviço: pipes `\\.\pipe\CorsairUtilityEngine\{A78E0942-12BB-4E77-B359-3B14722A80EA}_in/_out`,
  quadro `uint32 LE + JSON + \0`; handshake `":pid:<pid>"` devolve o caminho de um trio dedicado (`_in`, `_out`,
  `_callback`, este drenado por uma thread); `CDCSConnect {"params":{"clientProtocolVersion":1}}`,
  `CDCSGetDeviceInfos`, `CDCSGetGeometryInfo`, `CDCSSetLightingPriority {priority: 2}` (o iCUE fica em 1),
  `CDCSSetLedColors {"colors":[{"id","color":"ffrrggbb"}]}`. Fechar a sessão devolve a luz ao efeito gravado no
  pente. O serviço é Manual mas Authenticated Users podem iniciar (`StartServiceW`). **Nunca chamar** `CDCSUpdateFw`,
  `CDCSSetPerfProfile`, `CDCSClearPerfProfile` (firmware e XMP/tensões). Conectar avisa o iCUE (que, com
  "CORSAIR RGB DRAM Service Access" em NotifyMe, pergunta ao Felipe). **Testado com ele olhando em 06/11/2026**: `ffff0000`
  deixou os pentes vermelhos (a ordem é `aarrggbb`), 1 aparelho com 20 LEDs (10 por pente). Com o iCUE fechado não
  houve pergunta; o serviço, parado, foi iniciado pelo app, instalou o driver LLA dele mesmo e parou sozinho quando a
  sessão acabou (log em `C:\ProgramData\Corsair\Logs\CorsairDeviceControlService`, hora em UTC). A escolha fica em
  `settings.json` (`ramColor`, `modo:#hex`) e é retomada ao abrir. Respiração e arco-íris são desenhados aqui a 25 qps.
- **DSX** (`Controllers/Dsx.cs`): o mesmo servidor de mods que a ilha usa (UDP 127.0.0.1:6969 ou a porta do
  `app_settings.json`), `{"instructions":[{"type","parameters":[índice, ...]}]}`. Só aparece com o `DSX.exe` aberto.
  **Não testado ao vivo** (o DSX estava fechado). A licença/Steam do DSX não é tocada.
- **DualSense direto** (`Controllers/DualSense.cs`), só com o DSX fechado: relatório 0x02 USB (48 bytes) ou 0x31
  Bluetooth (78 bytes com CRC32 de 0xA2 + 74 bytes), layout do `hid-playstation.c` do Linux. **Não testado** (nenhum
  controle conectado em 06/11/2026); o autoteste confere o layout e o CRC.
- **Webcam** (`Webcam/Webcam.cs`): `IAMCameraControl` e `IAMVideoProcAmp` do DirectShow no filtro de captura, sem
  abrir captura (a luz da câmera não acende). Câmeras sem nenhum controle (OBS, Meta Quest) ficam fora da lista.
  Leitura conferida na C920.
- **Iluminação Dinâmica** e **controles do Windows** (`Lighting/Lights.cs`): LampArray (o Windows só dá a luz ao app
  da frente ou ao controlador de fundo) e `RawGameController` para bateria. **Armadilha:** `FindAsync` com
  `ConfigureAwait(false)`; esperar o resultado na thread da janela travava (a página de Perfis não abria).

## Decisões que não se desfazem sem motivo

- Nada de kernel, driver novo, modo de teste ou ring 0 (regra dele). A RAM só funciona pelo serviço da Corsair por isso.
- Nada de firmware, DFU, perfis de memória (timings, tensão) nem escrita de setor onboard da Logitech: fora do código
  de propósito.
- Os nomes de processo vêm do Toolhelp, nunca abrindo handle no processo do jogo (anti-cheat), como a ilha.
- O card de Snap Tap da ilha continua lá; os dois falam com o teclado e se toleram (transações e reenvio).
- O Run (`HKCU\...\Run`, valor `GearHub`, `"GearHub.exe" --tray`) só é escrito pela cópia instalada em
  `%LOCALAPPDATA%\Programs\GearHub` (`Startup.Sync`).

## Testado e não testado (06/11/2026)

- Testado: autoteste (`--self-test`, 19 itens: pacotes da Razer contra valores lidos, efeitos e bateria da Logitech,
  layout e CRC do DualSense, pacote do DSX, comparação de versão); sondagem só de leitura (`--probe`) em todos os
  aparelhos presentes; fotos de todas as páginas (`--screenshot`); bandeja, `--quit`, memória e CPU medidas; segunda
  cópia com `--tray` sai calada (no logon a tarefa do Desktop Glow e o Run abrem duas).
- **Escritas testadas uma a uma com ele (`--write-test <passo>`: escreve, lê de volta, devolve):** Razer atuação,
  rapid trigger, polling e brilho; G502 DPI e taxa; G733 sidetone e luz (vista vermelha); RAM (vista vermelha); zoom da
  webcam. Luz do G502 aceita mas não aparece (Iluminação Dinâmica, acima).
- Uma queda do .NET (0xC0000005 no coreclr) apareceu uma vez na foto da página da Razer antes da correção do
  deadlock da Iluminação Dinâmica e não voltou em dez execuções. Se voltar, olhar o `HidStream` (E/S sobreposta).
- **Não testado:** DSX e DualSense (nenhum controle ligado), memória onboard da Logitech, Iluminação Dinâmica pela
  página, perfis aplicando de verdade num jogo, atualizador (não há release), instalação pelo `instalar.ps1`.

## Comandos

- Compilar (com o teste do SAC): `powershell -ExecutionPolicy Bypass -File .\ferramentas\compilar.ps1`
  (`-Publicar <pasta>` publica com ReadyToRun). Saída em `src\GearHub\bin\Release\net10.0-windows10.0.26100.0\win-x64`.
- `GearHub.exe --tray` | `--quit` | `--self-test <arquivo>` | `--probe <arquivo>` | `--screenshot <png> [chave da página]`
  (desenha fora da tela, sem roubar foco). `GEARHUB_TRACE=1` registra cada comando da Razer no log.
- Instalar no PC: `ferramentas\instalar.ps1` num terminal comum (nunca pelo shell do Claude: AppData virtual).
- Release: `ferramentas\publicar-versao.ps1` (decisão dele); `-SoGerar <pasta>` monta `GearHub-X.Y.Z.zip` do último
  commit, arquivos na raiz, o que o atualizador e o Desktop Glow usam. Versão no csproj, no `versao.json` e no topo
  do CHANGELOG, as três juntas.
- Smart App Control: sempre compilar pelo `compilar.ps1` (carimbo `Stamp.g.cs` + `SourceRevisionId`, igual à ilha).
