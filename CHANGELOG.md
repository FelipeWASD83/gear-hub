# Changelog

## 1.3.0 (06/11/2026)

**Novo**

- Na página do teclado, a seção "Perfis guardados no teclado" mostra o que vale agora e as cópias que o Synapse guarda dentro do teclado (atuação das letras e das outras teclas, rapid trigger e Snap Tap de cada uma). "Usar este" passa uma delas para o que vale, o mesmo que o Synapse faz ao trocar de perfil. Serve para quando o teclado fica com um acionamento diferente do que você configurou.
- O registro do Gear Hub anota cada vez que ele grava atuação ou rapid trigger no teclado, para dar para saber de onde veio uma mudança.

## 1.2.0 (06/11/2026)

**Novo**

- Ao sair, o Gear Hub devolve cada aparelho ao que os apps das marcas tinham deixado: atuação, rapid trigger, Snap Tap, taxa, brilho e luz do teclado; DPI, taxa, memória e sidetone da Logitech; os ajustes da webcam; o perfil do DSX. As luzes animadas param e a memória volta ao efeito dela. Vale ao sair pela bandeja, pelo --quit e quando o Windows desliga.
- Se o Synapse trocar de perfil enquanto o Gear Hub está aberto, ele mesmo já regravou o teclado, e o Gear Hub não mexe mais.
- Em Ajustes: a chave "Devolver ao sair", a lista do que o Gear Hub mudou e o botão "Devolver agora".

## 1.1.0 (06/11/2026)

**Novo**

- Visão geral: a primeira página mostra todos os aparelhos em cartões, com bateria, e os perfis a um clique.
- Desenho do switch na página do teclado: a tecla de lado com a régua de 0 a 4 mm, onde ela vale, onde solta e a faixa do rapid trigger, mexendo junto com os controles. Com "Ao vivo", o desenho e as barras de W, A, S e D mostram quanto cada tecla está descendo.
- Efeitos de luz animados para a memória e para a luz dos aparelhos Logitech: degradê, troca de cor, arco-íris, onda de duas cores, respiração que troca de cor (sem apagar), cometa, estrelas, fogo e música (a luz acompanha o som do PC). Velocidade e brilho em cada um, e uma prévia na tela.
- Luz do teclado Razer pelos efeitos do próprio teclado: fixo, respiração, espectro, onda, reativo, ondulação, estrelas, fogo e roda de cores.
- Página Luzes: um efeito para todos os aparelhos de uma vez, com o teclado junto no efeito mais parecido que ele tem.
- Imagem ao vivo da webcam na página dela, para ver cada ajuste na hora.
- Integração com a ilha (4.3.0 em diante): perfil trocado, Snap Tap, DPI, aparelho que conectou ou saiu, bateria baixa e efeito de luz aparecem na pílula, e o menu da ilha troca de perfil.
- Atalhos de teclado em qualquer lugar: próximo perfil, ligar e desligar o Snap Tap, abrir a janela.
- Medidor da taxa real do mouse.
- Snap Tap no menu da bandeja.

**Mudou**

- A luz da memória passa pelo mesmo motor dos efeitos; a escolha da 1.0.0 continua valendo.

## 1.0.0 (06/11/2026)

**Novo**

- Central de Periféricos: um app só para configurar teclado, mouse, headset, memória, controles e webcam de marcas diferentes, no visual da ilha.
- Razer Huntsman V3 HE TKL direto pelo USB, sem precisar do Synapse: Snap Tap (ligar, pares e modo), ponto de atuação e rapid trigger por tecla, taxa de resposta até 8 kHz e brilho.
- Logitech por HID++ (qualquer mouse ou headset Logitech que fale o protocolo): DPI com atalhos, taxa de resposta, memória do mouse, luz por zona, sidetone, equalizador (nos aparelhos que guardam EQ; o G733 não guarda) e bateria, também pelo receptor sem fio.
- Memória RAM Corsair pelo serviço da própria Corsair, sem driver novo e sem precisar da janela do iCUE: cor fixa, respiração, arco-íris ou o efeito gravado no pente.
- Controles: DSX (gatilhos adaptáveis, barra de luz, luz do microfone) e, com o DSX fechado, DualSense e DualSense Edge direto pelo USB ou Bluetooth; bateria dos controles que o Windows enxerga.
- Webcam: zoom, foco, exposição, balanço de branco, brilho e o resto dos ajustes da própria câmera, com automático onde ela tem.
- Iluminação Dinâmica do Windows para aparelhos de qualquer marca que a aceitem.
- Perfis: guardam como cada aparelho está e entram sozinhos quando um jogo vem para a frente (só pelo nome do .exe).
- Fica na bandeja, abre com o Windows e procura atualização no GitHub.
