﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.TerrainFeatures;
using TaintedCellar.Framework;
using xTile;
using xTile.Dimensions;
using xTile.Layers;
using xTile.ObjectModel;
using xTile.Tiles;
using Tile = TaintedCellar.Framework.Tile;

namespace TaintedCellar
{
    /// <summary>The mod entry class loaded by SMAPI.</summary>
    public class ModEntry : Mod
    {
        /*********
        ** Fields
        *********/
        private readonly string MapAssetKey = "assets/TaintedCellarMap.tbin";
        private string SaveDataPath => Path.Combine(this.Helper.DirectoryPath, "pslocationdata", $"{Constants.SaveFolderName}.xml");
        private CellarConfig Config;
        private readonly XmlSerializer LocationSerializer = new XmlSerializer(typeof(GameLocation));
        private GameLocation TaintedCellar;


        /*********
        ** Public methods
        *********/
        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {
            this.Config = helper.ReadConfig<CellarConfig>();
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.Saving += this.OnSaving;
            helper.Events.GameLoop.Saved += this.OnSaved;
            helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Raised after the player loads a save slot and the world is initialised.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            if (this.Config.OnlyUnlockAfterFinalHouseUpgrade && Game1.player.HouseUpgradeLevel < 3)
                return;

            try
            {
                var map = this.Helper.Content.Load<Map>(this.MapAssetKey); // initialise map
            }
            catch (Exception ex)
            {
                this.UnloadMod();
                this.Monitor.Log(ex.Message, LogLevel.Error);
                this.Monitor.Log($"Unable to load map file '{this.MapAssetKey}', unloading mod. Please try re-installing the mod.", LogLevel.Alert);
                return;
            }

            TaintedCellar = this.Load();

            Game1.locations.Add(TaintedCellar);
            this.PatchMap(Game1.getFarm());
        }

        /// <summary>Raised before the game begins writes data to the save file (except the initial save creation).</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void OnSaving(object sender, SavingEventArgs e)
        {
            this.Save();
            Game1.locations.Remove(TaintedCellar);
        }

        /// <summary>Raised after the game finishes writing data to the save file (except the initial save creation).</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void OnSaved(object sender, SavedEventArgs e)
        {
            Game1.locations.Add(TaintedCellar);
        }

        /// <summary>Raised after the game returns to the title screen.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            TaintedCellar = null;
        }

        private void Save()
        {
            string path = Path.Combine(this.Helper.DirectoryPath, "pslocationdata", $"{Constants.SaveFolderName}.xml");

            Directory.CreateDirectory(Path.GetDirectoryName(path));

            using (var writer = XmlWriter.Create(path))
            {
                LocationSerializer.Serialize(writer, TaintedCellar);
            }
            //monitor.Log($"Object serialized to {path}");
        }

        /// <summary>Load the in-game location.</summary>
        private GameLocation Load()
        {
            var location = new GameLocation(this.Helper.Content.GetActualAssetKey(this.MapAssetKey), "TaintedCellarMap")
            {
                IsOutdoors = false,
                IsFarm = true
            };

            if (File.Exists(this.SaveDataPath))
            {
                GameLocation loaded;
                using (var reader = XmlReader.Create(this.SaveDataPath))
                    loaded = (GameLocation)LocationSerializer.Deserialize(reader);

                //monitor.Log($"Object deserialized from {path}");

                for (int i = loaded.characters.Count - 1; i >= 0; i--)
                {
                    if (!loaded.characters[i].DefaultPosition.Equals(Vector2.Zero))
                        loaded.characters[i].Position = loaded.characters[i].DefaultPosition;
                    loaded.characters[i].currentLocation = location
;
                    if (i < loaded.characters.Count)
                        loaded.characters[i].reloadSprite();
                }
                foreach (TerrainFeature current in loaded.terrainFeatures.Values)
                    current.loadSprite();

                foreach (KeyValuePair<Vector2, StardewValley.Object> current in loaded.objects.Pairs)
                {
                    current.Value.initializeLightSource(current.Key);
                    current.Value.reloadSprite();
                }

                location.characters.Set(loaded.characters);
                location.largeTerrainFeatures.Set(loaded.largeTerrainFeatures);
                location.numberOfSpawnedObjectsOnMap = loaded.numberOfSpawnedObjectsOnMap;
                foreach (var pair in loaded.objects.Pairs)
                    location.objects[pair.Key] = pair.Value;
                foreach (var pair in loaded.terrainFeatures.Pairs)
                    location.terrainFeatures[pair.Key] = pair.Value;
            }

            int entranceX = (this.Config.FlipCellarEntrance ? 69 : 57) + this.Config.XPositionOffset;
            int entranceY = 12 + this.Config.YPositionOffset;
            location.setTileProperty(3, 3, "Buildings", "Action", $"Warp {entranceX} {entranceY} Farm");

            return location;
        }

        /// Patch the farm map to add the cellar entrance.
        private void PatchMap(Farm farm)
        {
            this.Helper.Content.Load<Texture2D>(@"assets\Zpaths_objects_cellar.png");
            farm.map.AddTileSheet(new TileSheet("Zpaths_objects_cellar", farm.map, this.Helper.Content.GetActualAssetKey(@"assets\Zpaths_objects_cellar.png"), new Size(32, 68), new Size(16, 16)));
            farm.map.LoadTileSheets(Game1.mapDisplayDevice);
            if (this.Config.FlipCellarEntrance)
            {
                this.PatchMap(farm, this.GetCellarRightSideEdits());
                int entranceX = 68 + this.Config.XPositionOffset;
                int entranceY1 = 11 + this.Config.YPositionOffset;
                int entranceY2 = 12 + this.Config.YPositionOffset;
                farm.setTileProperty(entranceX, entranceY1, "Buildings", "Action", "Warp 3 4 TaintedCellarMap");
                farm.setTileProperty(entranceX, entranceY2, "Buildings", "Action", "Warp 3 4 TaintedCellarMap");
            }
            else
            {
                this.PatchMap(farm, this.GetCellarLeftSideEdits());
                int entranceX = 58 + this.Config.XPositionOffset;
                int entranceY1 = 11 + this.Config.YPositionOffset;
                int entranceY2 = 12 + this.Config.YPositionOffset;
                farm.setTileProperty(entranceX, entranceY1, "Buildings", "Action", "Warp 3 4 TaintedCellarMap");
                farm.setTileProperty(entranceX, entranceY2, "Buildings", "Action", "Warp 3 4 TaintedCellarMap");
            }
            farm.setTileProperty(68, 11, "Buildings", "Action", "Warp 3 4 TaintedCellarMap");
            farm.setTileProperty(68, 12, "Buildings", "Action", "Warp 3 4 TaintedCellarMap");

            var properties = farm.map.GetTileSheet("Zpaths_objects_cellar").Properties;
            foreach (int tileID in new[] { 1865, 1897, 1866, 1898 })
                properties.Add($"@TileIndex@{tileID}@Passable", new PropertyValue(true));
        }

        /// Get the tiles to change for the right-side cellar entrance.
        private Tile[] GetCellarRightSideEdits()
        {
            string tilesheet = "Zpaths_objects_cellar";
            int x1 = 68 + this.Config.XPositionOffset;
            int x2 = 69 + this.Config.XPositionOffset;
            int y1 = 11 + this.Config.YPositionOffset;
            int y2 = 12 + this.Config.YPositionOffset;
            return new[]
            {
                new Tile(1, x1, y1, 1864, tilesheet),
                new Tile(1, x2, y1, 1865, tilesheet),
                new Tile(1, x1, y2, 1896, tilesheet),
                new Tile(1, x2, y2, 1897, tilesheet)
            };
        }

        /// Get the tiles to change for the right-side cellar entrance.
        private Tile[] GetCellarLeftSideEdits()
        {
            string tilesheet = "Zpaths_objects_cellar";
            int x1 = 57 + this.Config.XPositionOffset;
            int x2 = 58 + this.Config.XPositionOffset;
            int y1 = 11 + this.Config.YPositionOffset;
            int y2 = 12 + this.Config.YPositionOffset;
            return new[]
            {
                new Tile(1, x1, y1, 1866, tilesheet),
                new Tile(1, x2, y1, 1867, tilesheet),
                new Tile(1, x1, y2, 1898, tilesheet),
                new Tile(1, x2, y2, 1899, tilesheet)
            };
        }

        /// Apply a set of map overrides to the farm map.
        private void PatchMap(Farm farm, Tile[] tiles)
        {
            foreach (Tile tile in tiles)
            {
                if (tile.TileIndex < 0)
                {
                    farm.removeTile(tile.X, tile.Y, farm.map.Layers[tile.LayerIndex].Id);
                    farm.waterTiles[tile.X, tile.Y] = false;

                    foreach (LargeTerrainFeature feature in farm.largeTerrainFeatures)
                    {
                        if (feature.tilePosition.X == tile.X && feature.tilePosition.Y == tile.Y)
                            farm.largeTerrainFeatures.Remove(feature);
                    }
                }
                else
                {
                    Layer layer = farm.map.Layers[tile.LayerIndex];
                    xTile.Tiles.Tile mapTile = layer.Tiles[tile.X, tile.Y];
                    if (mapTile == null || mapTile.TileSheet.Id != tile.Tilesheet)
                        layer.Tiles[tile.X, tile.Y] = new StaticTile(layer, farm.map.GetTileSheet(tile.Tilesheet), 0, tile.TileIndex);
                    else
                        farm.setMapTileIndex(tile.X, tile.Y, tile.TileIndex, layer.Id);
                }
            }
        }

        private void UnloadMod()
        {
            this.Helper.Events.GameLoop.SaveLoaded -= this.OnSaveLoaded;
            this.Helper.Events.GameLoop.Saving -= this.OnSaving;
            this.Helper.Events.GameLoop.Saved -= this.OnSaved;
            this.Helper.Events.GameLoop.ReturnedToTitle -= this.OnReturnedToTitle;
        }
    }
}