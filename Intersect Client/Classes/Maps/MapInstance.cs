﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using IntersectClientExtras.File_Management;
using IntersectClientExtras.GenericClasses;
using IntersectClientExtras.Graphics;
using Intersect_Client.Classes.Core;
using Intersect_Client.Classes.Entities;
using Intersect_Client.Classes.General;
using Intersect_Client.Classes.Items;
using Intersect_Library;
using Intersect_Library.GameObjects;
using Intersect_Library.GameObjects.Maps;
using Options = Intersect_Library.Options;

namespace Intersect_Client.Classes.Maps
{
    public class MapInstance : MapBase
    {
        //Client Only Values

        //Map State Variables
        public new const GameObject Type = GameObject.Map;
        protected static Dictionary<int, DatabaseObject> Objects = new Dictionary<int, DatabaseObject>();
        public static Dictionary<int,long> MapRequests = new Dictionary<int,long>();
        public bool MapLoaded { get; private set; }
        public bool MapRendered { get; private set; }


        //Camera Locking Variables
        public int HoldUp { get; set; }
        public int HoldDown { get; set; }
        public int HoldLeft { get; set; }
        public int HoldRight { get; set; }

        //World Position
        public int MapGridX { get; set; }
        public int MapGridY { get; set; }

        //Map Texture Caching Options
        private int _preRenderStage;
        private int _preRenderLayer;
        private bool _reRenderMap;
        public GameRenderTexture[] LowerTextures = new GameRenderTexture[3];
        public GameRenderTexture[] UpperTextures = new GameRenderTexture[3];
        public GameRenderTexture[] PeakTextures = new GameRenderTexture[3];

        //Map Items
        public Dictionary<int, MapItemInstance> MapItems = new Dictionary<int, MapItemInstance>();

        //Map Animations
        public List<MapAnimationInstance> LocalAnimations = new List<MapAnimationInstance>();

        //Map Players/Events/Npcs
        public Dictionary<int, Entity> LocalEntities = new Dictionary<int, Entity>();
        public List<int> LocalEntitiesToDispose = new List<int>();

        //Map Sounds
        public MapSound BackgroundSound { get; set; }
        public List<MapSound> AttributeSounds = new List<MapSound>();

        //Map Attributes
        private Dictionary<Intersect_Library.GameObjects.Maps.Attribute, AnimationInstance> _attributeAnimInstances = new Dictionary<Intersect_Library.GameObjects.Maps.Attribute, AnimationInstance>();

        //Fog Variables
        private long _fogUpdateTime = -1;
        private float _curFogIntensity;
        private float _fogCurrentX;
        private float _fogCurrentY;
        private float _lastFogX;
        private float _lastFogY;

        //Panorama Variables
        private long _panoramaUpdateTime = -1;
        private float _panoramaIntensity;

        //Overlay Image Variables
        private long _overlayUpdateTime = -1;
        private float _overlayIntensity;

        //Update Timer
        private long _lastUpdateTime;

        //Initialization
        public MapInstance(int mapNum) : base(mapNum, true)
        {
        }

        //LEGACY -- REMOVE WHEN SERIALIZATION IS WORKING!
        //Load
        public void Load(byte[] packet)
        {
            LocalEntitiesToDispose.AddRange(LocalEntities.Keys.ToArray());
            base.Load(packet);
            MapLoaded = true;
            Autotiles = new MapAutotiles(this);
            CreateMapSounds();
            MapRendered = false;
        }

        //Updating
        public void Update(bool isLocal)
        {
            if (isLocal)
            {
                _lastUpdateTime = Globals.System.GetTimeMS() + 10000;
                if (BackgroundSound == null && Sound != "None" && Sound != "")
                {
                    BackgroundSound = GameAudio.AddMapSound(Sound, -1, -1, MyMapNum, true, 10);
                }
                foreach (var anim in LocalAnimations)
                {
                    anim.Update();
                }
                foreach (var en in LocalEntities)
                {
                    if (en.Value == null) continue;
                    en.Value.Update();
                }
                for (int i = 0; i < LocalEntitiesToDispose.Count; i++)
                {
                    LocalEntities.Remove(LocalEntitiesToDispose[i]);
                }
                LocalEntitiesToDispose.Clear();
            }
            else
            {
                if (Globals.System.GetTimeMS() > _lastUpdateTime || GameGraphics.FreeMapTextures.Count < 27)
                {
                    Dispose();
                }
                HideActiveAnimations();
            }
        }

        public bool InView()
        {
            if (Globals.MapGridWidth == 0 || Globals.MapGridHeight == 0 || MapInstance.GetMap(Globals.Me.CurrentMap) == null) return true;
            var gridX = MapInstance.GetMap(Globals.Me.CurrentMap).MapGridX;
            var gridY = MapInstance.GetMap(Globals.Me.CurrentMap).MapGridY;
            for (int x = gridX - 1; x <= gridX + 1; x++)
            {
                for (int y = gridY - 1; y <= gridY + 1; y++)
                {
                    if (x >= 0 && x < Globals.MapGridWidth && y >= 0 && y < Globals.MapGridHeight)
                    {
                        if (Globals.MapGrid[x, y] == MyMapNum) return true;
                    }
                }
            }
            return false;
        }

        //Helper Functions
        public MapBase[,] GenerateAutotileGrid()
        {
            MapBase[,] mapBase = new MapBase[3, 3];
            if (Globals.MapGrid != null)
            {
                for (int x = -1; x <= 1; x++)
                {
                    for (int y = -1; y <= 1; y++)
                    {
                        var x1 = MapGridX + x;
                        var y1 = MapGridY + y;
                        if (x1 >= 0 && y1 >= 0 && x1 < Globals.MapGridWidth && y1 < Globals.MapGridHeight)
                        {
                            if (x == 0 && y == 0)
                            {
                                mapBase[x + 1, y + 1] = this;
                            }
                            else
                            {
                                mapBase[x + 1, y + 1] = MapInstance.GetMap(Globals.MapGrid[x1, y1]);
                            }
                        }
                    }
                }
            }
            return mapBase;
        }

        //Retreives the X Position of the Left side of the map in world space.
        public float GetX()
        {
            return MapGridX*Options.MapWidth*Options.TileWidth;
        }

        //Retreives the Y Position of the Top side of the map in world space.
        public float GetY()
        {
            return MapGridY*Options.MapHeight*Options.TileHeight;
        }

        //Attribute References
        private void UpdateMapAttributes()
        {
            for (int x = 0; x < Options.MapWidth; x++)
            {
                for (int y = 0; y < Options.MapHeight; y++)
                {
                    if (Attributes[x, y] != null)
                    {
                        if (Attributes[x, y].value == (int) MapAttributes.Animation)
                        {
                            var anim = AnimationBase.GetAnim(Attributes[x, y].data1);
                            if (anim != null)
                            {
                                if (!_attributeAnimInstances.ContainsKey(Attributes[x, y]))
                                {
                                    var animInstance = new AnimationInstance(anim, true);
                                    animInstance.SetPosition(GetX() + x*Options.TileWidth + Options.TileWidth/2, GetY() + y*Options.TileHeight + Options.TileHeight/2, 0);
                                    _attributeAnimInstances.Add(Attributes[x, y], animInstance);
                                }
                                _attributeAnimInstances[Attributes[x, y]].Update();
                            }
                        }
                    }
                }
            }
        }

        private void ClearMapAttributes()
        {
            foreach (var attributeInstance in _attributeAnimInstances)
            {
                attributeInstance.Value.Dispose();
            }
            _attributeAnimInstances.Clear();
        }

        //Sound Functions
        private void CreateMapSounds()
        {
            ClearAttributeSounds();
            for (int x = 0; x < Options.MapWidth; x++)
            {
                for (int y = 0; y < Options.MapHeight; y++)
                {
                    if (Attributes[x, y] != null)
                    {
                        if (Attributes[x, y].value == (int) MapAttributes.Sound)
                        {
                            if (Attributes[x, y].data4 != "None" && Attributes[x, y].data4 != "")
                            {
                                AttributeSounds.Add(GameAudio.AddMapSound(Attributes[x, y].data4, x, y, MyMapNum, true, Attributes[x, y].data1));
                            }
                        }
                    }
                }
            }
        }

        private void ClearAttributeSounds()
        {
            for (int i = 0; i < AttributeSounds.Count; i++)
            {
                GameAudio.StopSound(AttributeSounds[i]);
            }
            AttributeSounds.Clear();
        }

        //Animations
        public void AddTileAnimation(int animNum, int tileX, int tileY, int dir = -1)
        {
            var animBase = AnimationBase.GetAnim(animNum);
            if (animBase != null)
            {
                var anim = new MapAnimationInstance(animBase, tileX, tileY, dir);
                LocalAnimations.Add(anim);
                anim.SetPosition(GetX() + tileX*Options.TileWidth + Options.TileWidth/2, GetY() + tileY*Options.TileHeight + Options.TileHeight/2, dir);
            }
        }

        private void HideActiveAnimations()
        {
            foreach (Entity en in LocalEntities.Values)
            {
                en.ClearAnimations();
            }
            foreach (MapAnimationInstance anim in LocalAnimations)
            {
                anim.Dispose();
            }
            LocalAnimations.Clear();
            ClearMapAttributes();
        }


        //Rendering/Drawing Code
        public void Draw(int layer = 0)
        {
            if (_reRenderMap)
            {
                MapRendered = false;
                ReleaseRenderTextures();
                _preRenderLayer = 0;
                _preRenderStage = 0;
                _reRenderMap = false;
            }
            if (layer == 0)
            {
                DrawPanorama();
            }
            if (!MapRendered)
            {
                if (Globals.Database.RenderCaching) return;
                switch (layer)
                {
                    case 0:
                        for (int i = 0; i < 3; i++)
                        {
                            DrawMapLayer(null, i, Globals.AnimFrame, GetX(), GetY());
                        }
                        break;

                    case 1:
                        DrawMapLayer(null, 3, Globals.AnimFrame, GetX(), GetY());
                        break;

                    case 2:
                        DrawMapLayer(null, 4, Globals.AnimFrame, GetX(), GetY());
                        break;
                }
            }
            else
            {
                if (layer == 0)
                {
                    GameGraphics.DrawGameTexture(LowerTextures[Globals.AnimFrame], GetX(), GetY());
                }
                else if (layer == 1)
                {
                    GameGraphics.DrawGameTexture(UpperTextures[Globals.AnimFrame], GetX(), GetY());
                }
                else
                {
                    GameGraphics.DrawGameTexture(PeakTextures[Globals.AnimFrame], GetX(), GetY());
                }
            }
            if (layer == 0)
            {
                //Draw Map Items
                foreach (var item in MapItems)
                {
                    var itemBase = ItemBase.GetItem(item.Value.ItemNum);
                    if (itemBase != null)
                    {
                        GameTexture itemTex = Globals.ContentManager.GetTexture(GameContentManager.TextureType.Item, itemBase.Pic);
                        if (itemTex != null)
                        {
                            GameGraphics.DrawGameTexture(itemTex, GetX() + item.Value.X*Options.TileWidth, GetY() + item.Value.Y*Options.TileHeight);
                        }
                    }
                }

                //Add lights to our darkness texture
                foreach (var light in Lights)
                {
                    double w = light.Size;
                    var x = GetX() + (light.TileX*Options.TileWidth + light.OffsetX) + Options.TileWidth/2f;
                    var y = GetY() + (light.TileY*Options.TileHeight + light.OffsetY) + Options.TileHeight/2f;
                    GameGraphics.AddLight((int) x, (int) y, (int) w, light.Intensity, light.Expand, light.Color);
                }
                UpdateMapAttributes();
            }
            if (layer == 2)
            {
                DrawFog();
                DrawOverlayGraphic();
            }
        }

        private void DrawAutoTile(int layerNum, float destX, float destY, int quarterNum, int x, int y, int forceFrame, GameRenderTexture tex)
        {
            int yOffset = 0, xOffset = 0;

            // calculate the offset
            switch (Layers[layerNum].Tiles[x, y].Autotile)
            {
                case MapAutotiles.AutotileWaterfall:
                    yOffset = (forceFrame - 1)*Options.TileHeight;
                    break;

                case MapAutotiles.AutotileAnim:
                    xOffset = forceFrame*Options.TileWidth*2;
                    break;

                case MapAutotiles.AutotileCliff:
                    yOffset = -Options.TileHeight;
                    break;
            }
            if (TilesetBase.GetTileset(Layers[layerNum].Tiles[x, y].TilesetIndex) != null)
            {
                GameTexture tileset = Globals.ContentManager.GetTexture(GameContentManager.TextureType.Tileset, TilesetBase.GetTileset(Layers[layerNum].Tiles[x, y].TilesetIndex).Value);
                GameGraphics.DrawGameTexture(tileset, destX, destY, (int) Autotiles.Autotile[x, y].Layer[layerNum].QuarterTile[quarterNum].X + xOffset, (int) Autotiles.Autotile[x, y].Layer[layerNum].QuarterTile[quarterNum].Y + yOffset, Options.TileWidth/2, Options.TileHeight/2, tex);
            }
        }

        private void DrawMapLayer(GameRenderTexture tex, int l, int z, float xoffset = 0, float yoffset = 0)
        {
            int xmin = 0;
            int xmax = Options.MapWidth;
            int ymin = 0;
            int ymax = Options.MapHeight;
            if (!Globals.Database.RenderCaching)
            {
                if (GetX() < GameGraphics.CurrentView.Left)
                {
                    xmin += (int) (Math.Floor((GameGraphics.CurrentView.Left - GetX())/32));
                }
                if (GetX() + (xmax*32) > GameGraphics.CurrentView.Left + GameGraphics.CurrentView.Width)
                {
                    xmax -= (int) (Math.Floor(((GetX() + (xmax*32)) - (GameGraphics.CurrentView.Left + GameGraphics.CurrentView.Width))/32));
                }
                if (GetY() < GameGraphics.CurrentView.Top)
                {
                    ymin += (int) (Math.Floor((GameGraphics.CurrentView.Top - GetY())/32));
                }
                if (GetY() + (ymax*32) > GameGraphics.CurrentView.Top + GameGraphics.CurrentView.Height)
                {
                    ymax -= (int) (Math.Floor(((GetY() + (ymax*32)) - (GameGraphics.CurrentView.Top + GameGraphics.CurrentView.Height))/32));
                }
                xmin -= 2;
                if (xmin < 0) xmin = 0;
                xmax += 2;
                if (xmax > Options.MapWidth) xmax = Options.MapWidth;
                ymin -= 2;
                if (ymin < 0) ymin = 0;
                ymax += 2;
                if (ymax > Options.MapHeight) ymax = Options.MapHeight;
            }
            for (var x = xmin; x < xmax; x++)
            {
                for (var y = ymin; y < ymax; y++)
                {
                    var tileset = TilesetBase.GetTileset(Layers[l].Tiles[x, y].TilesetIndex);
                    if (tileset == null) continue;
                    GameTexture tilesetTex = Globals.ContentManager.GetTexture(GameContentManager.TextureType.Tileset, tileset.Value);
                    if (tilesetTex == null) continue;
                    switch (Autotiles.Autotile[x, y].Layer[l].RenderState)
                    {
                        case MapAutotiles.RenderStateNormal:
                            GameGraphics.DrawGameTexture(tilesetTex, x*Options.TileWidth + xoffset, y*Options.TileHeight + yoffset, Layers[l].Tiles[x, y].X*Options.TileWidth, Layers[l].Tiles[x, y].Y*Options.TileHeight, Options.TileWidth, Options.TileHeight, tex);
                            break;
                        case MapAutotiles.RenderStateAutotile:
                            DrawAutoTile(l, x*Options.TileWidth + xoffset, y*Options.TileHeight + yoffset, 1, x, y, z, tex);
                            DrawAutoTile(l, x*Options.TileWidth + (Options.TileWidth/2) + xoffset, y*Options.TileHeight + yoffset, 2, x, y, z, tex);
                            DrawAutoTile(l, x*Options.TileWidth + xoffset, y*Options.TileHeight + (Options.TileHeight/2) + yoffset, 3, x, y, z, tex);
                            DrawAutoTile(l, +x*Options.TileWidth + (Options.TileWidth/2) + xoffset, y*Options.TileHeight + (Options.TileHeight/2) + yoffset, 4, x, y, z, tex);
                            break;
                    }
                }
            }
        }

        //Map Caching (Only used if Options.RenderCaching is true)
        public void PreRenderMap()
        {
            if (!Globals.HasGameData) return;
            GameGraphics.PreRenderedMapLayer = true;
            if (_preRenderStage < 3)
            {
                int i = _preRenderStage;
                if (LowerTextures[i] == null)
                {
                    while (!GameGraphics.GetMapTexture(ref LowerTextures[i]))
                    {
                        Thread.Sleep(10);
                    }
                }
                if (UpperTextures[i] == null)
                {
                    while (!GameGraphics.GetMapTexture(ref UpperTextures[i]))
                    {
                        Thread.Sleep(10);
                    }
                }
                if (PeakTextures[i] == null)
                {
                    while (!GameGraphics.GetMapTexture(ref PeakTextures[i]))
                    {
                        Thread.Sleep(10);
                    }
                }
                LowerTextures[i].Clear(IntersectClientExtras.GenericClasses.Color.Transparent);
                UpperTextures[i].Clear(IntersectClientExtras.GenericClasses.Color.Transparent);
                PeakTextures[i].Clear(IntersectClientExtras.GenericClasses.Color.Transparent);

                _preRenderStage++;
                return;
            }
            else if (_preRenderStage >= 3 && _preRenderStage < 6)
            {
                int i = _preRenderStage - 3;
                int l = _preRenderLayer;
                _preRenderLayer++;
                if (l < 3)
                {
                    LowerTextures[i].Begin();
                    DrawMapLayer(LowerTextures[i], l, i);
                    LowerTextures[i].End();
                    return;
                }
                else if (l == 3)
                {
                    UpperTextures[i].Begin();
                    DrawMapLayer(UpperTextures[i], l, i);
                    UpperTextures[i].End();
                    return;
                }
                else
                {
                    PeakTextures[i].Begin();
                    DrawMapLayer(PeakTextures[i], l, i);
                    PeakTextures[i].End();
                    _preRenderStage++;
                    _preRenderLayer = 0;
                    return;
                }
            }

            MapRendered = true;
        }

        //Fogs/Panorama/Overlay
        private void DrawFog()
        {
            if (Globals.Me == null || MapInstance.GetMap(Globals.Me.CurrentMap) == null) return;
            float ecTime = Globals.System.GetTimeMS() - _fogUpdateTime;
            _fogUpdateTime = Globals.System.GetTimeMS();
            if (MyMapNum == Globals.Me.CurrentMap)
            {
                if (_curFogIntensity != 1)
                {
                    _curFogIntensity += (ecTime/2000f);
                    if (_curFogIntensity > 1)
                    {
                        _curFogIntensity = 1;
                    }
                }
            }
            else
            {
                if (_curFogIntensity != 0)
                {
                    _curFogIntensity -= (ecTime/2000f);
                    if (_curFogIntensity < 0)
                    {
                        _curFogIntensity = 0;
                    }
                }
            }
            if (Fog.Length > 0)
            {
                GameTexture fogTex = Globals.ContentManager.GetTexture(GameContentManager.TextureType.Fog, Fog);
                if (fogTex != null)
                {
                    int xCount = (int) (Options.MapWidth*Options.TileWidth*3/fogTex.GetWidth());
                    int yCount = (int) (Options.MapHeight*Options.TileHeight*3/fogTex.GetHeight());

                    _fogCurrentX -= (ecTime/1000f)*FogXSpeed*-6;
                    _fogCurrentY += (ecTime/1000f)*FogYSpeed*2;
                    float deltaX = 0;
                    _fogCurrentX -= deltaX;
                    float deltaY = 0;
                    _fogCurrentY -= deltaY;

                    if (_fogCurrentX < fogTex.GetWidth())
                    {
                        _fogCurrentX += fogTex.GetWidth();
                    }
                    if (_fogCurrentX > fogTex.GetWidth())
                    {
                        _fogCurrentX -= fogTex.GetWidth();
                    }
                    if (_fogCurrentY < fogTex.GetHeight())
                    {
                        _fogCurrentY += fogTex.GetHeight();
                    }
                    if (_fogCurrentY > fogTex.GetHeight())
                    {
                        _fogCurrentY -= fogTex.GetHeight();
                    }

                    var drawX = (float) Math.Round(_fogCurrentX);
                    var drawY = (float) Math.Round(_fogCurrentY);

                    for (int x = -1; x < xCount; x++)
                    {
                        for (int y = -1; y < yCount; y++)
                        {
                            int fogW = fogTex.GetWidth();
                            int fogH = fogTex.GetHeight();
                            GameGraphics.DrawGameTexture(fogTex, new FloatRect(0, 0, fogW, fogH), new FloatRect(GetX() - (Options.MapWidth*Options.TileWidth*1.5f) + x*fogW + drawX, GetY() - (Options.MapHeight*Options.TileHeight*1.5f) + y*fogH + drawY, fogW, fogH), new IntersectClientExtras.GenericClasses.Color((byte) (FogTransparency*_curFogIntensity), 255, 255, 255), null, GameBlendModes.Alpha);
                        }
                    }
                }
            }
        }

        private void DrawPanorama()
        {
            float ecTime = Globals.System.GetTimeMS() - _panoramaUpdateTime;
            _panoramaUpdateTime = Globals.System.GetTimeMS();
            if (MyMapNum == Globals.Me.CurrentMap)
            {
                if (_panoramaIntensity != 1)
                {
                    _panoramaIntensity += (ecTime/2000f);
                    if (_panoramaIntensity > 1)
                    {
                        _panoramaIntensity = 1;
                    }
                }
            }
            else
            {
                if (_panoramaIntensity != 0)
                {
                    _panoramaIntensity -= (ecTime/2000f);
                    if (_panoramaIntensity < 0)
                    {
                        _panoramaIntensity = 0;
                    }
                }
            }
            GameTexture imageTex = Globals.ContentManager.GetTexture(GameContentManager.TextureType.Image, Panorama);
            if (imageTex != null)
            {
                GameGraphics.DrawFullScreenTexture(imageTex, _panoramaIntensity);
            }
        }

        private void DrawOverlayGraphic()
        {
            float ecTime = Globals.System.GetTimeMS() - _overlayUpdateTime;
            _overlayUpdateTime = Globals.System.GetTimeMS();
            if (MyMapNum == Globals.Me.CurrentMap)
            {
                if (_overlayIntensity != 1)
                {
                    _overlayIntensity += (ecTime/2000f);
                    if (_overlayIntensity > 1)
                    {
                        _overlayIntensity = 1;
                    }
                }
            }
            else
            {
                if (_overlayIntensity != 0)
                {
                    _overlayIntensity -= (ecTime/2000f);
                    if (_overlayIntensity < 0)
                    {
                        _overlayIntensity = 0;
                    }
                }
            }
            GameTexture imageTex = Globals.ContentManager.GetTexture(GameContentManager.TextureType.Image, OverlayGraphic);
            if (imageTex != null)
            {
                GameGraphics.DrawFullScreenTexture(imageTex, _overlayIntensity);
            }
        }

        //Events
        public void AddEvent(Event evt)
        {
            if (LocalEntities.ContainsKey(evt.MyIndex))
            {
                LocalEntities[evt.MyIndex].Dispose();
                LocalEntities[evt.MyIndex] = evt;
            }
            else
            {
                LocalEntities.Add(evt.MyIndex, evt);
            }
        }

        public override GameObject GetGameObjectType()
        {
            return Type;
        }

        public new static MapInstance GetMap(int index)
        {
            if (Objects.ContainsKey(index))
            {
                return (MapInstance) Objects[index];
            }
            return null;
        }

        public static DatabaseObject Get(int index)
        {
            if (Objects.ContainsKey(index))
            {
                return Objects[index];
            }
            return null;
        }

        public override void Delete()
        {
            Objects.Remove(GetId());
            MapBase.GetObjects().Remove(GetId());
        }

        public static void ClearObjects()
        {
            Objects.Clear();
            MapBase.ClearObjects();
        }

        public static void AddObject(int index, DatabaseObject obj)
        {
            Objects.Add(index, obj);
            MapBase.Objects.Add(index, (MapBase) obj);
        }

        public static int ObjectCount()
        {
            return Objects.Count;
        }

        public static Dictionary<int, MapInstance> GetObjects()
        {
            Dictionary<int, MapInstance> objects = Objects.ToDictionary(k => k.Key, v => (MapInstance) v.Value);
            return objects;
        }

        //Dispose
        public void Dispose(bool prep = true, bool killentities = true)
        {
            if (Globals.Database.RenderCaching)
            {
                ReleaseRenderTextures();
            }
            if (killentities)
            {
                foreach (var en in Globals.Entities)
                {
                    if (en.Value.CurrentMap == MyMapNum)
                    {
                        Globals.EntitiesToDispose.Add(en.Key);
                    }
                }
                foreach (var en in LocalEntities)
                {
                    en.Value.Dispose();
                }
            }
            HideActiveAnimations();
            ClearMapAttributes();
            MapRendered = false;
            MapLoaded = false;
            Delete();
        }

        private void ReleaseRenderTextures()
        {
            //Clean up all map stuff.
            for (int i = 0; i < 3; i++)
            {
                if (LowerTextures[i] != null)
                {
                    GameGraphics.ReleaseMapTexture(LowerTextures[i]);
                    LowerTextures[i] = null;
                }
                if (UpperTextures[i] != null)
                {
                    GameGraphics.ReleaseMapTexture(UpperTextures[i]);
                    UpperTextures[i] = null;
                }
                if (PeakTextures[i] != null)
                {
                    GameGraphics.ReleaseMapTexture(PeakTextures[i]);
                    PeakTextures[i] = null;
                }
            }
        }
    }
}