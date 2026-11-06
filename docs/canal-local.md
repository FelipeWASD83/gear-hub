# Canal local do Gear Hub (para a ilha e outros programas)

O Gear Hub (1.1.0+) abre um named pipe só para o usuário atual:

    \\.\pipe\GearHub.Events

Várias conexões ao mesmo tempo. Texto UTF-8, **uma mensagem JSON por linha** (`\n`), nos dois sentidos. Campo
desconhecido se ignora (o formato pode crescer). Sem o Gear Hub aberto o pipe não existe: o cliente tenta de novo a cada
5 s.

## Do Gear Hub para o cliente

Ao conectar, sempre, o estado inteiro:

```json
{"type":"hello","version":"1.1.0","profile":"cs2","profileDisplay":"cs2",
 "profiles":[{"name":"default","display":"Padrão","apps":[],"saved":true},{"name":"cs2","display":"cs2","apps":["cs2.exe"],"saved":true}],
 "devices":[{"key":"razer-02ea","name":"Razer Huntsman V3 HE TKL","kind":"keyboard","status":"Snap Tap ligado","battery":null}],
 "snapTap":true}
```

`kind`: `keyboard`, `mouse`, `headset`, `memory`, `controller`, `camera`, `light`. `battery`: 0..100 ou `null`.
`saved`: o perfil tem algo guardado (perfil vazio não se aplica).

Eventos (chegam quando acontecem, a quem estiver conectado):

| type | campos | quando |
|---|---|---|
| `profile` | `name`, `display`, `reason` (`app`, `manual`, `tray`, `hotkey`, `ilha`), `app` (o .exe, quando `reason` = `app`) | um perfil foi aplicado |
| `profiles` | `profiles` (igual ao hello) | perfis criados, apagados ou renomeados |
| `devices` | `devices` (igual ao hello) | aparelho entrou, saiu ou mudou de status |
| `device` | `key`, `name`, `kind`, `connected` (bool) | um aparelho conectou ou desconectou (depois do hello, não na lista inicial) |
| `battery` | `key`, `name`, `kind`, `percent`, `charging` | bateria cruzou 20%, 10% ou 5% descendo (uma vez por nível até carregar) |
| `snaptap` | `on` (bool) | o Snap Tap mudou pelo Gear Hub (página, perfil, atalho, pedido) |
| `dpi` | `key`, `name`, `value` | o DPI mudou pelo Gear Hub |
| `effect` | `name` (texto para mostrar), `targets` (lista de nomes) | um efeito de luz começou |

## Do cliente para o Gear Hub

| cmd | campos | o que faz |
|---|---|---|
| `state` | | responde com um `hello` |
| `profile` | `name` | aplica o perfil (responde com o evento `profile`, `reason` = `ilha`) |
| `snaptap` | `on` (bool) | liga ou desliga o Snap Tap |
| `show` | `page` (opcional: chave do aparelho, `profiles`, `settings`, `home`) | abre a janela do Gear Hub |

Pedido inválido é ignorado. Não há resposta de "ok" além dos eventos.
