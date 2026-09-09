# VR Mod para HOPECORE / "Control, I'm Not Coming Back"

*[English version](README.md)*

## Aviso sobre uso de IA

Este mod se ha desarrollado con la ayuda de Claude (Claude Code, Anthropic), un asistente de codificación
basado en LLM. Claude ha escrito la mayor parte del código de `UnityVRModFix`, ha decompilado y analizado
el código del juego y de UnityVRMod para diagnosticar problemas, y ha redactado este README bajo la
dirección y supervisión de un humano (todas las decisiones de diseño, pruebas con el visor puesto y
validación final las ha hecho una persona). Como con cualquier código generado por IA: revisa antes de
confiar ciegamente, especialmente si vas a modificarlo o reutilizarlo en otro proyecto.

Mod de VR de 6DOF (solo tracking de cabeza, sin mandos de movimiento) para este juego de Unity no-VR,
hecho como proyecto personal/divertido. Movimiento y todas las acciones se hacen con teclado/ratón como
siempre; la cabeza controla la cámara y un puntero central (gaze) sirve para interactuar. Estado: jugado
de principio a fin en VR sin problemas de nuestro código (quedan un par de limitaciones conocidas, ver
abajo).

## Instalación

### Opción A: paquete todo en uno (recomendado)

Coge **`HOPECORE-VR-Mod-vX.Y-AllInOne.zip`** de la [página de Releases](../../releases). Trae BepInEx,
UnityVRMod y nuestro propio plugin `UnityVRModFix` juntos, ya configurados.

1. Descomprime el contenido del zip directamente en la carpeta de instalación del juego (la que tiene
   `HOPECORE.exe`).
2. Steam: clic derecho al juego -> Propiedades -> Opciones de lanzamiento -> añade `-force-d3d11`.
3. Asegúrate de tener SteamVR instalado y el visor conectado/encendido.
4. Lanza el juego normal desde Steam.

(Los pasos completos también vienen como `INSTALL.txt` dentro del zip.)

### Opción B: instalar todo por separado

Si prefieres montarlo tú mismo (por ejemplo, para usar otra versión de BepInEx/UnityVRMod):

1. Descarga y descomprime [BepInEx 6](https://github.com/BepInEx/BepInEx) (variante Mono) en la raíz del
   juego.
2. Descarga y descomprime [UnityVRMod](https://github.com/NewUnityModder/UnityVRMod) (variante OpenVR +
   Mono) en `BepInEx\plugins\UnityVRMod\`.
3. Coge solo `UnityVRModFix.dll` de las Releases de este repo y ponlo en
   `BepInEx\plugins\UnityVRModFix\UnityVRModFix.dll` (o compílalo tú mismo, ver "Compilar y desplegar"
   más abajo).
4. Los mismos dos últimos pasos que en la Opción A: opción de lanzamiento `-force-d3d11`, SteamVR
   abierto antes de lanzar el juego.

## Licencias / software de terceros

Este repositorio (el código fuente de `UnityVRModFix`) es nuestro; consulta el historial de git para la
autoría. Depende de, y en el paquete de release "todo en uno" incluye, dos proyectos de terceros
separados, sin modificar:

- **[BepInEx](https://github.com/BepInEx/BepInEx)** 6 (bleeding-edge #785, variante Mono); GNU Lesser
  General Public License v2.1. Texto de la licencia incluido como `LICENSE-BepInEx.txt` en el paquete
  de release.
- **[UnityVRMod](https://github.com/NewUnityModder/UnityVRMod)** v0.1.0-beta (variante OpenVR + Mono);
  GNU General Public License v3.0. Texto de la licencia incluido como `LICENSE-UnityVRMod.txt` en el
  paquete de release.

Ni este repositorio ni los paquetes de release contienen código ni assets del propio juego.

## Cómo funciona todo (arquitectura)

Tres piezas, cada una en su propia carpeta/DLL:

1. **BepInEx 6** (bleeding-edge #785, variante Mono); el "cargador de mods" de Unity. Inyectado vía
   doorstop (`winhttp.dll` + `doorstop_config.ini`) en la raíz del juego.
2. **UnityVRMod v0.1.0-beta** (`BepInEx\plugins\UnityVRMod\`); el mod de terceros
   (https://github.com/NewUnityModder/UnityVRMod) que realmente habla con SteamVR/OpenVR: crea el rig de
   cámaras estéreo, lee las poses del headset y envía los frames al compositor. No sabe nada de este
   juego en concreto.
3. **UnityVRModFix** (`mod\UnityVRModFix\`, este proyecto); nuestro propio plugin de BepInEx, escrito
   para este mod. Usa Harmony para parchear tanto UnityVRMod como el propio código del juego
   (`Assembly-CSharp.dll`) y arreglar todo lo que no funciona out-of-the-box: cámara que no sigue al
   juego, canvases de UI invisibles en VR, altura doblada, vídeos que no se ven, etc. Es el único código
   que hemos escrito nosotros; todo lo demás es de terceros.

`UnityVRModFix.dll` se compila con `dotnet build -c Release` dentro de `mod\UnityVRModFix\` y se copia a
mano a `BepInEx\plugins\UnityVRModFix\UnityVRModFix.dll`. El `.csproj` referencia copias locales de las
DLLs del juego/Unity/UnityVRMod (todas con `<Private>false</Private>`, solo para compilar contra sus
tipos - nunca se redistribuyen).

**UnityVRMod no soporta D3D12** (solo D3D11), y este juego arranca en D3D12 por defecto. Hay que forzar
`-force-d3d11` como opción de lanzamiento (Steam → clic derecho → Propiedades → Opciones de lanzamiento).

## Los arreglos (`mod\UnityVRModFix\*.cs`)

Todos se aplican vía Harmony desde `Plugin.cs` al arrancar. Los activos ahora mismo:

- **`CameraFollowFix.cs`**; UnityVRMod solo copia la posición/rotación de la cámara del juego al rig VR
  UNA vez, al crear el rig. Este juego mueve la cámara con Cinemachine constantemente (siguiendo al
  jugador, reencuadres, etc.), así que sin esto el rig se queda flotando donde estaba la cámara en el
  instante 0. Reescribe la posición/yaw del rig cada frame, antes de aplicar el head-tracking encima.
- **`HeightFix.cs`**; UnityVRMod usa espacio de tracking "Standing" (altura absoluta real del headset
  sobre el suelo), que se sumaba a la altura de ojos ya correcta de la cámara del juego, duplicando la
  altura. Cambiado a "Seated" (altura relativa a donde estaba el headset al activar VR).
- **`PointerFix.cs`**; el raycast de interacción del juego (`CheckRay`) usaba la cámara plana, cuyo
  pitch no sigue al headset (solo el yaw, vía `CameraFollowFix`). Lo sustituye por un raycast desde la
  mirada real del headset, para que el crosshair/interacción sigan a donde miras con la cabeza.
- **`CameraCleanerFix.cs`**; una cámara "CameraCleaner" (probablemente una esfera que oculta el fondo,
  pensada para estar siempre centrada en la cámara plana) se volvía visible por dentro al mover la
  cabeza en VR. Desactivada mientras el rig VR esté activo.
- **`PlayerCapsuleFix.cs`**; el propio jugador tenía una malla de cápsula de colisión visible (textura
  de cuadros, claramente un placeholder de desarrollo), invisible en modo plano porque la cámara está
  siempre en su centro exacto. Oculta su renderer mientras el rig VR esté activo.
- **`BackwardMovementFix.cs`**; dos cambios al movimiento (`FirstPersonController.FixedUpdate`):
  (1) el juego bloqueaba caminar hacia atrás, quitado; (2) el movimiento ahora es relativo a hacia dónde
  mira la CABEZA (headset), no hacia dónde apunta el cuerpo/cámara plana; así "adelante" en el stick
  es intuitivo en VR. Reversible con `BackwardMovementFix.Enabled = false`.
- **`GamepadEmulator.cs`** (clase `ActionEnableFix`, el nombre del archivo quedó desactualizado); varias
  Input Actions del juego ("Move", "Look", "Interact") empiezan deshabilitadas por razones ajenas a
  cualquier mando/dispositivo; las reactiva cada frame, respetando los bloqueos de movimiento reales del
  juego (`playerCanMove`/`cameraCanMove`) y una lista explícita de escenas con el movimiento bloqueado a
  propósito (hoy solo `1_ModuloMandosCohete`, la consola de mandos del cohete). También resetea el flag
  `GameManager.IsInDialogue()` al cambiar de escena y al arrancar, porque se queda pillado en `true` (bug
  del propio juego, no nuestro) y bloquearía "Interact" para siempre si no se corrigiera.
- **`CanvasFix.cs`**; el arreglo más grande. Los Canvas en modo "Screen Space" (la inmensa mayoría de la
  UI del juego: diálogos, menús, crosshair) no llegan a las cámaras estéreo de VR, así que son invisibles
  con el headset puesto. Los convierte a "World Space" y los cuelga delante de la cabeza:
  - Solo convierte canvases con texto real, el crosshair, los de vídeo, o el "Fade Canvas" (fundido a
    color / créditos); el resto son overlays decorativos (filtros, marcos de resolución) que se
    quedarían como una segunda pantalla flotante y se descartan.
  - Se cuelgan de un ancla `DontDestroyOnLoad` (no del rig VR, que se destruye y recrea en cada cambio de
    escena) que copia la pose de la cámara del ojo izquierdo cada frame; separado en `Tick()` (barrido
    de canvases nuevos, con throttle de 0,25s) y `LateTick()` (solo mover el ancla, cada frame, para que
    el diálogo no vaya a tirones).
  - El diálogo/menú normal va a 2m de distancia; los "fullScreen" (vídeo) a 4m y mucho más grandes.
  - El "Fade Canvas" es especial: el mismo objeto se usa tanto para el flash blanco de la explosión (sin
    texto) como para los créditos finales (con texto); su tamaño se recalcula cada `Tick()` según si
    tiene texto activo en ese momento: pequeño/legible con texto (créditos), grande y muy cerca (0,6m)
    sin texto (flash que debe cegar/cubrir el campo de visión).
  - Al cambiar de escena, destruye los canvases adoptados que no vinieran de la escena "Persistent"
    (para que no se acumulen para siempre), dejando intactos los que sí (como el crosshair).
  - También desactiva cualquier `RawImage` con una `RenderTexture` en vivo (cámara de fondo) dentro de un
    canvas normal; el menú principal tiene un filtro retro pixelado así, y al convertir su canvas a
    world-space esa cámara de fondo acababa grabándose a sí misma, un efecto recursivo de "pantalla
    dentro de pantalla".
- **`VideoFix.cs`**; los `VideoPlayer` en modo `CameraFarPlane`/`CameraNearPlane` (dibujan sobre la
  cámara plana original) no llegan a las cámaras VR. Redirige cada uno a su propia `RenderTexture` y un
  panel dedicado delante del jugador, visible solo mientras `isPlaying`. Se reafirma cada 0,25s (no cada
  frame) y se oculta a la fuerza en cada cambio de escena, para que un vídeo que ya terminó no se quede
  flotando en la siguiente escena.
- **`RigidbodyInterpolationFix.cs`**; objetos con física real (la canoa, las piedras deslizantes) se
  mueven en `FixedUpdate` a 50Hz fijos; sin interpolación, su posición visual da saltos notables a los
  90Hz+ de VR. Activa `Rigidbody.interpolation = Interpolate` en cada Rigidbody no-kinemático de la
  escena, **excepto el del propio jugador** (que se mueve con el mismo patrón de física, y activarle
  interpolación a él hacía que TODO el juego se sintiera a tirones, no solo la canoa).
- **`CinemachineUpdateModeFix.cs`**; la interpolación del Rigidbody no bastaba: `CinemachineBrain`
  (la cámara del juego) viene en modo `SmartUpdate`, que para un objetivo con Rigidbody elige
  automáticamente actualizarse en `FixedUpdate` también; o sea, la propia cámara solo cambia de
  posición 50 veces/seg, con o sin interpolación en el objeto que sigue. Fuerza `UpdateMethod =
  LateUpdate` (se reevalúa cada frame de render), que combinado con la interpolación de arriba sí
  produce cámara suave seguiendo un objeto físico.

## Cosas que probamos y NO funcionaron (dejadas desactivadas a propósito)

Texto de diálogo/menú detrás de geometría cercana (TextMeshPro no tiene ninguna propiedad de ZTest
expuesta, y el shader "Distance Field Overlay" que la ignora no está incluido en este build). Probamos
4 enfoques, todos fallidos, código dejado en el repo comentado/inerte por si se retoma:

- **`RenderEyeOverlayFix.cs`**; re-renderizar la cámara de cada ojo una segunda vez (solo la capa de UI,
  con el buffer de profundidad limpio) para dibujar el texto siempre encima. Probamos 3 variantes
  (doble-submit al compositor de OpenVR, `ClearFlags.Depth`, `GL.Clear` manual + `ClearFlags.Nothing`):
  la primera colgaba la vista VR en cada cambio de escena; las otras dos dejaban la pantalla entera en
  amarillo sólido. Las cámaras de ojo de UnityVRMod están deshabilitadas y se renderizan a mano con
  `Camera.Render()` justo antes de un único `Submit()` al compositor; una segunda llamada a `Render()`
  ahí parece ser, en sí misma, incompatible con este pipeline.
- **`TransparentDepthClearFix.cs`**; en vez de un segundo `Render()`, añadir un `CommandBuffer` a las
  cámaras del ojo (limpia profundidad justo antes de la cola "transparente", donde cae la UI) para que
  se ejecute como parte de SU ÚNICA pasada normal. Confirmado con una prueba de color magenta que el
  `CommandBuffer` simplemente nunca se ejecuta: esas cámaras están fuera del bucle normal de renderizado
  de Unity del que depende `AddCommandBuffer`/`CameraEvent`.

Si algún día se quiere retomar, la pista más prometedora sin tocar el pipeline de render sería suavizar
manualmente la lectura en `CameraFollowFix.cs` (interpolar nosotros la posición leída) en vez de tocar
Rigidbody/Cinemachine; pero no se ha probado.

## Archivos muertos (no se cargan, no hacen nada)

- **`DialogueAdvanceFix.cs`**; quedó sin usar tras quitar por completo el soporte de mandos/láser (el
  usuario pidió eliminarlo entero y volver a teclado/ratón + puntero de mirada). Compila pero nadie lo
  llama desde `Plugin.cs`.

## Hotkeys de diagnóstico (`Plugin.cs`, activas en todo momento)

- **F3**; vuelca todos los `VideoPlayer` de la escena (modo, textura, si se ven en algún renderer).
- **F4**; vuelca los `Renderer` a menos de 5m del rig VR (mesh, material, shader).
- **F5**; fuerza `GameManager.IsInDialogue(false)` a mano, por si el flag se queda pillado.
- **F6**; vuelca el estado del `FirstPersonController` (puede moverse/mirar) y si el juego cree que hay
  un diálogo activo.
- **F8**; vuelca todos los `Canvas` (modo, tamaño, si tienen texto/RawImage, textura de esas RawImage).
- **F9**; vuelca todas las `Camera` de la escena (cuál es `Camera.main`, si tienen `CinemachineBrain`).

## Config del mod (`BepInEx\config\com.newunitymodder.unityvrmod.cfg`)

- `VR World Scale`; 1 = normal; súbelo si el mundo se siente diminuto, bájalo si se siente gigante.
- `User Eye Height Offset`; ajuste de altura de ojos en metros.
- `Asserted Camera Overrides`; si UnityVRMod detecta la cámara equivocada, fuerza manualmente qué
  GameObject/cámara usar. Formato: `NombreEscena|Ruta/A/La/Camara;`. Hoy mismo se usa `MainCamera` global.
- `Scene-Specific Pose Overrides`; posición/rotación inicial del rig VR por escena.
- `Safe Mode Level`; en `FullVrReinitOnToggle` (recomendado para OpenVR, evita sesiones colgadas al
  activar/desactivar VR a mano).
- `Automatic Safe Mode Duration`; cuánto se desactiva el render VR en cada cambio de escena. Trade-off:
  - **0.2** (valor actual) = más estable. Con 0.1 tuvimos varios crashes duros y silenciosos (sin
    excepción de C#, el log de BepInEx simplemente se corta) justo en transiciones de escena.
  - **0.1** = transición más rápida/menos intrusiva, pero más riesgo de crash en el cambio de escena.
  - Importante: los crashes vistos con 0.1 resultaron ser del **driver de la gráfica AMD**
    (excepción de Windows `0xc0000005`, módulo "unknown", stack trace repetido byte a byte dentro de
    `amdxx64.dll`, visible en `%LOCALAPPDATA%\Temp\DesbordeGames\HOPECORE\Crashes\`), no de este mod ni
    de UnityVRMod. Antes de descartar 0.1 del todo, merece la pena actualizar/reinstalar el driver de
    AMD y desactivar el overlay de Radeon Software (una causa muy común de crashes así en juegos que
    renderizan manualmente a texturas, como hace este mod para VR).

## Cómo probarlo con el visor

1. Abre SteamVR primero (headset conectado y encendido).
2. Lanza el juego con `-force-d3d11` (opciones de lanzamiento de Steam, o
   `HOPECORE.exe -force-d3d11` directamente).
3. Revisa `BepInEx\LogOutput.log`; deberías ver
   `[VRModCore] Unity VR Mod 0.1.0 (Mono) fully initialized.` y las líneas `[UnityVRMod Debug-Hotkey Fix]
   [...] Patched ...` de cada arreglo de arriba.
4. El mod arranca en Safe Mode. Usa el toggle de Safe Mode del propio UnityVRMod para activar el
   renderizado estéreo/head-tracking (y para volver a Safe Mode si algo va mal).

## Compilar y desplegar tras un cambio

```
cd mod\UnityVRModFix
dotnet build -c Release
copy bin\Release\UnityVRModFix.dll ..\..\BepInEx\plugins\UnityVRModFix\UnityVRModFix.dll
```

Reiniciar el juego para que cargue el DLL nuevo (BepInEx no hace hot-reload de plugins).
