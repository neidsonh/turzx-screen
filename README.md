# TURZX Screen

App leve (um .exe, sem instalar nada) para a tela USB **Turing / TURZX 3.5"**, que substitui o app oficial (UsbMonitor).

> Projeto independente da comunidade, sem vínculo com os fabricantes da tela. Use por sua conta e risco.

## Como usar
Requisitos: Windows 10 ou 11 com .NET Framework 4.x, que já vem no sistema, e a tela 3.5" rev. A ligada no USB.

1. Baixe o código e execute `build.bat`. Ele gera `TurzxScreen.exe` sem precisar instalar compilador.
2. Feche o app oficial da tela, se estiver aberto, porque só um programa pode usar a porta.
3. Abra `TurzxScreen.exe`.

As configurações ficam ao lado do `.exe`, em `turzx.ini`, `layout.json` e `api-token.txt`. Esses arquivos podem ter dados pessoais, como o link da sua agenda, e não devem ser compartilhados.

Recursos opcionais: temperatura da CPU e FPS detalhado dependem do LibreHardwareMonitor, e **Cores do RGB** depende do SignalRGB.

## Hardware identificado
- Turing Smart Screen 3.5" **rev. A**, USB `1A86:5722`, serial `USB35INCHIPSV2`, aparece como porta COM (ex.: COM3). Não responde ao comando HELLO (modelo original).
- Resolução 320×480 (retrato) / 480×320 (paisagem), cor RGB565
- Velocidade medida: **~170 KB/s**, ou seja, ~1,8 s por tela cheia. É limite do hardware (testado com e sem controle de fluxo).
  Por isso o app envia só os retângulos que mudaram, e o gráfico de histórico é do tipo "varredura".

## Recursos
| Área | O que faz |
|---|---|
| **Painel**, 6 temas | Cartões · Gráficos (histórico de 1 amostra/s) · Relógio grande · Medidores · Clima e cotações · Tocando agora |
| **Cores do RGB** | na aba **Painel**, usa a cor do efeito atual do SignalRGB como destaque e deriva fundo e texto do mesmo tom, conferindo a cada 2 s. Só lê os ajustes salvos no registro do usuário, sem alterar nada. Efeitos sem cor fixa mantêm as cores atuais. Desligar a opção devolve as cores anteriores |
| **Cartões** | escolha e ordem dos cartões: CPU, RAM, GPU, temperatura da CPU e da GPU, disco, download, upload. Especificações da máquina ao lado da hora e/ou dentro dos cartões. A ordem vale também para Gráficos e Medidores |
| **No ar** | moldura vermelha e etiqueta quando microfone ou câmera estão em uso, com o nome do app. Valida as entradas do registro do Windows, que costumam ficar velhas. Em chamada, a regra de PC parado não apaga a tela |
| **Imagem / GIF** | imagem ou GIF animado, preencher ou ajustar |
| **Espelhar tela** | um monitor inteiro ou uma lupa 1:1 em volta do mouse |
| **Texto** | mensagem livre centralizada |
| **Notificações** | lê a central de notificações do Windows (Teams, WhatsApp, Outlook...) e mostra um cartão no estilo do Windows 11 por cima de tudo. Opções: tempo, privacidade, apps ignorados |
| **Música** | título, artista, álbum, capa e progresso de qualquer player no controle de mídia do Windows (Spotify, navegador, VLC...). Aviso de faixa nova nos outros temas |
| **Clima / cotações** | Open-Meteo e AwesomeAPI, gratuitas e sem chave. Clima a cada 10 min, cotações a cada 2 min |
| **Automático** | PC parado → relógio, imagem ou tela desligada · música → tema Tocando agora · rodízio de temas · brilho noturno |
| Geral | brilho, orientação, liga/desliga, bandeja, iniciar com o Windows, reconexão automática, oferece fechar o app TURZX se ele segurar a porta |

Prioridade da troca automática: PC parado > música > rodízio > modo escolhido. As notificações aparecem por cima de tudo.

## Tema Gaming
Mostra nome do jogo, FPS, 1% low, frametime, histórico, uso e temperatura de GPU/CPU, RAM e clock da GPU. Com a troca automática ligada, entra sozinho quando um jogo está em primeiro plano.

- **Detecção:** executável em pastas de jogos (Steam, Epic, GOG, Xbox, Riot, Battle.net, Ubisoft) ou janela em tela cheia com pelo menos 15 FPS, fora de uma lista de apps comuns.
- **FPS:** eventos ETW do DXGI (`Microsoft-Windows-DXGI`), sem injetar nada no jogo. Exige o grupo *Performance Log Users* ou administrador. A captura só fica ligada enquanto há um candidato a jogo.
- **Reserva:** RivaTuner Statistics Server, se estiver aberto, para jogos que não apresentam por DXGI (alguns Vulkan/OpenGL).

## Próxima reunião
Lê um ou mais calendários ICS (separe por `;`): link publicado do Outlook ou Google, ou arquivo `.ics`, atualizando a cada 5 min. Entende fusos, recorrência (`RRULE` diária, semanal, mensal e anual), exceções, ocorrências alteradas e cancelamentos; eventos de dia inteiro são ignorados.

Mostra uma faixa de contagem regressiva, avisos 5 e 1 minuto antes, o tema **Agenda** e o cartão **Próxima reunião**. O link ICS é secreto: fica em `turzx.ini`, então não compartilhe esse arquivo.

## Porta-retrato
Na aba **Imagem**, escolha uma pasta: as fotos (JPG, PNG, BMP, GIF, TIFF) entram em rodízio, com subpastas e ordem aleatória opcionais, relógio por cima e a data em que a foto foi tirada. A rotação gravada pela câmera é respeitada. Sem "Preencher a tela", fotos em outra proporção aparecem inteiras sobre um fundo desfocado. Em **Auto**, a ação "Imagem / fotos" transforma a tela em porta-retrato quando o PC fica parado.

## Editor visual
Em **Painel → Tema**, escolha qualquer tema e clique em **Editar**. Em um tema pronto, o app cria uma cópia editável (ex.: "Cartões personalizado") e o original continua igual; se fechar sem mudar nada, a cópia é descartada. Em um tema seu, a edição é direta. O editor também renomeia e exclui temas, e **Restaurar padrão** volta ao tema de origem. Os temas criados aparecem na lista de temas e no rodízio. No widget de texto, `{cpu}`, `{ram}`, `{gpu}`, `{cputemp}`, `{down}` e similares viram valores ao vivo. Arraste widgets, redimensione pelos cantos e ajuste as propriedades; a telinha mostra o resultado ao vivo enquanto o editor estiver aberto. Widgets: relógio, data, texto, métrica (qualquer cartão ou FPS), medidor, gráfico, clima, previsão, cotações, música, player, agenda, painel de FPS, **máquina** (resumo do hardware ou um dado só: processador, placa de vídeo, memória, placa-mãe, sistema ou disco), imagem e painel. Retrato e paisagem têm layouts separados, salvos em `layout.json`.

## API local
Escuta só em `127.0.0.1:48765`. Toda chamada precisa do cabeçalho `X-Turzx-Token` com o conteúdo de `api-token.txt` (gerado na primeira execução; não compartilhe). Chamadas vindas de páginas web são recusadas.

| Rota | Corpo | Efeito |
|---|---|---|
| `GET /status` | | estado da tela e do quadro de status |
| `POST /notify` | `{"app","title","body","seconds"}` | cartão de aviso |
| `POST /state` | `{"key","label","state","text","detail","ttl"}` | faixa no rodapé; `state`: `working`, `waiting`, `done`, `error` ou `info` |
| `POST /clear` | `{"key"}` | remove um status |
| `POST /image?seconds=10` | bytes da imagem (até 8 MB) | mostra a imagem por um tempo |

```powershell
$t = (Get-Content .\api-token.txt -Raw).Trim()
Invoke-RestMethod http://127.0.0.1:48765/notify -Method Post -Headers @{ 'X-Turzx-Token' = $t } -ContentType 'application/json' -Body '{"title":"Build pronto","body":"Tudo certo"}'
```

### Status do Claude Code
Os hooks em `~/.claude/settings.json` chamam `TurzxScreen.exe /claude-hook` em segundo plano. Só são enviados o estado, o tipo de ferramenta e o nome da pasta; prompts e conteúdo de arquivos nunca vão para a tela. Se mover o `.exe`, atualize o caminho nesses hooks.

## Temperatura da CPU
O Windows não expõe a temperatura real da CPU sem driver e administrador. A zona térmica ACPI, que é lida sem admin, marca a placa-mãe (~28 °C) e não serve.
O app lê a temperatura do **LibreHardwareMonitor** quando ele estiver aberto (sensor "CPU Package"):
- v0.9.x (atual): pelo servidor web local `http://127.0.0.1:8085/data.json` (Options → Remote Web Server). Essa versão não publica mais WMI.
- versões antigas / OpenHardwareMonitor: via WMI (`root\LibreHardwareMonitor`).

Configuração sugerida: servidor web só em `127.0.0.1:8085`, iniciar minimizado e abrir como administrador no logon. O LibreHardwareMonitor é um programa à parte e não acompanha este projeto.
A GPU NVIDIA vem do `nvidia-smi`, sem precisar de nada extra.

## Dá para usar como monitor comum?
Não de verdade. A tela é um dispositivo serial, não uma placa de vídeo: o Windows não a reconhece como monitor e não existe driver de vídeo para ela.
O máximo é o modo **Espelhar**, que funciona para conteúdo que muda pouco.

## Arquivos
- `src/TurzxScreen.cs`: protocolo da tela, interface, renderização dos temas e modos
- `src/Notifications.cs`: leitor da central de notificações (WinRT `UserNotificationListener`, por polling)
- `src/Media.cs`: "tocando agora" (WinRT `GlobalSystemMediaTransportControlsSessionManager`)
- `src/Widgets.cs`: clima, cotações e ícones de clima
- `src/Hardware.cs`: especificações (CPU, GPU, RAM, placa-mãe, Windows) e temperatura da CPU
- `src/Extras.cs`: log de erros, sensores do LibreHardwareMonitor e detecção de microfone/câmera
- `src/DirtyRects.cs`: encontra as áreas alteradas de cada quadro
- `src/NetStats.cs`: tráfego das placas de rede físicas
- `src/RgbSync.cs`: leitura da cor do efeito atual do SignalRGB
- `turzx.ini`: configurações, salvas logo após cada ajuste (gravação atômica)
- `turzx.log`: erros do app, sem conteúdo de notificações (limite de 256 KB, com uma cópia `.1`)
- `tools/snap.ps1`: captura a janela do app (desenvolvimento). `TurzxScreen.exe /tab N` abre já na aba N.

## Compilar
`build.bat` usa o compilador C# 5 que já vem com o Windows (.NET Framework 4) e os `.winmd` de `System32\WinMetadata`.
As operações assíncronas WinRT são esperadas manualmente (`WinRt.Wait`), sem o `Windows.winmd` do SDK.

## Protocolo (rev. A)
Comandos de 6 bytes com coordenadas de 10 bits empacotadas + código: 101 reset, 102 clear, 108 desligar, 109 ligar,
110 brilho (0 = máximo, 255 = mínimo), 121 orientação (pacote de 16 bytes), 197 bitmap (seguido dos pixels RGB565 little-endian).
Referência: [turing-smart-screen-python](https://github.com/mathoudebine/turing-smart-screen-python), licenciado sob GPL-3.0.

## Licença
Distribuído sob a [GPL-3.0](LICENSE). Copyright (C) 2026 colaboradores do TURZX Screen.
