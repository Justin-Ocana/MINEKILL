# Contrato del enlace ULTRAKILL ↔ Minecraft

Versión del contrato: **0.1**. El layout de bytes es el del protocolo SkyCraft **v11**.

## Procesos y propiedad

| Cosa | Dueño | Notas |
|---|---|---|
| Mapeo de memoria `Local\Minekill_v1` | **ULTRAKILL** lo crea; Minecraft lo abre | El nombre es distinto del de SkyCraft y Killcraft para que no colisionen si se ejecutan a la vez. Para usar el mod de SkyCraft hay que lanzarlo con `-Dskycraft.link=Local\Minekill_v1`. |
| Cuerpo del jugador (posición, velocidad, colisión) | **ULTRAKILL** (modo de propiedad dividida) | Minecraft sigue a V1 a través de `HostState`. |
| Inventario, ítems, bloques, mobs | **Minecraft** | Fases 5 y siguientes. |
| Salud | Por decidir en la fase 7 | Opción preferida: la de ULTRAKILL, con la armadura de Minecraft como reducción del daño. |

## Unidades y ejes

- 1 bloque = `UnitsPerBlock` unidades de Unity. Por defecto 2.
- Minecraft `x = ux/U + originX`, `y = uy/U`, `z = -uz/U + originZ`.
- Yaw de MC = yaw de Unity + 180, envuelto a (-180, 180]. Pitch de MC = −pitch de Unity (en Minecraft, positivo es mirar abajo).
- Origen por nivel: hash FNV-1a del nombre de escena. Áreas separadas 2048 bloques, siempre dentro de ±60000.
- Los pies se calculan con `playerCollider.bounds.min.y`, es decir, se miden y no se suponen.

## Canales

| Región | Dirección | Política | Si llega tarde o roto |
|---|---|---|---|
| Header @0x0 (magic, version, pids, heartbeats) | ambos | escritura directa. ULTRAKILL publica `magic` lo último | latido con más de 3000 ms de antigüedad = el par se considera caído |
| HostState @0x100 (0x40 B) | ULTRAKILL → MC | seqlock (seq impar mientras se escribe) | lectura rota = se conserva el valor anterior |
| GuestState @0x200 (0xC8 B) | MC → ULTRAKILL | seqlock | igual |
| Anillos de colisión, render y eventos, overlay | — | reservados con el layout v11, todavía sin usar | — |

## Ciclo de vida

1. ULTRAKILL crea el mapeo al arrancar. Si ya existía porque Minecraft lo tenía abierto, pone a cero lo que le pertenece antes de publicar `magic`.
2. El invitado abre el mapeo solo si `magic` y `version` coinciden. Si no, rechaza la conexión y no lee nada.
3. `teleportSeq` aumenta al cambiar de escena y cada vez que el invitado (re)conecta. El invitado lo confirma con `teleportAck`.
4. `epoch` aumenta al cambiar de escena: el invitado debe descartar la colisión que tenga.
5. Si el latido del par caduca, cada lado sigue funcionando solo. ULTRAKILL nunca se bloquea esperando a Minecraft.
