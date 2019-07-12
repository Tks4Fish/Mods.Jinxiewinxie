﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using WonderfulFarmLife.Framework;
using WonderfulFarmLife.Framework.Config;
using WonderfulFarmLife.Framework.Constants;
using xTile.Dimensions;
using xTile.Layers;
using xTile.ObjectModel;
using xTile.Tiles;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using SObject = StardewValley.Object;

namespace WonderfulFarmLife
{
    /// <summary>The main entry class for the mod.</summary>
    internal class ModEntry : Mod
    {
        /*********
        ** Properties
        *********/
        /// <summary>Whether the player filled the pet bowls today.</summary>
        private bool PetBowlsFilled;

        /// <summary>The mod configuration.</summary>
        private ModConfig Config;

        /// <summary>The map overrides to apply.</summary>
        private DataModel LayoutConfig;

        /// <summary>The default tilesheet for tile overrides that don't specify one.</summary>
        private string DefaultTilesheet;


        /*********
        ** Public methods
        *********/
        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {
            // read config
            this.Config = helper.ReadConfig<ModConfig>();
            this.LayoutConfig = helper.Data.ReadJsonFile<DataModel>("data.json");
            if (!this.LayoutConfig.Tilesheets.ContainsKey("default"))
                throw new KeyNotFoundException("The required 'default' tilesheet isn't specified in data.json.");
            this.DefaultTilesheet = this.LayoutConfig.Tilesheets["default"];

            // hook up events
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            helper.Events.Player.Warped += this.OnWarped;
        }


        /*********
        ** Private methods
        *********/
        /****
        ** Events
        ****/
        /// <summary>Raised after the player loads a save slot and the world is initialised.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            this.ApplyMapOverrides();
        }

        /// <summary>Raised after a player warps to a new location.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnWarped(object sender, WarpedEventArgs e)
        {
            if (e.IsLocalPlayer && e.NewLocation is Farm farm)
                this.PrepareMapForRendering(farm);
        }

        /// <summary>Raised after the game begins a new day (including when the player loads a save).</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            this.UpdatePetBowlsForNewDay();
        }

        /// <summary>Raised after the player presses a button on the keyboard, controller, or mouse.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            switch (e.Button)
            {
                case SButton.MouseRight:
                case SButton.ControllerA:
                    this.TryAction();
                    break;

                case SButton.MouseLeft:
                case SButton.ControllerX:
                    if (!this.TryFillPetBowls(Game1.getFarm(), this.GetActionCursor()))
                        this.TryAction();
                    break;
            }
        }

        /****
        ** Methods
        ****/
        /// <summary>Apply the map overrides.</summary>
        private void ApplyMapOverrides()
        {
            // get farm data
            Farm farm = Game1.getFarm();
            FarmType farmType = (FarmType)Game1.whichFarm;

            // get layouts
            if (!this.LayoutConfig.Layouts.ContainsKey(farmType))
            {
                this.Monitor.Log($"The {farmType} farm isn't supported by the mod.", LogLevel.Warn);
                return;
            }
            LayoutConfig[] layouts = this.LayoutConfig
                .Layouts[farmType]
                .Where(p => p.ConfigFlag == null || this.GetConfigFlag(p.ConfigFlag))
                .ToArray();

            // resize tilesheet
            TileSheet tileSheet = farm.map.GetTileSheet(FarmTilesheet.Outdoors);
            tileSheet.SheetSize = new Size(tileSheet.SheetSize.Width, tileSheet.SheetSize.Height + 44);

            // apply layouts
            foreach (LayoutConfig layout in layouts)
            {
                // override tiles
                if (layout.Tiles != null)
                    this.Apply(farm, layout.Tiles.SelectMany(p => p.GetOverrides(this.LayoutConfig.Tilesheets, this.DefaultTilesheet)));

                // tile properties
                if (layout.TileProperties != null)
                {
                    foreach (TileProperty property in layout.TileProperties.SelectMany(p => p.GetProperties(this.LayoutConfig.Tilesheets, this.DefaultTilesheet)))
                        farm.setTileProperty(property.X, property.Y, property.Layer.ToString(), property.Key, property.Value);
                }

                // tilesheet properties
                if (layout.TileIndexProperties != null)
                {
                    foreach (TileIndexProperty property in layout.TileIndexProperties.SelectMany(p => p.GetProperties(this.LayoutConfig.Tilesheets, this.DefaultTilesheet)))
                        farm.map.GetTileSheet(property.Tilesheet).Properties.Add(property.Key, new PropertyValue(property.Value));
                }
            }
        }

        /// <summary>Prepare the farm map before it begins rendering.</summary>
        /// <param name="farm">The farm map to prepare.</param>
        private void PrepareMapForRendering(Farm farm)
        {
            // remove shipping bin sprite
            if (this.Config.RemoveShippingBin)
                this.Helper.Reflection.GetField<TemporaryAnimatedSprite>(farm, "shippingBinLid").SetValue(null);

            // patch tilesheet before draw
            TileSheet tileSheet = farm.map.GetTileSheet(FarmTilesheet.Outdoors);
            var sheetTextures = this.Helper.Reflection.GetField<Dictionary<TileSheet, Texture2D>>(Game1.mapDisplayDevice, "m_tileSheetTextures").GetValue();
            Texture2D targetTexture = sheetTextures[tileSheet];
            Dictionary<int, int> spriteOverrides = new Dictionary<int, int>();
            for (int key = 0; key < 1100; ++key)
                spriteOverrides.Add(key, 1975 + key);
            if (targetTexture != null)
                sheetTextures[tileSheet] = this.PatchTexture(targetTexture, $"{Game1.currentSeason}_wonderful.png", spriteOverrides, 16, 16);
        }

        /// <summary>Apply the happiness bonus for filling pets' watering bowls, and reset the bowls.</summary>
        private void UpdatePetBowlsForNewDay()
        {
            if (!this.PetBowlsFilled)
                return;

            // get pets
            Pet[] pets = this.GetPets().ToArray();
            if (!pets.Any())
                return;

            // apply happiness bonus
            foreach (Pet pet in pets)
                pet.friendshipTowardFarmer = Math.Min(Pet.maxFriendship, pet.friendshipTowardFarmer + 6);

            // empty bowls
            Farm farm = Game1.getFarm();
            farm.setMapTileIndex(52, 7, 2201, "Buildings");
            farm.setMapTileIndex(53, 7, 2202, "Buildings");
            this.PetBowlsFilled = false;
        }

        /// <summary>Get the value of a config flag.</summary>
        /// <param name="name">The name of the config flag.</param>
        private bool GetConfigFlag(string name)
        {
            // get property
            PropertyInfo property = this.Config
                .GetType()
                .GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            // validate property
            if (property == null)
            {
                this.Monitor.Log($"The '{name}' config setting doesn't exist, assuming false.", LogLevel.Warn);
                return false;
            }
            if (property.PropertyType != typeof(bool))
            {
                this.Monitor.Log($"The '{name}' config setting isn't a bool flag, assuming false.", LogLevel.Warn);
                return false;
            }

            // check flag value
            return (bool)property.GetValue(this.Config);
        }

        /// <summary>Get the tile under the cursor or grab tile.</summary>
        private Vector2 GetActionCursor()
        {
            Vector2 cursorPos = Game1.currentCursorTile;
            return Utility.tileWithinRadiusOfPlayer((int)cursorPos.X, (int)cursorPos.Y, 1, Game1.player)
                ? cursorPos
                : Game1.player.GetGrabTile();
        }

        /// <summary>Trigger the action for the tile under the cursor, if applicable.</summary>
        private bool TryAction()
        {
            if (Game1.numberOfSelectedItems != -1 || Game1.activeClickableMenu != null)
                return false;

            // get tile under cursor
            Vector2 actionPos = this.GetActionCursor();
            Tile actionTile = Game1.currentLocation.map.GetLayer("Buildings").Tiles[(int)actionPos.X, (int)actionPos.Y];
            if (actionTile == null)
                return false;

            // get tile action
            actionTile.Properties.TryGetValue("Action", out PropertyValue propertyValue);
            if (propertyValue == null)
                return false;
            string action = propertyValue.ToString();

            // handle action
            switch (action)
            {
                case "NewShippingBin":
                    {
                        ItemGrabMenu itemGrabMenu = new ItemGrabMenu(null, true, false, Utility.highlightShippableObjects, this.ShipItem, "", null, true, true, false);
                        itemGrabMenu.initializeUpperRightCloseButton();
                        itemGrabMenu.setBackgroundTransparency(false);
                        itemGrabMenu.setDestroyItemOnClick(true);
                        itemGrabMenu.initializeShippingBin();
                        Game1.activeClickableMenu = itemGrabMenu;
                        Game1.playSound("shwip");
                        if (Game1.player.facingDirection == 1)
                            Game1.player.Halt();
                        Game1.player.showCarrying();
                        return true;
                    }

                case "TelescopeMessage":
                    {
                        Random random = new Random();
                        string[] messages = {
                            "I wish Neil DeGrasse Tyson was here.",
                            "I call this star mine... and that one, oh, and that one too.",
                            "Astronomy compels the soul to look upward, and leads us from this world to another.",
                            "Be glad of life, because it gives you the chance to love and to work and to play and to look up at the stars.",
                            "The sky is the ultimate art gallery just above us.",
                            "'Stop acting so small. You are the universe in estatic motion.' - Rumi",
                            "The universe doesn't give you what you ask for with your thoughts, it gives you what you demand with your actions.",
                            "The darkest nights produce the brightest stars.",
                            "'there wouldn't be a sky full of stars if we were all meant to wish on the same one.' - Frances Clark",
                            "Stars can't shine without darkness.",
                            "I have loved the stars too fondly to be fearful of the night.",
                            "I know nothing with any certainty, but the sight of the stars makes me dream."
                        };
                        Game1.drawObjectDialogue(messages[random.Next(messages.Length)]);
                        return true;
                    }
            }
            return false;
        }

        /// <summary>Fill the pet bowls if the player is holding a filled watering can and the cursor is over a bowl.</summary>
        /// <param name="farm">The current location.</param>
        /// <param name="tile">The tile being interacted with.</param>
        private bool TryFillPetBowls(Farm farm, Vector2 tile)
        {
            WateringCan wateringCan = Game1.player.CurrentTool as WateringCan;
            if (wateringCan == null || wateringCan.WaterLeft <= 0)
                return false;

            // fill bowls if under cursor
            int tileID = farm.getTileIndexAt((int)tile.X, (int)tile.Y, "Buildings");
            if (tileID == 2201 || tileID == 2202)
            {
                farm.setMapTileIndex(52, 7, 2204, "Buildings");
                farm.setMapTileIndex(53, 7, 2205, "Buildings");
                this.PetBowlsFilled = true;
                return true;
            }
            return false;
        }

        /// <summary>Add an item to the shipping bin.</summary>
        /// <param name="item">The item to ship.</param>
        /// <param name="player">The player in whose inventory the item is stored.</param>
        private void ShipItem(Item item, Farmer player)
        {
            if (item == null)
                return;

            Farm farm = Game1.getFarm();
            farm.shippingBin.Add(item);
            if (item is SObject)
                DelayedAction.playSoundAfterDelay("Ship", 0);
            farm.lastItemShipped = item;
            player.removeItemFromInventory(item);
            if (Game1.player.ActiveObject == null)
            {
                Game1.player.showNotCarrying();
                Game1.player.Halt();
            }
        }

        /// <summary>Get all known pets.</summary>
        private IEnumerable<Pet> GetPets()
        {
            if (!Game1.player.hasPet())
                return null;

            return
                Game1.getFarm().characters
                .Concat(Utility.getHomeOfFarmer(Game1.player).characters)
                .OfType<Pet>();
        }

        /// <summary>Apply tile overrides to the map.</summary>
        /// <param name="location">The game location to patch.</param>
        /// <param name="tiles">The tile overrides to apply.</param>
        private void Apply(Farm location, IEnumerable<TileOverride> tiles)
        {
            foreach (TileOverride tile in tiles)
            {
                // reset tile
                if (tile.TileID == null)
                {
                    location.removeTile(tile.X, tile.Y, tile.LayerName);
                    location.waterTiles[tile.X, tile.Y] = false;

                    foreach (LargeTerrainFeature feature in location.largeTerrainFeatures)
                    {
                        if (feature.tilePosition.X == tile.X && feature.tilePosition.Y == tile.Y)
                        {
                            location.largeTerrainFeatures.Remove(feature);
                            break;
                        }
                    }
                }

                // overwrite tile
                else
                {
                    Layer layer = location.map.GetLayer(tile.LayerName);
                    var layerTile = layer.Tiles[tile.X, tile.Y];
                    if (layerTile == null || layerTile.TileSheet.Id != tile.Tilesheet)
                    {
                        var tilesheet = location.map.GetTileSheet(tile.Tilesheet);
                        layer.Tiles[tile.X, tile.Y] = new StaticTile(layer, tilesheet, BlendMode.Alpha, tile.TileID.Value);
                    }
                    else
                        location.setMapTileIndex(tile.X, tile.Y, tile.TileID.Value, layer.Id);
                }
            }
        }

        /// <summary>Read a tilesheet from the <c>overrides</c> folder, and write any overridden sprites onto the given texture.</summary>
        /// <param name="targetTexture">The texture to patch.</param>
        /// <param name="overridingTexturePath">The filename within the <c>assets</c> folder from which to read the tilesheet.</param>
        /// <param name="spriteOverrides">The sprite indexes for new sprites.</param>
        /// <param name="gridWidth">The width of a tile in the tilesheet.</param>
        /// <param name="gridHeight">The height of each tile in the tilesheet.</param>
        private Texture2D PatchTexture(Texture2D targetTexture, string overridingTexturePath, Dictionary<int, int> spriteOverrides, int gridWidth, int gridHeight)
        {
            int bottom = this.GetSourceRect(spriteOverrides.Values.Max(), targetTexture, gridWidth, gridHeight).Bottom;
            if (bottom > targetTexture.Height)
            {
                Color[] targetPixels = new Color[targetTexture.Width * targetTexture.Height];
                targetTexture.GetData(targetPixels);

                Color[] newPixels = new Color[targetTexture.Width * bottom];
                Array.Copy(targetPixels, newPixels, targetPixels.Length);
                targetTexture = new Texture2D(Game1.graphics.GraphicsDevice, targetTexture.Width, bottom);
                targetTexture.SetData(newPixels);
            }

            using (FileStream fileStream = File.Open(Path.Combine(this.Helper.DirectoryPath, "assets", overridingTexturePath), FileMode.Open))
            {
                Texture2D texture = Texture2D.FromStream(Game1.graphics.GraphicsDevice, fileStream);
                foreach (KeyValuePair<int, int> spriteOverride in spriteOverrides)
                {
                    Color[] data = new Color[gridWidth * gridHeight];
                    texture.GetData(0, this.GetSourceRect(spriteOverride.Key, texture, gridWidth, gridHeight), data, 0, data.Length);
                    targetTexture.SetData(0, this.GetSourceRect(spriteOverride.Value, targetTexture, gridWidth, gridHeight), data, 0, data.Length);
                }
            }
            return targetTexture;
        }

        /// <summary>Get a sprite area on the farm tilesheet.</summary>
        /// <param name="index">The tile index in the tilesheet.</param>
        /// <param name="texture">The sprite texture.</param>
        /// <param name="gridWidth">The width of a tile in the tilesheet.</param>
        /// <param name="gridHeight">The height of each tile in the tilesheet.</param>
        private Rectangle GetSourceRect(int index, Texture2D texture, int gridWidth, int gridHeight)
        {
            return new Rectangle(index % (texture.Width / gridWidth) * gridWidth, index / (texture.Width / gridWidth) * gridHeight, gridWidth, gridHeight);
        }
    }
}
