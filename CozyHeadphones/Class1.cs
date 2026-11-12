using System.Threading.Tasks;
using System;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace CozyHeadphones
{
    public sealed class ModEntry : Mod
    {
        private const string HeadphonesId = "CozyHeadphones.Headphones";
        private const string HeadphonesQualifiedId = "(O)" + HeadphonesId;
        private const string HeadphonesTexture = "Mods/CozyHeadphones/Headphones";

        private readonly MusicPlaybackController playback = new();
        private Task<string?>? folderTask;

        // Selector de carpeta: en pantalla completa exclusiva el diálogo de
        // Windows queda detrás del juego, así que pasamos a modo ventana
        // mientras está abierto y restauramos el modo original después.
        private string? previousWindowMode;
        private int pickerDelayTicks;
        private string? selectedFolder;
        private string[] tracks = Array.Empty<string>();
        private int trackIndex;

        public override void Entry(IModHelper helper)
        {
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
            helper.Events.Content.AssetRequested += OnAssetRequested;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += OnDayStarted;

            Monitor.Log(
                "Cozy Headphones se ha cargado correctamente.",
                LogLevel.Info
            );
        }

        // Define el item, su textura y la receta de fabricación.
        private void OnAssetRequested(
            object? sender,
            AssetRequestedEventArgs e)
        {
            if (e.NameWithoutLocale.IsEquivalentTo(HeadphonesTexture))
            {
                e.LoadFromModFile<Texture2D>(
                    "assets/headphones.png",
                    AssetLoadPriority.Medium);
            }
            else if (e.NameWithoutLocale.IsEquivalentTo("Data/Objects"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, StardewValley.GameData.Objects.ObjectData>().Data;

                    data[HeadphonesId] = new StardewValley.GameData.Objects.ObjectData
                    {
                        Name = HeadphonesId,
                        DisplayName = "Cozy Headphones",
                        Description = "Auriculares para escuchar tu propia música mientras trabajas en la granja. Clic derecho o K para abrir el reproductor.",
                        Type = "Basic",
                        Category = 0,
                        Price = 500,
                        Texture = HeadphonesTexture,
                        SpriteIndex = 0
                    };
                });
            }
            else if (e.NameWithoutLocale.IsEquivalentTo("Data/CraftingRecipes"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, string>().Data;

                    // Pila (787) + Barra de cobre (334) -> Cozy Headphones.
                    // Formato: ingredientes / (sin uso) / resultado / ¿gran objeto? / condición / nombre
                    data[HeadphonesId] =
                        $"787 1 334 1/Home/{HeadphonesId} 1/false/default/Cozy Headphones";
                });
            }
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            LearnRecipe();
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            LearnRecipe();
        }

        // Asegura que la receta aparezca también en partidas ya existentes.
        private static void LearnRecipe()
        {
            if (!Game1.player.craftingRecipes.ContainsKey(HeadphonesId))
                Game1.player.craftingRecipes.Add(HeadphonesId, 0);
        }

        private void OnButtonPressed(
            object? sender,
            ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady ||
                Game1.activeClickableMenu != null)
            {
                return;
            }

            // Tecla K: abre el reproductor si tenés los auriculares en el inventario.
            if (e.Button == SButton.K)
            {
                if (!HasHeadphones())
                {
                    Game1.addHUDMessage(new HUDMessage(
                        "Necesitas tener los Cozy Headphones en tu inventario.",
                        HUDMessage.error_type
                    ));

                    Helper.Input.Suppress(e.Button);
                    return;
                }

                OpenPlayerMenu();
                Helper.Input.Suppress(e.Button);
                return;
            }

            // Clic derecho (o botón de acción) con los auriculares en la mano.
            if (e.Button.IsActionButton() &&
                Context.CanPlayerMove &&
                Game1.player.CurrentItem?.QualifiedItemId == HeadphonesQualifiedId)
            {
                OpenPlayerMenu();
                Helper.Input.Suppress(e.Button);
            }
        }

        private void OpenPlayerMenu()
        {
            Game1.activeClickableMenu = new MusicPlayerMenu(this);
        }

        private void OnUpdateTicked(
            object? sender,
            UpdateTickedEventArgs e)
        {
            // Se procesa antes de comprobar el mundo para no dejar el juego
            // en modo ventana si el jugador vuelve al título con el selector abierto.
            if (pickerDelayTicks > 0 && --pickerDelayTicks == 0)
                LaunchFolderPicker();

            if (folderTask is { IsCompleted: true } task)
            {
                folderTask = null;
                RestoreWindowMode();

                try
                {
                    string? folder = task.GetAwaiter().GetResult();

                    if (!string.IsNullOrWhiteSpace(folder))
                        SetFolder(folder);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Error al seleccionar carpeta: {ex}",
                        LogLevel.Error);

                    Game1.addHUDMessage(new HUDMessage(
                        "No se pudo abrir la carpeta.",
                        HUDMessage.error_type));
                }
            }

            if (!Context.IsWorldReady)
                return;

            if (!HasHeadphones())
            {
                if (playback.IsActive)
                    playback.Stop();

                return;
            }

            if (playback.HasEnded && tracks.Length > 0)
                MoveTrack(1, showErrors: false);
        }

        private void OnReturnedToTitle(
            object? sender,
            ReturnedToTitleEventArgs e)
        {
            playback.Stop();
        }

        private bool HasHeadphones()
        {
            return Context.IsWorldReady &&
                Game1.player.Items.Any(
                    item => item?.QualifiedItemId ==
                        HeadphonesQualifiedId
                );
        }

        private void SetFolder(string folder)
        {
            selectedFolder = folder;

            tracks = Directory.GetFiles(
                folder,
                "*.*",
                SearchOption.TopDirectoryOnly
            )
            .Where(file =>
                file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(
                file => Path.GetFileName(file),
                StringComparer.CurrentCultureIgnoreCase
            )
            .ToArray();

            trackIndex = 0;
            playback.Stop();

            Monitor.Log(
                $"Carpeta seleccionada: {folder}. " +
                $"Canciones encontradas: {tracks.Length}.",
                LogLevel.Info
            );

            if (tracks.Length == 0)
            {
                Game1.addHUDMessage(new HUDMessage(
                    "No encontré archivos MP3, OGG o WAV en esa carpeta.",
                    HUDMessage.error_type
                ));
            }
            else
            {
                Game1.addHUDMessage(new HUDMessage(
                    $"Se encontraron {tracks.Length} canciones.",
                    HUDMessage.newQuest_type
                ));
            }
        }

        private string CurrentTrackName =>
            tracks.Length == 0
                ? "No hay canciones cargadas"
                : Path.GetFileNameWithoutExtension(tracks[trackIndex]);

        private void PlayCurrentTrack()
        {
            if (!HasHeadphones())
            {
                Game1.addHUDMessage(new HUDMessage(
                    "Necesitas tener los Cozy Headphones en tu inventario.",
                    HUDMessage.error_type
                ));
                return;
            }

            if (tracks.Length == 0)
            {
                Game1.addHUDMessage(new HUDMessage(
                    "Primero elige una carpeta con música.",
                    HUDMessage.error_type
                ));
                return;
            }

            try
            {
                playback.Play(tracks[trackIndex]);

                Monitor.Log(
                    $"Reproduciendo: {tracks[trackIndex]}",
                    LogLevel.Info
                );
            }
            catch (Exception ex)
            {
                Monitor.Log(
                    $"No se pudo reproducir la canción: {ex}",
                    LogLevel.Error
                );

                Game1.addHUDMessage(new HUDMessage(
                    "No se pudo reproducir el archivo. Comprueba que sea válido.",
                    HUDMessage.error_type
                ));
            }
        }

        private void TogglePlayback()
        {
            if (playback.IsPaused)
                playback.Resume();
            else if (playback.IsPlaying)
                playback.Pause();
            else
                PlayCurrentTrack();
        }

        private void MoveTrack(int offset, bool showErrors = true)
        {
            if (tracks.Length == 0)
            {
                if (showErrors)
                {
                    Game1.addHUDMessage(new HUDMessage(
                        "Primero elige una carpeta con música.",
                        HUDMessage.error_type
                    ));
                }

                return;
            }

            trackIndex = (trackIndex + offset + tracks.Length) % tracks.Length;
            PlayCurrentTrack();
        }

        private void ChooseMusicFolder()
        {
            if (pickerDelayTicks > 0 ||
                (folderTask != null && !folderTask.IsCompleted))
                return;

            // El diálogo no se ve sobre el juego en pantalla completa:
            // salimos a modo ventana y abrimos el selector unos ticks después.
            if (SwitchToWindowedMode())
                pickerDelayTicks = 15;
            else
                LaunchFolderPicker();
        }

        private bool SwitchToWindowedMode()
        {
            try
            {
                if (previousWindowMode != null)
                    return true;

                if (Game1.options.isCurrentlyFullscreen())
                    previousWindowMode = "Fullscreen";
                else if (Game1.options.isCurrentlyWindowedBorderless())
                    previousWindowMode = "Windowed Borderless";
                else
                    return false; // ya está en ventana

                Game1.options.setWindowedOption("Windowed");
                return true;
            }
            catch (Exception ex)
            {
                Monitor.Log($"No se pudo cambiar a modo ventana: {ex}",
                    LogLevel.Warn);
                previousWindowMode = null;
                return false;
            }
        }

        private void RestoreWindowMode()
        {
            if (previousWindowMode == null)
                return;

            string mode = previousWindowMode;
            previousWindowMode = null;

            try
            {
                Game1.options.setWindowedOption(mode);
            }
            catch (Exception ex)
            {
                Monitor.Log($"No se pudo restaurar el modo de pantalla: {ex}",
                    LogLevel.Warn);
            }
        }

        private void LaunchFolderPicker()
        {
            try
            {
                // Con el juego en ventana el diálogo se ve; si no hay handle
                // válido se usa el de la ventana en primer plano.
                IntPtr owner = Game1.game1.Window.Handle;

                Monitor.Log("Abriendo selector de carpeta.", LogLevel.Debug);

                folderTask = FolderPicker.ChooseFolderAsync(owner);
            }
            catch (Exception ex)
            {
                RestoreWindowMode();

                Monitor.Log($"Error al abrir el selector: {ex}",
                    LogLevel.Error);

                Game1.addHUDMessage(new HUDMessage(
                    "No se pudo abrir el selector.",
                    HUDMessage.error_type));
            }
        }

        public class MusicPlayerMenu : IClickableMenu
        {
            private readonly ModEntry mod;

            private Rectangle closeButton;
            private Rectangle panel;
            private Rectangle chooseFolderButton;
            private Rectangle previousButton;
            private Rectangle playButton;
            private Rectangle pauseButton;
            private Rectangle stopButton;
            private Rectangle nextButton;

            public MusicPlayerMenu(ModEntry mod)
                : base(
                    (Game1.uiViewport.Width - 760) / 2,
                    (Game1.uiViewport.Height - 500) / 2,
                    760,
                    500)
            {
                this.mod = mod;

                UpdateLayout();
            }

            // Recalcula posición y botones; hace falta al cambiar el tamaño
            // de la ventana (por ejemplo al volver a pantalla completa).
            private void UpdateLayout()
            {
                xPositionOnScreen = (Game1.uiViewport.Width - 760) / 2;
                yPositionOnScreen = (Game1.uiViewport.Height - 500) / 2;
                width = 760;
                height = 500;


                closeButton = new Rectangle(
                    xPositionOnScreen + 690,
                    yPositionOnScreen + 25,
                    38,
                    38
                );

                panel = new Rectangle(
                    xPositionOnScreen + 45,
                    yPositionOnScreen + 100,
                    670,
                    210
                );

                chooseFolderButton = new Rectangle(
                    xPositionOnScreen + 165,
                    yPositionOnScreen + 135,
                    430,
                    55
                );

                previousButton = new Rectangle(
                    xPositionOnScreen + 55,
                    yPositionOnScreen + 345,
                    145,
                    52
                );

                playButton = new Rectangle(
                    xPositionOnScreen + 210,
                    yPositionOnScreen + 345,
                    145,
                    52
                );

                pauseButton = new Rectangle(
                    xPositionOnScreen + 365,
                    yPositionOnScreen + 345,
                    145,
                    52
                );

                nextButton = new Rectangle(
                    xPositionOnScreen + 520,
                    yPositionOnScreen + 345,
                    185,
                    52
                );

                stopButton = new Rectangle(
                    xPositionOnScreen + 275,
                    yPositionOnScreen + 415,
                    210,
                    42
                );
            
            }

            public override void gameWindowSizeChanged(
                Rectangle oldBounds,
                Rectangle newBounds)
            {
                base.gameWindowSizeChanged(oldBounds, newBounds);
                UpdateLayout();
            }

            public override void receiveLeftClick(
                int x,
                int y,
                bool playSound = true)
            {
                if (closeButton.Contains(x, y))
                {
                    Game1.exitActiveMenu();
                    return;
                }

                if (chooseFolderButton.Contains(x, y))
                {
                    mod.ChooseMusicFolder();
                    return;
                }

                if (previousButton.Contains(x, y))
                {
                    mod.MoveTrack(-1);
                    return;
                }

                if (playButton.Contains(x, y))
                {
                    if (mod.playback.IsPaused)
                        mod.playback.Resume();
                    else
                        mod.PlayCurrentTrack();

                    return;
                }

                if (pauseButton.Contains(x, y))
                {
                    mod.TogglePlayback();
                    return;
                }

                if (nextButton.Contains(x, y))
                {
                    mod.MoveTrack(1);
                    return;
                }

                if (stopButton.Contains(x, y))
                {
                    mod.playback.Stop();
                    Game1.playSound("smallSelect");
                    return;
                }

                base.receiveLeftClick(x, y, playSound);
            }

            public override void draw(SpriteBatch b)
            {
                Game1.drawDialogueBox(
                    xPositionOnScreen,
                    yPositionOnScreen,
                    width,
                    height,
                    false,
                    true
                );

                DrawCenteredText(
                    b,
                    "Cozy Headphones",
                    Game1.dialogueFont,
                    yPositionOnScreen + 35,
                    Game1.textColor
                );

                DrawPanel(b, panel);

                DrawButton(
                    b,
                    chooseFolderButton,
                    "Elegir carpeta de música",
                    true
                );

                string folderName = mod.selectedFolder == null
                    ? "Todavía no seleccionada"
                    : new DirectoryInfo(mod.selectedFolder).Name;

                DrawCenteredText(
                    b,
                    folderName,
                    Game1.smallFont,
                    yPositionOnScreen + 205,
                    Game1.textColor
                );

                string trackText = mod.tracks.Length == 0
                    ? "Sin canciones"
                    : $"{mod.trackIndex + 1}/{mod.tracks.Length} · " +
                    mod.CurrentTrackName;

                trackText = trackText.Length > 42
                    ? trackText.Substring(0, 39) + "..."
                    : trackText;

                DrawCenteredText(
                    b,
                    trackText,
                    Game1.smallFont,
                    yPositionOnScreen + 255,
                    Game1.textColor
                );

                DrawButton(b, previousButton, "Anterior", false);

                DrawButton(
                    b,
                    playButton,
                    mod.playback.IsPaused ? "Reanudar" : "Reproducir",
                    false
                );

                DrawButton(
                    b,
                    pauseButton,
                    "Pausar",
                    false
                );

                DrawButton(b, nextButton, "Siguiente", false);
                DrawButton(b, stopButton, "Detener música", false);
                DrawButton(b, closeButton, "X", false);

                drawMouse(b);
            }

            private void DrawButton(
                SpriteBatch b,
                Rectangle bounds,
                string label,
                bool selected)
            {
                DrawPanel(
                    b,
                    bounds,
                    selected
                        ? new Color(226, 177, 83)
                        : new Color(244, 225, 184)
                );

                Vector2 measured = Game1.smallFont.MeasureString(label);

                float scale = Math.Min(
                    1f,
                    (bounds.Width - 16f) / measured.X
                );

                Vector2 size = measured * scale;

                Vector2 position = new(
                    bounds.X + (bounds.Width - size.X) / 2f,
                    bounds.Y + (bounds.Height - size.Y) / 2f
                );

                b.DrawString(
                    Game1.smallFont,
                    label,
                    position,
                    Game1.textColor,
                    0f,
                    Vector2.Zero,
                    scale,
                    SpriteEffects.None,
                    1f
                );
            }

            private void DrawCenteredText(
                SpriteBatch b,
                string text,
                SpriteFont font,
                float y,
                Color color)
            {
                Vector2 size = font.MeasureString(text);

                float scale = Math.Min(
                    1f,
                    (width - 70f) / Math.Max(size.X, 1f)
                );

                size *= scale;

                b.DrawString(
                    font,
                    text,
                    new Vector2(
                        xPositionOnScreen + (width - size.X) / 2f,
                        y
                    ),
                    color,
                    0f,
                    Vector2.Zero,
                    scale,
                    SpriteEffects.None,
                    1f
                );
            }

            private void DrawPanel(
                SpriteBatch b,
                Rectangle bounds,
                Color? fill = null)
            {
                b.Draw(
                    Game1.staminaRect,
                    bounds,
                    new Color(111, 73, 48)
                );

                b.Draw(
                    Game1.staminaRect,
                    new Rectangle(
                        bounds.X + 3,
                        bounds.Y + 3,
                        bounds.Width - 6,
                        bounds.Height - 6
                    ),
                    fill ?? new Color(232, 211, 165)
                );
            }

            public override void receiveKeyPress(Keys key)
            {
                if (key == Keys.Escape)
                {
                    Game1.exitActiveMenu();
                    return;
                }

                base.receiveKeyPress(key);
            }
        }
    }
}
