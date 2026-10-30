# MODLOG: decisiones y evidencia

Regla: una afirmación sin evidencia registrada aquí cuenta como **no verificada**.

## 2026-10-08

### Entorno (verificado inspeccionando archivos)
- ULTRAKILL en la carpeta del juego del usuario (`ULTRAKILL_DIR`).
  - Unity **2022.3.29f1** (versión de `UnityPlayer.dll`), **Mono** (`MonoBleedingEdge` y `Managed\Assembly-CSharp.dll`).
  - **Pipeline integrado**: no hay ensamblados `Unity.RenderPipelines*`.
  - Acuerdo con el usuario: Claude no ejecuta `ULTRAKILL.exe`; el usuario lo arranca y comparte el log.
- Herramientas portables en `.tools/`: .NET SDK 8.0.425, Temurin JDK 25.0.4.1, ilspycmd 9.1 (instalado pero sin usar, porque descompilar requiere autorización del usuario) y el paquete BepInEx 5.4.23.5 x64 (SHA256 `82F98785…32C4`).
- Copia de seguridad en `backups/ultrakill-pre-bepinex-20261008/`: manifiesto SHA256 de 684 archivos, `Saves`, `Preferences` y los archivos raíz.
- No hay colisiones: ningún archivo de BepInEx existe ya en la carpeta del juego.
- **Copiar BepInEx a la carpeta del juego fue bloqueado por el clasificador de permisos.** Se ha preparado `tools/install.ps1` para que lo ejecute el usuario.

### APIs de ULTRAKILL verificadas por metadatos (tools/ApiProbe, sin ejecutar código del juego)
- `MonoSingleton<T>.TryGetInstance(out T)`. Usamos este método y no `.Instance`, que llama a `GetOrCreateInstance` y podría crear la instancia.
- `NewMovement`: `hp:int`, `dead:bool`, `rb:Rigidbody`, `playerCollider:CapsuleCollider`, `boost`, `sliding`, `jumping`, `Position` y `HeadPosition`. También `GetHurt(int, bool, float, bool, bool, float, bool)` y `GetHealth(int, bool, bool, bool)`.
- `CameraController`: `cam:Camera`, `rotationX`, `rotationY` y `LateUpdate()`.
- `EnemyIdentifier`: `health`, `dead`, `enemyType` y `DeliverDamage(GameObject, Vector3, Vector3, float, bool, float, GameObject, bool, bool)`.
- `GunControl.NoWeapon()` / `YesWeapon()`, `FistControl.activated` y `SceneHelper.CurrentScene` (static).
- `PostProcessV2_Handler`: `mainCam`, `hudCam`, `virtualCam`, `mainTex`, `depthBuffer:RenderTexture`, `buffers:RenderBuffer[]`, `downscaleResolution`, `ZBufferParams(Camera)` y `GetInverseVP_NonOblique(Camera)`.
  - **Hipótesis H-R1:** la cámara principal renderiza a varios render targets a la vez (MRT: `mainTex` + `depthBuffer`). Un shader que solo escriba color podría dejar el `depthBuffer` de post-proceso sin rellenar en esos píxeles. Pendiente de verificar con `RenderProbe` y el cubo marcador.

### Puente (verificado: 17/17 tests)
- `bridge/Minekill.Link` (netstandard2.1): mapeo con nombre, seqlocks, latido y coordenadas.
- Oráculos de los tests:
  - Offsets contra la transcripción **y** contra el archivo `skycraft_protocol.h` real.
  - Prueba de estrés del seqlock: 1,5 s sin ninguna lectura rota aceptada.
  - **Prueba entre procesos** con `FakeGuest` (pid distinto, eco del estado, `teleportAck`).
  - Modo `--stay`.
  - Ejes y yaw según los puntos cardinales.
- Plugin `build/plugin/Minekill/` (Minekill.dll y Minekill.Link.dll): **compila** contra el `Assembly-CSharp` de esta instalación con 0 advertencias. **Todavía no se ha probado en el juego.**

### Primera ejecución en el juego (21:14, la arrancó el usuario)
- **A1 PASS:** el log muestra `MINEKILL 0.0.1 | Unity 2022.3.29f1 | game 4.0 | Intel(R) HD Graphics 630 (Direct3D11)`, 19/19 checks [PASS] y las 5 features ON. Sin excepciones en `Player.log`.
- **A2 parcial:** `link Local\Minekill_v1 ready (created, 191 MB)`. FakeGuest conectó (`host pid 9228`) y luego se cerró con `FAIL host heartbeat stale (3344 ms)`.
- Escenas observadas: `Bootstrap` e `Intro`. `Main Camera` dibuja a pantalla, en Forward. `ULTRAKILL/Master` no existe todavía en esas escenas (solo `Standard`). Capas: 8 = Environment, 7 = EnvironmentBaked, 24 = Outdoors, etc.
- **Causa raíz del fallo:**
  1. El host dejó de ejecutar frames unos 18 s: la siguiente línea `status` dice `beatAge=18046ms fps=0`. Coincide con que el usuario cambiara de ventana para lanzar FakeGuest. El código del juego no tiene `OnApplicationFocus` (verificado con ApiProbe `find`), así que la pausa la hace Unity (`runInBackground` = false). **Pendiente de confirmar** con la nueva línea de log `Application.runInBackground was …`.
  2. FakeGuest trataba la pausa del host como un error fatal, en contra de CONTRACT.md (ciclo de vida, punto 5).
- **Arreglos:**
  - Test de regresión `FakeGuestSurvivesHostPause`: falló con el mismo mensaje y ahora pasa. **18/18**.
  - FakeGuest espera (`WAIT`/`RESUMED`) y solo se cierra si el proceso del host ya no existe.
  - Plugin recompilado: opción `RunInBackground=true` y registro del valor original en el log.
  - El cubo marcador se recrea en cada escena y solo se muestra cuando hay pose de V1. Antes podía crearse en el menú con `Standard` y quedarse así, lo que habría invalidado la prueba A5.

### Segunda ejecución en el juego (21:20, log + captura del usuario)
- **Hipótesis 1 confirmada:** `Application.runInBackground was False`. Con `RunInBackground=True`, el enlace se mantuvo vivo toda la sesión (`peer=alive`, `beatAge` ≤ 63 ms, `guestFps` 15-16).
- **A1, A2 y A4 PASS.** El juego se pudo jugar y la captura muestra el panel. **A3 PASS:** `pose` cambia al moverse y `teleportSeq`/`ack` se actualizan en cada escena (4 → 5 → 6).
- **A5 FAIL (no se vio el cubo):** `poseErrBlocks=41166`. El **menú principal tiene un `NewMovement`** (`scene='Main Menu' pose=(0,-84,5)`), así que FakeGuest `--stay` se fijó en la zona de coordenadas del menú y no volvió a fijarse en 0-1. El cubo existía con `ULTRAKILL/Master` en la capa 8, pero a unas 82.000 unidades.
  - Arreglo: `--stay` se vuelve a fijar en cada `teleportSeq` nuevo, 3 s después (`--anchor-delay`), porque en 0-1 V1 empieza cayendo (`pose y=44.1` → `-1.35`).
  - Tests de regresión `FakeGuestStayModeReanchorsOnTeleport` (falló con -43007 y ahora pasa) y `FakeGuestStayModeAnchorsAfterDelay`. **20/20.**
- **Render de un nivel (Level 0-1), evidencia de `RenderProbe`:**
  - `Main Camera`: depth -1, mask `0x8FD2DFD7` (incluye la capa 8 Environment; excluye la 13 AlwaysOnTop), FOV 105, near 0.01, far 4000, clear Skybox, Forward.
  - `HUD Camera`: depth 0, mask `0x2000` (**capa 13 AlwaysOnTop**, que es donde deberá ir la mano de Minecraft), clear Depth, FOV 90.
  - `Virtual Camera`: mask `0x80000` (capa 19 Virtual Screen).
  - `PostProcessV2_Handler`: `downscale=480`. `mainTex` = 853x480 ARGB32 y `depthBuffer` = 853x480 **formato Depth de 32 bits**, es decir, un buffer de profundidad normal y no un render target de color. **H-R1 (MRT) refutada:** cualquier shader opaco que escriba Z queda ocluido correctamente. Además, el juego renderiza a 853x480 internamente y escala la imagen, lo que crea el pixelado.
  - En los niveles existen `ULTRAKILL/Master` y `psx/vertexlit/vertexlit`; en `Bootstrap`, `Intro` y `Main Menu` no.
- **Rendimiento:** de 146 a 330 FPS en 0-1 con la HD 630 (resolución interna 480p). Hay margen para un segundo proceso. Se observó un pico aislado `fps=1` en una línea `status`, posiblemente una carga; pendiente de investigar si se repite.

### Tercera ejecución (21:25, captura del usuario)
- **El cubo aparece en el sitio correcto:** `poseErrBlocks=2.12` coincide con lo que se alejó el usuario, y el cubo está apoyado en el suelo donde aterrizó V1. El anclaje y el mapeo Unity↔MC funcionan en el juego real.
- **Se renderiza dentro del frame del juego:** los bordes del cubo tienen la misma escalera de píxeles que el escenario (resolución interna de 480p), mientras que el panel IMGUI superpuesto se ve nítido a 1366x768. No es una superposición.
- **Falta (A5):**
  - (a) Captura con una pared delante, para la oclusión.
  - (b) La iluminación: el cubo sale verde plano y brillante en un nivel oscuro, porque no recibe la luz del nivel (el mismo límite que documenta Killcraft).
- **Crash nativo (21:30) introducido por mí en esa versión de diagnóstico:** `Crash!!!` en el hilo principal (UnityPlayer, sin símbolos), justo después de crear el cubo en `Main Menu`. Volcado: `%TEMP%\Hakita\ULTRAKILL\Crashes\Crash_2026-10-09_023024312`.
  - Causa: `ApplyMaterial` hacía `Object.Destroy(old)`, y en un `CreatePrimitive` recién creado `old` es el **Default-Material compartido de Unity**. La versión anterior no destruía nada y no se cerró.
  - Arreglo: solo se destruyen materiales registrados como propios (`owned`). `GuestMarker.Release` libera el objeto y su material al cambiar de escena (antes el material se perdía sin liberar). Ninguna otra llamada a `Destroy` del plugin toca recursos que no sean nuestros (revisado con grep).
  - Lección para el contrato: **nunca destruir un objeto de Unity que MINEKILL no haya creado.** Un crash nativo no se puede capturar con try/catch.
  - El `NullReferenceException` en `SettingsMenu.GraphicsSettings.SetColorPalette` del mismo log es del juego (`OptionsMenuToManager.OnEnable`), ocurre antes del cubo y no tiene relación con él.
- **Pista de iluminación** (propiedades registradas de `ULTRAKILL/Master` nuevo): `_Fog=0`, `_VertexLighting=1`, `_LightingContribution=0`, `CUSTOM_LIGHTMAP=0`, `_LightMapTex=null`. Un material nuevo tiene la **niebla desactivada**. Pendiente: comparar con la copia de un material del nivel (F9, modo 1).
- **Cuarta ejecución (21:33):** **sin crash**, con 6 cambios de material (F9) y liberaciones. El arreglo de propiedad está verificado.
  - Capturas: **Standard** recibe la luz de la escena (caras sombreadas, tono acorde con la sala). **ULTRAKILL/Master nuevo** se ve plano y brillante.
  - El modo 1 copió `Cube (1)` de la **capa 16 Invisible**, un material transparente (`renderQueue=3000`, `TRANSPARENCY`, `ZWrite=0`): plantilla inválida.
  - **Hipótesis H-L1:** `ULTRAKILL/Master` activa la luz y la niebla con **keywords** (el material copiado tiene `_FOG_ON` con `_Fog=0`). Un `new Material(shader)` no tiene keywords, así que se dibuja sin luz ni niebla.
  - Siguiente paso, cambiando una sola variable: plantilla filtrada a opacos visibles de las capas Environment, EnvironmentBaked, Outdoors y OutdoorsBaked. `RenderProbe` registra los grupos de keywords de las superficies del nivel, cuántas usan lightmap de Unity, las luces en tiempo real, la luz ambiental y la niebla.
  - **Oclusión: todavía sin captura.**
- **Quinta ejecución (21:36): sin cubo.** El panel mostraba `peer=none beatAge=57406ms`.
  - FakeGuest (pid 9800) **seguía vivo pero congelado**: 0,08 s de CPU sin aumentar y el hilo principal esperando. La consola tenía QuickEdit activado (`HKCU\Console\QuickEdit=1`): al hacer clic en la ventana, Windows bloquea las escrituras a la consola, y el latido estaba en el mismo hilo que `Console.WriteLine`.
  - Test de regresión `FakeGuestKeepsHeartbeatWhenConsoleBlocks` (stdout redirigido y nunca leído, más `--verbose`): falló («guest frames stalled 4 times») y ahora pasa. **21/21.**
  - Arreglo: la salida pasa por una cola acotada con su propio hilo; si se llena, se descartan líneas.
- **Evidencia de iluminación** (sondeo de 0-1 en esa ejecución):
  - Las superficies opacas del nivel tienen las keywords `_FOG_ON _GLITCHMODE_NONE _RAINCEILINGTOGGLE_ON _USEALBEDOASEMISSIVE_ON _VERTEXCOLORS_ON _VERTEXLIGHTING_ON _ZWRITE_ON VERTEX_LIGHTING`, con `lightmapIndex=-1` y sin `_LightMapTex`.
  - 4 luces en tiempo real, luz ambiental `Flat` negra y `RenderSettings.fog=False`: la niebla es la del shader propio, activada por keyword.
  - **H-L1 muy respaldada.** Se añadió el modo 3 de F9 (Master nuevo con esas keywords).
  - Solo se encontraron 4 superficies, posiblemente porque el sondeo corrió antes de que el nivel terminara de cargar. Se repite 5 s después.
- **Sexta ejecución (21:41), comparación de materiales con 4 capturas desde el mismo sitio. H-L1 CONFIRMADA para la iluminación:**
  - Modo 0 (sin keywords) y modo 1 (copia de `Cube`: `_FOG_ON`, pero **sin** `VERTEX_LIGHTING`/`_VERTEXLIGHTING_ON`, `_VertexLighting=0`): verde plano y brillante.
  - Modo 2 (Standard) y **modo 3 (Master + `_FOG_ON _VERTEXLIGHTING_ON VERTEX_LIGHTING _VERTEXCOLORS_ON _ZWRITE_ON`)**: iluminados por las luces de la sala. El modo 3 tiene además el tono cálido del nivel.
  - La variable decisiva es la keyword de iluminación por vértice. **Decisión: el modo 3 pasa a ser el predeterminado** para la geometría de Minecraft.
  - Sondeo a los 5 s: 38 superficies opacas en 6 grupos de keywords (11 emisivas, 5 sin luz), 0 con lightmap de Unity, **28 luces en tiempo real**, ambiente negro y niebla lineal (0.518, 0.235, 0.106) de 0 a 250 (leída por el shader propio; `RenderSettings.fog=False`).
  - Pendiente: derivar las keywords del nivel en curso en lugar de usar la lista fija (por ejemplo, el grupo `_FOG_OFF`).
- **Oclusión:** las capturas de los modos 0 y 1 muestran una muesca oscura en la esquina del cubo, un indicio pero no una prueba. Se añadió el **oráculo automático `OcclusionTest` (F8)**: raycast hasta la pared que hay delante, comprobación de que la pared cubre las 8 esquinas del cubo de prueba, 3 renders con una cámara de prueba (sin cubo / detrás / delante), recuento de píxeles cambiados y PNG en `BepInEx\minekill-tests\<fecha>`. PASS si el cubo de detrás cambia ≤ 0,5 % de lo que cambia el de delante.
- **Séptima ejecución (21:44): oclusión PASS.**
  - `PASS: wall 'Cube-clone' at 8.3 units; changed pixels behind=0, in front=986 (of 57600)`.
  - Revisé las imágenes de `minekill-tests\20261008-214434`: la base muestra la sala, y con el cubo delante aparece el cubo oliva iluminado (modo 3) ante la pared. El cubo detrás de la pared no cambia ni un píxel.
  - **Límite del oráculo:** usa una cámara copiada de `Main Camera` que renderiza a su propio RT. Comprueba la profundidad contra la geometría real del nivel con nuestro material, no el pipeline exacto de `PostProcessV2_Handler`. Para ese pipeline está la evidencia visual de la tercera ejecución (cubo pixelado dentro del frame) y el `depthBuffer`, que es un buffer de profundidad estándar.
  - **F8 abre la consola de desarrollador de ULTRAKILL.** La tecla del oráculo pasa a F6, con la opción renombrada a `SelfTestKey` para no heredar el valor guardado.
  - El `NullReferenceException` de `SettingsMenu...SetColorPalette` vuelve a aparecer al cargar el menú. Es del juego.

### MVP-0: CERRADO (2026-10-08)
| # | Criterio | Estado |
|---|---|---|
| A1 | carga del plugin, 19/19 checks | PASS |
| A2 | enlace entre procesos con ULTRAKILL | PASS |
| A3 | pose de V1 publicada, `teleportSeq`/`ack` | PASS |
| A4 | juego jugable, sin errores de MINEKILL | PASS (después del arreglo del crash del Default-Material) |
| A5 | geometría dentro del frame: posición, pixelado, luz y niebla (modo 3), oclusión (oráculo) | PASS |
| A6 | desconexión del invitado | PASS en el log (`stopped answering`, el juego sigue); la desaparición del cubo no se comprobó visualmente |

### Fase 2b/3: Minecraft real (inicio 2026-10-08 ~21:50)
- El usuario eligió el **entorno de desarrollo de Fabric** (Loom `runClient`, jugador offline `Steve`, sin cuenta y sin tocar su `.minecraft`).
- Toolchain: Gradle 9.7.1 (SHA256 verificado contra services.gradle.org), wrapper generado con la distribución oficial y `GRADLE_USER_HOME` en `.tools/gradle-home`. Loom resolvió la versión **1.18.3**. Versiones: MC 26.3, loader 0.19.5, Fabric API 0.161.0+26.3 (las mismas que SkyCraft 0.1.2 y Killcraft).
- **Cambio de decisión respecto al informe (opción b):** no se usa el mod Fabric de SkyCraft como invitado, sino uno propio (`mcmod/`). El de SkyCraft hace que Minecraft mande sobre el movimiento, y eso choca con la propiedad dividida (el cuerpo es de ULTRAKILL). Se mantiene el layout v11, por compatibilidad y para reutilizar herramientas.
- Enlace Java: `mcmod/.../link/Link.java` usa la API FFM de Java 25 (`OpenFileMappingW`/`MapViewOfFile`), el mismo enfoque que el `SkyLink.java` de SkyCraft. Incluye tests JUnit: offsets, apertura de un mapeo inexistente, ida y vuelta con lectura de offsets en crudo, estrés del seqlock y rechazo de versión o magic incorrectos.
- Oráculo de extremo a extremo sin ULTRAKILL: `tools/FakeHost` crea el mapeo y mueve un V1 falso en círculo (con un teleporte opcional) y puntúa: PASS si al menos el 80 % de las muestras están a ≤ 0,75 bloques, con `ack` correcto.
- Primer intento de compilación: `build genSources` en la misma invocación lo rechaza Gradle 9 («implicit dependency»). Se ejecutan por separado.
- **Corrección de proceso:** `genSources` **descompila** Minecraft, lo que va contra la regla del usuario («no descompiles sin autorización»). Las fuentes generadas se **borraron sin leerlas**. La verificación de APIs se hace solo con firmas (`javap -public` sobre el jar compilado) y con el compilador. Firmas comprobadas en 26.3: `Minecraft.player/level/gui/getLevelSource/createWorldOpenFlows/getSingleplayerServer`, `Gui.screen/setScreen/overlay`, `Entity.getX/getYRot/onGround/getEyeHeight`, `ServerPlayer.teleportTo(ServerLevel, x, y, z, Set<Relative>, yaw, pitch, bool)` y `level()`, `WorldOpenFlows.openWorld/createFreshLevel`, `LevelSettings(...)`, `WorldOptions(long, bool, bool)`, `WorldPresets.FLAT`.
- Tests Java (JUnit, `gradlew build`): **5/5** (offsets v11, mapeo inexistente, ida y vuelta con lectura en crudo, estrés del seqlock, rechazo de versión o magic incorrectos).
- `run/options.txt`: `pauseOnLostFocus:false`, `maxFps:30`, `renderDistance:4` y sonido silenciado. Minecraft reescribió el archivo conservando esas claves, así que son válidas en 26.3.
- **Prueba de extremo a extremo con Minecraft real (22:02), juez `FakeHost --seconds 40 --teleport-at 15`: PASS.**
  - El primer intento falló porque FakeHost dejó de esperar mientras `runClient` descargaba los assets (unos 2,5 min la primera vez). Se repitió con Minecraft ya abierto.
  - Minecraft creó el mundo `MINEKILL` (plano, creativo, pacífico), se enlazó y aplicó `teleport request 1` y después `2`.
  - **689 muestras, 99 % a ≤ 0,75 bloques, peor caso 1,32 bloques** (justo en el salto de 100 bloques). El seguimiento va a unos 20 Hz (`mcFrame` sube unos 20/s). Error típico de 0,2 a 0,5 bloques con V1 a unos 3 bloques/s: el retraso de un tick.
  - Se cerraron el cliente de Minecraft y los procesos de Gradle lanzados por mí.
- Siguiente prueba (la hace el usuario): ULTRAKILL real + Minecraft real (`tools/run-minecraft.ps1`).
- **ULTRAKILL real + Minecraft real (22:05-22:07): PASS.** Lo confirma el usuario («la cámara está sincronizada, el cubo me sigue y Steve se mueve cuando me muevo») y lo confirman los dos logs:
  - Panel: `peer=alive guestFps=20 ack=8/8 poseErrBlocks=0.08`.
  - En 0-1, 17 muestras: media de 0,74 bloques, mediana de unos 0,03, en reposo 0,00. Dos valores atípicos:
    - 10,39 al empezar el nivel, con V1 cayendo (`pose y=65.71`): retraso de un tick a gran velocidad vertical.
    - 0,97 en movimiento.
  - En el lado de Minecraft, `host` coincide con `player` hasta el centésimo en reposo, y con 0,66 bloques de retraso vertical durante un salto.
  - En `Main Menu` hubo dos desconexiones de unos 3 s: el hilo de cliente de Minecraft estaba ocupado abriendo el mundo (22:05:37-22:05:45). El enlace se recuperó solo.
  - **Fase 3 (pose) cerrada**, con el límite conocido de un tick de retraso. Se puede mejorar enviando el estado desde el hilo de render en vez de por tick, o interpolando.

### A + B: manos/HUD de Minecraft y combate (2026-10-08/09, probado con Minecraft real y FakeHost)
- **Protocolo** (v11, compatible con SkyCraft): overlay en triple búfer (0x300/0x340), anillo de entrada (0x1000), tabla de actores (0x12000) y anillo de eventos (0x17000).
  - Tests C# **27/27** (dos ejecuciones seguidas) y Java **9/9**.
  - Un test previo (`FakeGuestKeepsHeartbeatWhenConsoleBlocks`) era intermitente con carga en paralelo: exigía que no hubiera lecturas rotas, que son legítimas. Se corrigió el test (reintenta), no el producto.
- **A, en Minecraft:**
  - Mixins verificados con `javap`: `LevelRenderer.render` (no dibuja el mundo mientras está enlazado), `Minecraft.renderFrame` (captura después de `GameRenderer.render()`) y `LevelLoadTracker$WaitingForPlayerChunk.isReady`.
  - Copia asíncrona del render target a la memoria compartida, con la API `renderpearl` verificada.
- **Fallos encontrados y corregidos:**
  - Overlay 0 % transparente: Minecraft se quedaba en «Loading terrain…» porque esperaba el render de un chunk que nunca se dibuja. Arreglo: `WaitingForPlayerChunkMixin`. Después del arreglo, **85,7 % transparente**.
  - Steve moría de caída 3 s después de cada desconexión del host (`Steve fell from a high place`) y perdía la espada. Arreglo: el cuerpo nunca tiene gravedad ni acumula caída y se queda quieto sin host. Regresión: 15 s desconectado, salud 20 y espada en la mano.
  - El juez FakeHost contaba `--seconds` desde su propio arranque y no desde la conexión. Ahora cuenta desde la conexión.
- **Resultado de A** (`build/overlay-A2.png` y `build/ov/*.png`): mano de Minecraft, espada de diamante, corazones, hambre, barra de XP y hotbar sobre fondo transparente, a unos **29 fps**, 854x480 RGBA8. La tecla «2» enviada desde el host cambia la ranura seleccionada (comprobado en el log `slot=2` y en las capturas).
- **Resultado de B** (`FakeHost --combat`): **COMBAT PASS.**
  - 5/5 ataques dan `EvHitActor` de **7,0** (el daño de vanilla de la espada de diamante), con el retroceso hacia el enemigo.
  - El stand-in es un cerdo invisible escalado a 0,8x1,9 bloques.
  - Un golpe a Steve de 5 lo deja en 16 de salud (15 más un punto de regeneración natural).
  - El golpe letal provoca `Steve died` → `EvPlayerDied` → respawn automático con el kit.
- **Lado ULTRAKILL** (compila; **sin probar en el juego**):
  - `HandMode`: tecla º; NoWeapon/NoFist, con `GunControl.Update` y `FistControl.Update` parcheados mientras está activo; 1-9 y la rueda se reenvían a Minecraft.
  - `Overlay`: RawImage en un canvas superior.
  - `Combat`: `EnemyTracker` → actores a 20 Hz → `DeliverDamage`; `GetHurt` con prefijo y finalizer que dejan decidir a ULTRAKILL el daño real (i-frames, dash) y lo envían a la salud de Minecraft.
  - Compat añade 15 comprobaciones nuevas.
- **Límites conocidos:**
  - Se ven dos miras (la de ULTRAKILL y la de Minecraft).
  - La pantalla «You Died!» de Minecraft aparece 1 s.
  - El alcance es el de Minecraft: 3 bloques = 6 unidades.
  - La barra de vida de V1 no refleja la salud real, que es la de los corazones.
- **A + B en ULTRAKILL real (2026-10-09 ~00:05, confirmado por el usuario y por su log): PASS.**
  - Se aplicaron los 3 parches.
  - Overlay de 854x480.
  - Modo mano alternado 5 veces sin errores.
  - **20 Filths eliminados** con golpes de Minecraft: 7,0 con la espada cargada y entre 1,8 y 3,4 al hacer clic seguido. La recarga de ataque de Minecraft funciona dentro de ULTRAKILL.
  - **3 golpes a V1** pasaron a la salud de Minecraft («V1 took 30 ULTRAKILL damage -> 6.00»).
  - Sin excepciones.

### C: bloques, TNT, escudo y extras (2026-10-09)
- **Coordenadas:** `y_MC = y_UK/2 + 64`. Los niveles de -256 a +512 unidades caben en la altura de construcción, en lugar de quedar en el vacío de Minecraft, que mata por debajo de -128. Tests: altura, celda que contiene el punto y centro de celda.
- **Anillo de render v11** (bytes, mensajes alineados a 8, relleno para dar la vuelta) con tipos propios 100+: `RenBlocks`, `RenTexSet` (PNG de arriba/lados/abajo leídos del gestor de recursos de Minecraft en tiempo de ejecución) y `RenTnt`. Tests C# (incluido el paso de unos 200 MB por 64 MB) y Java (ídem).
- **Comandos:** `InUse`/`InBreak` (100/101). ULTRAKILL hace el raycast porque Minecraft no tiene la geometría del nivel.
- **`CubeGeometry`** sin dependencias de Unity, con tests de que todos los triángulos miran hacia fuera, las submallas y las texturas derechas. Se verificó que el `System.Numerics.dll` del juego define `Vector2`/`Vector3` con `Cross`, `Dot` y operadores (ApiProbe con `MINEKILL_PROBE_ASSEMBLY`).
- **Prueba `FakeHost --blocks` con Minecraft real: PASS.** Colocar sobre el nivel, colocar contra una cara, TNT, encenderla con mechero, explosión y romper; 6 PNG (adoquín y TNT).
- **Fallos que encontraron las pruebas y se corrigieron:**
  1. Carrera de ranura: el servidor usaba la ranura anterior y la TNT salió como adoquín. Ahora la petición lleva la ranura.
  2. La prueba no se podía repetir porque el mundo conserva los bloques. Ahora limpia las celdas al empezar.
  3. La TNT encendida caía al vacío y explotaba en y=20. Ahora no tiene gravedad.
  4. Sin gravedad, su impulso de encendido la subía y explotaba en y=70,5. Ahora su velocidad es cero. Resultado: explota en (2.5, 63.5, 2.5), el centro del bloque.
- **Explosiones:** Minecraft las marca con su `DamageTypeTags.IS_EXPLOSION` → `HitExplosion`. Al principio usé una heurística por cantidad de daño y la sustituí por esta etiqueta.
- **Lado ULTRAKILL** (compila; **sin probar en el juego**):
  - `BlockWorld`: cubos en Environment con BoxCollider, Master + keywords del nivel, textura de Minecraft con filtro point, tinte de hierba y hojas, ALPHA_TEST en recortados; TNT encendida con destello blanco y crecimiento final; `ExplosionFx` con esfera y luz, más `CameraShake`.
  - `Interaction`: el clic izquierdo rompe un bloque al alcance o ataca; el derecho coloca o enciende contra una cara, coloca contra el nivel (nunca dentro de V1) o hace el «usar» de Minecraft (escudo, manzana), mantenido mientras se pulsa.
  - `Style`: «MINECRAFTED» y «TNT'D» en el medidor de estilo.
  - Kit: espada, adoquín, tablones, cristal, ladrillos, TNT ×16, mechero, manzana dorada ×8, troncos y escudo en la otra mano.
  - Con el escudo levantado, el golpe se lo lleva el escudo, que pierde durabilidad.
- **Estado real de C en el juego: SIN PROBAR.** El log de ULTRAKILL del usuario no tiene ninguna línea de bloques ni de TNT (última escritura a las 00:06, antes de instalar C). Lo que dicen los logs de Minecraft de esa hora son mis pruebas automáticas.

### D: interacciones cruzadas (2026-10-09)
- **Lado Minecraft, `FakeHost --synergy` con Minecraft real: PASS** en las 6:
  - **Botín:** 5 flechas, 1 TNT y 10 de XP.
  - **Arco:** flecha a 2,97-3,00 bloques/tick, crítica, con 8-10 de daño.
  - **Bola de nieve y perla** a 1,48 bloques/tick.
  - **Parry** con el escudo levantado hace 0,4 s.
  - **Explosión de ULTRAKILL** → TNT encendida → explota en (3.5, 63.5, 0.5).
  - **Crítico al caer:** 10,5 = 7 × 1,5.
- **Fallo encontrado y corregido:** el crítico al caer daba 7,0. El diagnóstico midió `fallDistance=0.0` en el momento del golpe en el servidor (el teletransporte y el reinicio de caída de cada tick lo ponen a 0). Arreglo: `AttackEntityCallback` (Fabric) pone 0,5 justo antes de `Player.attack` mientras V1 cae; está por debajo de 3 bloques, así que no hay daño por caída.
- **APIs verificadas:**
  - ULTRAKILL (ApiProbe): `Explosion.Start/maxSize/harmless`, `NewMovement.GetHealth(Int32, Boolean, Boolean, Boolean)`, `gc.onGround`, `falling`, `rb`, `TimeController.ParryFlash`, `StyleHUD.rankIndex`, `EnemyIdentifier.totalHealthModifier/isBoss`, `EnemyIdentifierIdentifier.eid` y los 43 nombres de `EnemyType`.
  - Minecraft (javap y compilador): proyectiles en `projectile.arrow` y `projectile.throwableitemprojectile` (26.3), `giveExperiencePoints`, `heal`, `getTicksUsingItem` y `getExplosionResistance`.
- **Lado ULTRAKILL** (compila; **sin probar en el juego**):
  - `Synergy`: tabla de botín para 19 tipos de enemigo y botín extra de jefe; patch `Explosion.Start` → `InExplode`; patch `GetHealth` → `InHeal` (Bloodcraft); parry → `ParryFlash` y estilo.
  - `Projectiles`: vuelo con la gravedad y el rozamiento de Minecraft, `RaycastAll` que ignora a V1, impacto en enemigos (extremidades vía `EnemyIdentifierIdentifier`), flechas clavadas en la pared, la perla teletransporta a V1 (5 de daño a Steve) y sprites con las texturas de objeto de Minecraft.
  - Estilo: ARROWED, SNOWBALLED, ENDERED y SHIELD PARRY.
- **Tests:** C# **38/38**, Java 11/11 (incluido `minekillNumbersMatchCSharp`/`MinekillNumbersMatchJava`, que fijan los mismos números en los dos lados).

### E: contorno de selección y parries de ULTRAKILL (2026-10-09, petición del usuario con captura de referencia)
- **Contorno vanilla** (`Outline`): malla de 12 aristas (`MeshTopology.Lines`), negra al 40 %, 1,004 veces el tamaño del bloque, con test de profundidad y sin escritura de profundidad, shader `Hidden/Internal-Colored` (se registra en el log cuál se usa).
  - Marca el bloque de Minecraft apuntado o, sobre el nivel, el bloque del suelo o la pared que hay justo detrás de la superficie.
  - Solo al alcance de bloque de Minecraft (4,5 bloques) y en modo mano.
- **Parries con el sistema real de ULTRAKILL** (`Parrying`). Miembros verificados con ApiProbe: `Punch.ActiveStart` (privado), `TryParryProjectile(Transform, Boolean)`, `ParryProjectile(Projectile)`, `Parry(Boolean, EnemyIdentifier, String)` (público), `Punch.type`, `FistControl.currentPunch/RefreshArm`, `Projectile.friendly/playerBullet/unparryable`.
  - **Golpe** (espada, arco, puño): ejecuta los fotogramas activos del puñetazo Feedbacker de V1 (oculto). Devuelve proyectiles de delante y para a enemigos a mitad de ataque. Técnica de Killcraft (MIT).
  - **Escudo levantado** (`GuestBlocking`, nuevo bit 1 << 8 de Minecraft): cada 30 ms se buscan en 8 unidades los proyectiles enemigos parables que se acercan a V1 y se devuelven con `TryParryProjectile`, o con `ParryProjectile` si el primero no lo consigue.
  - **Parry cuerpo a cuerpo con escudo** (`EvParry` de Minecraft, ventana de 0,5 s): `Punch.Parry` sobre el enemigo más cercano por delante (7 unidades); si no hay ninguno, el destello propio.
  - Nueva función de Compat `Parry` con 10 comprobaciones. El golpe solo se ejecuta si pasan.
- **Lado Minecraft verificado:** `FakeHost --synergy` → `GUEST shield up (GuestBlocking)` y **SYNERGY PASS** (las 7).
- **Lado ULTRAKILL:** compila; **sin probar en el juego** (contorno, parries, y también C y D).
- **Límite conocido:** cada golpe en modo mano ejecuta también el puñetazo de V1, así que su daño de puñetazo (1) se suma al de Minecraft. Un Filth muere igual de un golpe; contra enemigos grandes es un pequeño extra.

- **Diagnóstico añadido, sin arreglo todavía:** con F9 se alternan 3 materiales (`ULTRAKILL/Master` nuevo, copia del material de la pared más cercana y Standard), y cada cambio registra en el log el shader, las keywords y todas las propiedades.

### F: movimiento de Minecraft (tecla M) e inventario con E (2026-10-09, petición del usuario)
- **Causa del fallo «no puedo abrir el inventario con E»:** ULTRAKILL nunca enviaba la E a Minecraft. Solo se reenviaban 1-9 y la rueda como órdenes de ranura, y no existía ningún canal para el cursor ni los clics de una pantalla de Minecraft.
- **Inventario (`McScreen` en ULTRAKILL, `Screens` en Minecraft):**
  - En modo mano, E se envía como tecla real al `KeyboardHandler.keyPress` de Minecraft, que abre su propio `InventoryScreen` en el overlay.
  - Con una pantalla de Minecraft abierta:
    - ULTRAKILL libera el cursor y congela la cámara (`CameraController.activated`).
    - V1 queda quieto con las reglas de movimiento de Minecraft y sin teclas; el juego sigue corriendo.
    - El cursor (normalizado sobre el overlay) va a `MouseHandler.onMove` y los clics a `onButton`.
    - E, Esc, Q, F y 1-9 (con shift y ctrl) van a `keyPress`.
    - Esc no abre la pausa de ULTRAKILL (patch `OptionsManager.Update`).
  - **Protocolo** (propio de MINEKILL):
    - `InCursor` 105, `InScreenButton` 106 e `InFall` 107.
    - `InKey` lleva el scancode SDL. En Minecraft 26.3 las teclas son scancodes SDL: E=8 y Esc=41; ratón izquierdo=1 y derecho=3.
    - El test Java `inputCodesAreMinecrafts` compara con `InputConstants` de Minecraft.
  - **Mixin `InputConstantsMixin`:** mientras está enlazado, Minecraft nunca captura el cursor del sistema.
  - Firmas verificadas con javap:
    - `KeyEvent(int key, int keycode, int modifiers)`, `KeyboardHandler.keyPress(long, int, KeyEvent)`.
    - `MouseHandler.onMove(long, double×4)`, `onButton(long, MouseButtonInfo, int)`, `MouseButtonInfo(int, int)`.
    - `SDLKeyboard.SDL_GetKeyFromScancode(int, short, boolean)`, `Window.handle()/getScreenWidth()/getScreenHeight()`.
    - `InputConstants.grabMouse(Window, double, double)` y `DamageSources.fall()`.
- **Verificado con Minecraft real: `FakeHost --inventory` → INVENTORY PASS** (`.tools/test-runs/inventory-1`):
  - E abre a los 3,19 s y cierra a los 10,05 s.
  - E vuelve a abrir a los 12,09 s y Esc cierra a los 13,09 s.
  - Esc sin pantalla no abre la pausa de Minecraft.
  - Log de Minecraft:
    - `Minecraft screen -> InventoryScreen`
    - `cursor carries 1 minecraft:diamond_sword` → `nothing` → `diamond_sword` → `nothing`: la espada va a la mochila y vuelve a la ranura 1 con clics reales.
  - Captura del overlay (`overlay-t07s-f415.png`): el inventario de Minecraft se dibuja dentro del overlay.
- **Movimiento de Minecraft (`McMovement`, tecla `m`, configurable con `MinecraftMovementKey`):**
  - Las reglas de Minecraft están en `McPhysics` (Minekill.Link, sin Unity, con tests):
    - andar 4,317, correr 5,612 (Ctrl o doble W) y agacharse 1,295 b/s;
    - salto de 0,42 (1,25 bloques; +0,2 al correr) con retardo de 10 ticks;
    - gravedad de 0,08 con rozamiento de 0,98; aire 0,02/0,026 y 0,91; suelo 0,6 × 0,91;
    - agachado no se cae del borde (`maybeBackOffFromEdge`, con un `Physics.CheckBox` 0,6 bloques bajo los pies);
    - correr se corta al chocar con una pared.
  - **Daño de caída** = `ceil(distancia − 3)`, enviado a Minecraft con `DamageSources.fall()`. Como en `Entity.checkFallDamage`, no cuenta el tramo del tick en que aterriza.
  - ULTRAKILL sigue haciendo las colisiones con su Rigidbody. Mientras está activo, se saltan `NewMovement.Update/FixedUpdate` (sin dash, slide, slam ni wall jump) y `rb.useGravity=false`; al salir se restaura. La cuenta atrás de `hurtInvincibility` se hace a mano.
  - Se desactiva solo durante cinemáticas (`activated`), con la muerte y al acabar el nivel (`levelOver`).
  - Nuevas funciones de Compat: `McMovement` (14 comprobaciones) y `McScreens` (7, y depende de `McMovement` y `MinecraftHand`).
- **Tests:** C# **51/51** (13 nuevos de `McPhysics`) y Java **12/12**.
- **Lado ULTRAKILL:** compila; **sin probar en el juego**. Hay que comprobar en el juego:
  - que `gc.onGround` sigue actualizándose con `NewMovement` en pausa;
  - que `cc.activated=false` congela solo la vista;
  - que el cursor se libera.

### F2: sensación de Minecraft — FOV, balanceo y velocidad (2026-10-09, primera prueba del usuario con F)
- **Lo que confirmó el log del usuario (10:12):**
  - Las 14 comprobaciones de McMovement y las 7 de McScreens dan PASS.
  - Se aplicaron los parches `NewMovement` y `OptionsManager`.
  - El inventario se abrió y se cerró 3 veces con E.
  - El modo M se activó.
  - Daño de caída 1, 1 y 11 enviado a Minecraft.
- **Lo que reportó el usuario:**
  - Se siente «muy lento».
  - La mano no se balancea al andar ni al correr.
  - El FOV no cambia al correr.
- **Causas:**
  - La mano: `AbstractClientPlayer.updateBob()` lee el movimiento del jugador de Minecraft, que siempre es 0 porque ULTRAKILL lo mueve por teletransporte.
  - El FOV: nunca se aplicaba nada de Minecraft a la cámara de ULTRAKILL, que sigue con el suyo (muy ancho), y eso hace que todo parezca más lento.
- **Arreglos en Minecraft:**
  - Mixin `AbstractClientPlayerMixin`: `updateBob` → `avatarState().updateBob(objetivo)`, con la velocidad real de V1 (posición del host entre ticks × 0,546) y `addWalkDistance(movido × 0,6)`, como en `Entity.move`.
  - `GuestState.fov` = `options.fov` × `getFieldOfViewModifier(true, fovEffectScale)` (zoom de arco y catalejo, pociones) × 1,15 al correr (nueva bandera `HostSprinting` 32), escalado por «FOV Effects».
- **Arreglos en ULTRAKILL (`McCamera`, configurable con `MinecraftCamera` y `MinecraftViewBob`):**
  - Solo mientras se renderiza la cámara del jugador (`Camera.onPreCull`), y se deshace en `onPostRender`, así que el apuntado y los raycasts de ULTRAKILL no lo ven.
  - FOV de Minecraft con su suavizado: la mitad por tick.
  - Altura de ojos de Steve: 1,62 bloques, 1,27 agachado.
  - `bobView` de Minecraft (`McViewBob`, con tests; conserva la extrapolación de `walkDist` que hace Minecraft).
- **Velocidad:**
  - Mientras está activo se quitan el `drag` del Rigidbody y la fricción del material de V1; se restauran al salir.
  - Nuevo diagnóstico en el log: `Minecraft movement: asked X b/s, real Y b/s` cada 2 s, más los valores originales de drag, material, altura de V1, altura de ojos y FOV al activar.
  - Si la velocidad real queda por debajo de la pedida, ahí se verá.
- **Verificado con Minecraft real: `FakeHost --bob` → BOB PASS** (`.tools/test-runs/bob-1`):
  - Oscilación del centro de la mano quieto: 0,50 px (107 fotogramas); andando: 24,10 px (146 fotogramas).
  - FOV quieto 70,0 y corriendo 80,5.
- **Tests:** C# **54/54** y Java OK.
- **Lado ULTRAKILL de la cámara:** compila; **sin probar en el juego**.

### F3: velocidad, FOV y sonidos de Minecraft (2026-10-09, segunda prueba del usuario)
- **Medido en el juego del usuario (10:30):**
  - La velocidad real es exactamente la pedida: andar 4,30-4,31 y correr 5,61 b/s (`asked` = `real`).
  - V1 mide 1,75 bloques, con los ojos a 1,45, FOV 105 y `fixedDeltaTime` 0,008; el material ya era `NoFriction` y el drag 0.
  - La sensación de lentitud no venía de la física.
- **Petición:** más velocidad al andar y al correr (el salto igual), más FOV y los sonidos de Minecraft.
- **Velocidad:** `McPhysics.SpeedMultiplier` (configurable con `MinecraftSpeed`, 1,5 por defecto) escala la aceleración en suelo y aire y el impulso del salto con carrera.
  - Con ×1,5: andar 6,48 y correr 8,42 b/s.
  - Agachado (1,295) y la altura del salto no cambian. Test `SpeedMultiplierScalesWalkAndSprintOnly`.
  - Nuevo log `ULTRAKILL movement: top ground speed` para equilibrar con datos.
- **FOV:** `MinecraftFovBonus` (+10° por defecto) sobre el FOV de Minecraft: 80 quieto y unos 90,5 corriendo.
- **Sonidos:**
  - `run/options.txt`: volumen general 1, música, discos, clima y ambiente a 0 (que no pisen la música de ULTRAKILL).
  - Pasos (`Sounds`): Minecraft no los genera porque Steve se mueve por teletransporte. Ahora suena un paso cada 1/0,6 bloques de recorrido real de V1, ninguno agachado (como `getMovementEmission`) y la caída al aterrizar. Usa el bloque de Minecraft bajo los pies o piedra sobre el suelo de ULTRAKILL; se reproduce sin atenuación.
  - Nueva bandera `HostSneaking` 64.
  - Los golpes a los espejos ahora cuentan como acertados (mixin `LivingEntityMixin` en `hurtServer`, que sustituye a `ALLOW_DAMAGE`).
    - Antes Minecraft tocaba `attack.nodamage` (el fiuu de fallar) en cada espadazo; ahora toca `attack.strong`, `crit`, etc.
    - También gasta durabilidad y hambre, como un golpe de verdad.
- **Fallo encontrado y arreglado (lo habría visto el usuario):** al girar por ±180° de ULTRAKILL, el yaw de Minecraft saltaba 360° y la inercia del brazo barría la mano por la pantalla durante 0,4 s.
  - Se diagnosticó con una traza por fotograma de FakeHost (`MINEKILL_BOB_DEBUG=1`, el centro de la mano saltó a x=505 en s=3,26 sin ningún golpe registrado).
  - Arreglo: el servidor aplica el yaw continuo (`getYRot + wrapDegrees(Δ)`).
- **Verificado con Minecraft real:**
  - `--bob` PASS: mano girando quieta 0,84 px (antes hasta 39) y andando 26,8 px; FOV 70 → 80,5.
  - Log de sonidos: `block.stone.step` 6-9 por cada 5 s andando, con el oyente siguiendo a Steve.
  - `--combat` PASS con `entity.player.attack.strong`.
  - `--synergy` PASS (7/7) con `arrow.shoot`, `ender_pearl.throw`, `snowball.throw`, `generic.explode`, `attack.crit` y `block.stone.fall`.
- **Tests:** C# 55/55 y Java OK.
- **Límite conocido:** el Steve del servidor nunca está «en el suelo» (sin gravedad y teletransportado), así que no hay barrido de espada ni su sonido.
- **Sin probar en el juego:** el ×1,5, el +10° y que el audio de Minecraft se oiga junto a ULTRAKILL.
