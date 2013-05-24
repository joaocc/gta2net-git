﻿// GTA2.NET
// 
// File: TextureAtlas.cs
// Created: 28.01.2010
// 
// 
// Copyright (C) 2010-2013 Hiale
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software
// and associated documentation files (the "Software"), to deal in the Software without restriction,
// including without limitation the rights to use, copy, modify, merge, publish, distribute,
// sublicense, and/or sell copies of the Software, and to permit persons to whom the Software
// is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies
// or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR
// IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// 
// Grand Theft Auto (GTA) is a registred trademark of Rockstar Games.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Messaging;
using System.Xml.Serialization;
using System.IO;
using Hiale.GTA2NET.Core.Helper.Threading;
using Hiale.GTA2NET.Core.Style;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Hiale.GTA2NET.Core.Helper
{
    /// <summary>
    /// Holds information where certail tiles or sprites are put on the image.
    /// </summary>
    [Serializable]
    public abstract class TextureAtlas : IDisposable
    {
        protected class ImageEntry
        {
            public int Index;
            public string FileName;
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public int ZipEntryIndex;
            public int SameImageIndex;
        }

        //Based on http://www.blackpawn.com/texts/lightmaps/
        protected class Node
        {
            public Rectangle Rectangle;
            private readonly Node[] _child;
            private int _imageWidth;
            private int _imageHeight;

            public Node(int x, int y, int width, int height)
            {
                Rectangle = new Rectangle(x, y, width, height);
                _child = new Node[2];
                _child[0] = null;
                _child[1] = null;
                _imageWidth = -1;
                _imageHeight = -1;
            }

            private bool IsLeaf()
            {
                return _child[0] == null && _child[1] == null;
            }

            public Node Insert(int imageWidth, int imageHeight)
            {
                if (!IsLeaf())
                {
                    var newNode = _child[0].Insert(imageWidth, imageHeight);
                    return newNode ?? _child[1].Insert(imageWidth, imageHeight);
                }
                if (_imageWidth >= 0 && _imageHeight >= 0)
                    return null;
                if (imageWidth > Rectangle.Width || imageHeight > Rectangle.Height)
                    return null;

                if (imageWidth == Rectangle.Width && imageHeight == Rectangle.Height)
                {
                    _imageWidth = imageWidth;
                    _imageHeight = imageHeight;
                    return this;
                }

                var dw = Rectangle.Width - imageWidth;
                var dh = Rectangle.Height - imageHeight;

                if (dw > dh)
                {
                    _child[0] = new Node(Rectangle.X, Rectangle.Y, imageWidth, Rectangle.Height);
                    _child[1] = new Node(Rectangle.X + imageWidth, Rectangle.Y, Rectangle.Width - imageWidth, Rectangle.Height);
                }
                else
                {
                    _child[0] = new Node(Rectangle.X, Rectangle.Y, Rectangle.Width, imageHeight);
                    _child[1] = new Node(Rectangle.X, Rectangle.Y + imageHeight, Rectangle.Width, Rectangle.Height - imageHeight);
                }
                return _child[0].Insert(imageWidth, imageHeight);
            }
        }

        protected class ImageEntryComparer : IComparer<ImageEntry>
        {
            public bool CompareSize { get; set; }

            public int Compare(ImageEntry x, ImageEntry y)
            {
                if (CompareSize)
                {
                    var xSize = x.Height*1024 + x.Width;
                    var ySize = y.Height*1024 + y.Width;
                    return ySize.CompareTo(xSize);
                }
                return x.Index.CompareTo(y.Index);
            }
        }

        public event AsyncCompletedEventHandler BuildTextureAtlasCompleted;

        private delegate void BuildTextureAtlasDelegate(CancellableContext context, out bool cancelled);

        private readonly object _sync = new object();

        [XmlIgnore]
        public bool IsBusy { get; private set; }

        private CancellableContext _buildTextureAtlasContext;

        /// <summary>
        /// Image with all the tiles, sprites or deltas on it. Only used during creation. Use ImagePath to load the image at runtime.
        /// </summary>
        [XmlIgnore]
        public Image Image { get; protected set; }

        /// <summary>
        /// Path to image file, used by serialization
        /// </summary>
        public string ImagePath { get; set; }

        /// <summary>
        /// Padding to eliminate texture bleeding, it SEEMS that XNA 4.0 fixed it, so it's not needed anymore?
        /// </summary>
        public int Padding { get; set; }

        [XmlIgnore]
        public ZipStorer ZipStore { get; protected set; }

        protected List<ZipStorer.ZipFileEntry> ZipEntries;

        protected Dictionary<uint, ImageEntry> CrcDictionary; //Helper list to find duplicate images.

        protected Graphics Graphics;

        protected TextureAtlas()
        {
            //needed by xml serializer
            Padding = 1;
            CrcDictionary = new Dictionary<uint, ImageEntry>();
        }

        protected TextureAtlas(string imagePath, ZipStorer zipStore)
            : this()
        {
            ImagePath = imagePath;
            ZipStore = zipStore;
        }

        protected virtual List<ImageEntry> CreateImageEntries(CancellableContext context, out bool cancelled)
        {
            cancelled = false;
            var entries = new List<ImageEntry>();
            CrcDictionary.Clear();
            ZipEntries = ZipStore.ReadCentralDir();
            for (var i = 0; i < ZipEntries.Count; i++)
            {
                if (context.IsCancelling)
                {
                    cancelled = true;
                    return null;
                }
                var entry = new ImageEntry();
                if (!CrcDictionary.ContainsKey(ZipEntries[i].Crc32))
                {
                    CrcDictionary.Add(ZipEntries[i].Crc32, entry);
                    var source = GetBitmapFromZip(ZipStore, i);
                    if (source != null)
                    {
                        entry.Width = source.Width + 2 * Padding; // Include a single pixel padding around each sprite, to avoid filtering problems if the sprite is scaled or rotated.
                        entry.Height = source.Height + 2 * Padding;
                        source.Dispose();
                    }
                    else
                    {
                        entry.Width = 0; // + 2*Padding;
                        entry.Height = 0; // +2 * Padding;
                    }
                }
                else
                {
                    var sameEntry = CrcDictionary[ZipEntries[i].Crc32];
                    entry.SameImageIndex = sameEntry.Index;
                    entry.Width = sameEntry.Width;
                    entry.Height = sameEntry.Height;
                }
                entry.Index = i;
                entry.FileName = ParsePath(ZipEntries[i].FilenameInZip);
                entry.ZipEntryIndex = i;
                entries.Add(entry);
            }
            return entries;
        }

        protected virtual Bitmap GetBitmapFromZip(ZipStorer zipStore, int zipFileEntryIndex)
        {
            var zipFileEntry = ZipEntries[zipFileEntryIndex];
            var memoryStream = new MemoryStream((int) zipFileEntry.FileSize);
            zipStore.ExtractFile(zipFileEntry, memoryStream);
            memoryStream.Position = 0;
            var bmp = (Bitmap) Image.FromStream(memoryStream);
            memoryStream.Close();
            return bmp;
        }

        protected virtual void CreateOutputBitmap(int width, int height)
        {
            Image = new Bitmap(width, height);
            Graphics = Graphics.FromImage(Image);
        }

        protected virtual CompactRectangle Place(ImageEntry entry)
        {
            var source = GetBitmapFromZip(ZipStore, entry.ZipEntryIndex);
            Graphics.DrawImageUnscaled(source, entry.X + Padding, entry.Y + Padding);
            var rect = new CompactRectangle(entry.X + Padding, entry.Y + Padding, entry.Width - 2*Padding, entry.Height - 2*Padding);
            source.Dispose();
            return rect;
        }

        public virtual void BuildTextureAtlasAsync()
        {
            var worker = new BuildTextureAtlasDelegate(BuildTextureAtlas);
            var completedCallback = new AsyncCallback(BuildTextureAtlasCompleteCallback);

            lock (_sync)
            {
                if (IsBusy)
                    throw new InvalidOperationException("The control is currently busy.");

                var async = AsyncOperationManager.CreateOperation(null);
                var context = new CancellableContext(async);
                bool cancelled;

                worker.BeginInvoke(context, out cancelled, completedCallback, async);

                IsBusy = true;
                _buildTextureAtlasContext = context;
            }
        }

        public void BuildTextureAtlas()
        {
            var context = new CancellableContext(null);
            bool cancelled;
            BuildTextureAtlas(context, out cancelled);
        }

        protected abstract void BuildTextureAtlas(CancellableContext context, out bool cancel);

        private void BuildTextureAtlasCompleteCallback(IAsyncResult ar)
        {
            var worker = (BuildTextureAtlasDelegate) ((AsyncResult) ar).AsyncDelegate;
            var async = (AsyncOperation) ar.AsyncState;
            bool cancelled;

            // finish the asynchronous operation
            worker.EndInvoke(out cancelled, ar);

            // clear the running task flag
            lock (_sync)
            {
                IsBusy = false;
                _buildTextureAtlasContext = null;
            }

            // raise the completed event
            var completedArgs = new AsyncCompletedEventArgs(null, cancelled, null);
            async.PostOperationCompleted(e => OnBuildTextureAtlasCompleted((AsyncCompletedEventArgs) e), completedArgs);
        }

        public void CancelBuildTextureAtlas()
        {
            lock (_sync)
            {
                if (_buildTextureAtlasContext != null)
                    _buildTextureAtlasContext.Cancel();
            }
        }

        /// <summary>
        /// Heuristic guesses what might be a good output width for a list of sprites.
        /// </summary>
        protected virtual int GuessOutputWidth(ICollection<ImageEntry> entries)
        {
            // Gather the widths of all our sprites into a temporary list.
            var widths = entries.Select(entry => entry.Width).ToList();

            // Sort the widths into ascending order.
            //widths.Sort();

            // Extract the maximum and median widths.
            var maxWidth = widths[widths.Count - 1];
            var medianWidth = widths[widths.Count/2];

            // Heuristic assumes an NxN grid of median sized sprites.
            var width = medianWidth*(int) Math.Round(Math.Sqrt(entries.Count));

            // Make sure we never choose anything smaller than our largest sprite.
            width = Math.Max(width, maxWidth);

            return PowerOfTwo(width);
        }

        protected virtual int GuessOutputHeight(ICollection<ImageEntry> entries, int width)
        {
            var totalArea = entries.Sum(imageEntry => imageEntry.Width*imageEntry.Height);
            var height = (int) Math.Ceiling((float) totalArea/width);
            return PowerOfTwo(height);
        }

        protected virtual int PowerOfTwo(int minimum)
        {
            uint current;
            var exponent = 0;
            do
            {
                current = (uint) (1 << exponent);
                exponent++;
            } while (current < minimum);
            return (int) current;
        }

        //Based on http://stackoverflow.com/questions/4820212/automatically-trim-a-bitmap-to-minimum-size/4821100#4821100
        public static System.Drawing.Rectangle CalculateTrimRegion(Bitmap source)
        {
            System.Drawing.Rectangle srcRect;
            BitmapData data = null;
            try
            {
                data = source.LockBits(new System.Drawing.Rectangle(0, 0, source.Width, source.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var buffer = new byte[data.Height * data.Stride];
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                int xMin = int.MaxValue,
                    xMax = int.MinValue,
                    yMin = int.MaxValue,
                    yMax = int.MinValue;

                var foundPixel = false;

                // Find xMin
                for (var x = 0; x < data.Width; x++)
                {
                    var stop = false;
                    for (var y = 0; y < data.Height; y++)
                    {
                        var alpha = buffer[y * data.Stride + 4 * x + 3];
                        if (alpha == 0)
                            continue;
                        xMin = x;
                        stop = true;
                        foundPixel = true;
                        break;
                    }
                    if (stop)
                        break;
                }

                // Image is empty...
                if (!foundPixel)
                    return new System.Drawing.Rectangle();

                // Find yMin
                for (var y = 0; y < data.Height; y++)
                {
                    var stop = false;
                    for (var x = xMin; x < data.Width; x++)
                    {
                        var alpha = buffer[y * data.Stride + 4 * x + 3];
                        if (alpha == 0)
                            continue;
                        yMin = y;
                        stop = true;
                        break;
                    }
                    if (stop)
                        break;
                }

                // Find xMax
                for (var x = data.Width - 1; x >= xMin; x--)
                {
                    var stop = false;
                    for (var y = yMin; y < data.Height; y++)
                    {
                        var alpha = buffer[y * data.Stride + 4 * x + 3];
                        if (alpha == 0)
                            continue;
                        xMax = x;
                        stop = true;
                        break;
                    }
                    if (stop)
                        break;
                }

                // Find yMax
                for (var y = data.Height - 1; y >= yMin; y--)
                {
                    var stop = false;
                    for (var x = xMin; x <= xMax; x++)
                    {
                        var alpha = buffer[y * data.Stride + 4 * x + 3];
                        if (alpha == 0)
                            continue;
                        yMax = y;
                        stop = true;
                        break;
                    }
                    if (stop)
                        break;
                }
                srcRect = System.Drawing.Rectangle.FromLTRB(xMin, yMin, xMax + 1, yMax + 1);
            }
            finally
            {
                if (data != null)
                    source.UnlockBits(data);
            }
            return srcRect;
        }

        public static Bitmap TrimBitmap(Bitmap source)
        {
            var srcRect = CalculateTrimRegion(source);
            return source.Clone(srcRect, source.PixelFormat);
        }

        protected virtual string ParsePath(string path)
        {
            var pos = path.LastIndexOf('/');
            return path.Substring(pos + 1, path.Length - pos - Globals.TextureImageFormat.Length - 1);
        }

        public void Serialize(string path)
        {
            var textWriter = new StreamWriter(path);
            var serializer = new XmlSerializer(GetType());
            serializer.Serialize(textWriter, this);
            textWriter.Close();
        }

        public static T Deserialize<T>(string path) where T : TextureAtlas
        {
            var textReader = new StreamReader(path);
            var deserializer = new XmlSerializer(typeof (T));
            var atlas = (T) deserializer.Deserialize(textReader);
            textReader.Close();
            return atlas;
        }

        protected virtual void OnBuildTextureAtlasCompleted(AsyncCompletedEventArgs e)
        {
            if (BuildTextureAtlasCompleted != null)
                BuildTextureAtlasCompleted(this, e);
        }

        /// <summary>
        /// Disposes the image when not needed anymore.
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (Image != null)
                    Image.Dispose();
                if (Graphics != null)
                    Graphics.Dispose();
            }
            catch (Exception)
            {
                //ignore
            }
        }

    }

    public class TextureAtlasTiles : TextureAtlas
    {
        public SerializableDictionary<int, CompactRectangle> TileDictionary { get; set; }

        public TextureAtlasTiles()
        {
            //this constructor is needed by xml serializer
        }

        public TextureAtlasTiles(string imagePath, ZipStorer zipStore) : base(imagePath, zipStore)
        {
            //
        }

        protected override void BuildTextureAtlas(CancellableContext context, out bool cancelled)
        {
            var entries = CreateImageEntries(context, out cancelled);
            if (cancelled)
                return;
            var outputWidth = GuessOutputWidth(entries);
            var outputHeight = GuessOutputHeight(entries, outputWidth);

            var root = new Node(0, 0, outputWidth, outputHeight);

            if (context.IsCancelling)
            {
                cancelled = true;
                return;
            }
            CreateOutputBitmap(outputWidth, outputHeight);
            TileDictionary = new SerializableDictionary<int, CompactRectangle>();
            foreach (var entry in entries)
            {
                if (context.IsCancelling)
                {
                    cancelled = true;
                    return;
                }

                CompactRectangle rect;
                if (entry.SameImageIndex == 0)
                {
                    var node = root.Insert(entry.Width, entry.Height);
                    if (node == null)
                        continue; //no space to put the image, increase the output image?
                    entry.X = node.Rectangle.X;
                    entry.Y = node.Rectangle.Y;
                    rect = Place(entry);
                }
                else
                {
                    rect = TileDictionary[entry.SameImageIndex];
                }
                var index = int.Parse(entry.FileName);
                TileDictionary.Add(index, rect);
            }
            Image.Save(Globals.GraphicsSubDir + Path.DirectorySeparatorChar + ImagePath, ImageFormat.Png);
            Serialize(Globals.GraphicsSubDir + Path.DirectorySeparatorChar + Path.GetFileNameWithoutExtension(ImagePath) + Globals.XmlFormat);

        }
    }

    public class TextureAtlasSprites : TextureAtlas
    {
        public SerializableDictionary<int, SpriteItem> SpriteDictionary { get; set; }

        public TextureAtlasSprites()
        {
            //this constructor is needed by xml serializer
        }

        public TextureAtlasSprites(string imagePath, ZipStorer zipStore, SerializableDictionary<int, SpriteItem> spriteDictionary) : base(imagePath, zipStore)
        {
            SpriteDictionary = spriteDictionary;
        }

        public static void FillSpriteId(SerializableDictionary<int, SpriteItem> spriteDictionary)
        {
            foreach (var spriteItem in spriteDictionary)
                spriteItem.Value.SpriteId = spriteItem.Key;
        }

        public void MergeDeltas(SerializableDictionary<int, DeltaItem> deltaDictionary)
        {
            MergeDeltas(SpriteDictionary, deltaDictionary);
        }

        public static void MergeDeltas(SerializableDictionary<int, SpriteItem> spriteDictionary, SerializableDictionary<int, DeltaItem> deltaDictionary)
        {
            foreach (var deltaItem in deltaDictionary)
            {
                SpriteItem spriteItem;
                if (spriteDictionary.TryGetValue(deltaItem.Key, out spriteItem))
                {
                    spriteItem.DeltaItems = deltaItem.Value.SubItems;
                }
            }
        }

        protected override void BuildTextureAtlas(CancellableContext context, out bool cancelled)
        {
            var entries = CreateImageEntries(context, out cancelled);
            if (cancelled)
                return;

            // Sort so the largest sprites get arranged first.
            var comparer = new ImageEntryComparer {CompareSize = true};
            entries.Sort(comparer);

            var outputWidth = GuessOutputWidth(entries);
            var outputHeight = GuessOutputHeight(entries, outputWidth);
            outputWidth = 2048; //ToDo

            if (context.IsCancelling)
            {
                cancelled = true;
                return;
            }

            // Sort the sprites back into index order.
            comparer.CompareSize = false;
            entries.Sort(comparer);

            var root = new Node(0, 0, outputWidth, outputHeight);

            CreateOutputBitmap(outputWidth, outputHeight);
            //SpriteDictionary = new SerializableDictionary<int, SpriteItem>();
            foreach (var entry in entries)
            {
                if (context.IsCancelling)
                {
                    cancelled = true;
                    return;
                }

                CompactRectangle rect;
                if (entry.SameImageIndex == 0)
                {
                    var node = root.Insert(entry.Width, entry.Height);
                    if (node == null)
                        continue; //ToDo: the picture could not be inserted because there were not enough space. Increase the output image?
                    entry.X = node.Rectangle.X;
                    entry.Y = node.Rectangle.Y;
                    rect = Place(entry);
                }
                else
                {
                    rect = SpriteDictionary[entry.SameImageIndex].Rectangle;
                }
                var item = SpriteDictionary[entry.Index];
                item.Rectangle = rect;
            }
            Image.Save(Globals.GraphicsSubDir + Path.DirectorySeparatorChar + ImagePath, ImageFormat.Png);
            Serialize(Globals.GraphicsSubDir + Path.DirectorySeparatorChar + Path.GetFileNameWithoutExtension(ImagePath) + Globals.XmlFormat);
        }
    }

    public class TextureAtlasDeltas : TextureAtlas
    {
        private struct ItemToRemove
        {
            public int SpriteId;
            public DeltaSubItem DeltaSubItem;
        }


        public SerializableDictionary<int, DeltaItem> DeltaDictionary { get; set; } //Key = Sprite ID

        private readonly Dictionary<string, int[]> _deltaIndexDictionary; //Key = Filename, Value 0 = Sprite Id, Value 1 = Delta Index

        private readonly Dictionary<int, System.Drawing.Rectangle> _cachedRectDictionary; //Key = Zip entry index

        public TextureAtlasDeltas()
        {
            //this constructor is needed by xml serializer
        }

        public TextureAtlasDeltas(string imagePath, ZipStorer zipStore, SerializableDictionary<int, DeltaItem> deltaDictionary) : base(imagePath, zipStore)
        {
            DeltaDictionary = deltaDictionary;
            _deltaIndexDictionary = new Dictionary<string, int[]>();
            _cachedRectDictionary = new Dictionary<int, System.Drawing.Rectangle>();
        }

        public static void FillSpriteId(SerializableDictionary<int, DeltaItem> deltaDictionary)
        {
            foreach (var deltaItem in deltaDictionary)
                deltaItem.Value.SpriteId = deltaItem.Key;
        }

        protected override Bitmap GetBitmapFromZip(ZipStorer zipStore, int zipFileEntryIndex)
        {
            var bmp = base.GetBitmapFromZip(zipStore, zipFileEntryIndex);
            var bmpWidth = bmp.Width;
            var bmpHeight = bmp.Height;
            System.Drawing.Rectangle drawingRect;
            if (_cachedRectDictionary.ContainsKey(zipFileEntryIndex))
            {
                drawingRect = _cachedRectDictionary[zipFileEntryIndex];
            }
            else
            {
                drawingRect = CalculateTrimRegion(bmp);
                _cachedRectDictionary.Add(zipFileEntryIndex, drawingRect);
            }
            var area = drawingRect.Width*drawingRect.Height;
            if (area < (bmpWidth*bmpHeight))
            {
                var trimmedBmp = area == 0 ? null : bmp.Clone(drawingRect, bmp.PixelFormat);
                bmp.Dispose();
                return trimmedBmp;
            }
            return bmp;
        }

        protected override void BuildTextureAtlas(CancellableContext context, out bool cancelled)
        {
            var entries = CreateImageEntries(context, out cancelled);
            if (cancelled)
                return;

            // Sort so the largest sprites get arranged first.
            var comparer = new ImageEntryComparer {CompareSize = true};
            entries.Sort(comparer);

            var outputWidth = GuessOutputWidth(entries);
            var outputHeight = GuessOutputHeight(entries, outputWidth);

            if (context.IsCancelling)
            {
                cancelled = true;
                return;
            }

            // Sort the sprites back into index order.
            comparer.CompareSize = false;
            entries.Sort(comparer);

            var root = new Node(0, 0, outputWidth, outputHeight);

            CreateOutputBitmap(outputWidth, outputHeight);

            var itemsToRemove = new List<ItemToRemove>();

            foreach (var entry in entries)
            {
                if (context.IsCancelling)
                {
                    cancelled = true;
                    return;
                }

                CompactRectangle rect;
                if (entry.SameImageIndex == 0)
                {
                    if (entry.Width*entry.Height > 0)
                    {
                        var node = root.Insert(entry.Width, entry.Height);
                        if (node == null)
                            continue; //ToDo: the picture could not be inserted because there were not enough space. Increase the output image?
                        entry.X = node.Rectangle.X;
                        entry.Y = node.Rectangle.Y;
                        rect = Place(entry);
                    }
                    else
                    {
                        rect = new CompactRectangle();
                    }
                }
                else
                {
                    var sameEntry = entries[entry.SameImageIndex];
                    var sameSpriteDeltaId = _deltaIndexDictionary[sameEntry.FileName];
                    var sameSpriteId = sameSpriteDeltaId[0];
                    var sameDeltaIndex = sameSpriteDeltaId[1];
                    rect = DeltaDictionary[sameSpriteId].SubItems[sameDeltaIndex].Rectangle;
                }

                var spriteDeltaId = _deltaIndexDictionary[entry.FileName];
                var spriteId = spriteDeltaId[0];
                var deltaIndex = spriteDeltaId[1];
                var subDeltaItem = DeltaDictionary[spriteId].SubItems[deltaIndex];
                if (entry.Width*entry.Height > 0)
                {
                    var cachedRect = _cachedRectDictionary[entry.SameImageIndex == 0 ? entry.ZipEntryIndex : entry.SameImageIndex];
                    subDeltaItem.RelativePosition = new Point(cachedRect.X, cachedRect.Y);
                    subDeltaItem.Rectangle = rect;
                }
                else
                {
                    var itemToRemove = new ItemToRemove {SpriteId = spriteId, DeltaSubItem = subDeltaItem};
                    itemsToRemove.Add(itemToRemove);
                }
            }

            foreach (var itemToRemove in itemsToRemove)
            {
                var deltaIndexToRemove = -1;
                for (var i = 0; i < DeltaDictionary[itemToRemove.SpriteId].SubItems.Count; i++)
                {
                    if (DeltaDictionary[itemToRemove.SpriteId].SubItems[i].Type != itemToRemove.DeltaSubItem.Type)
                        continue;
                    deltaIndexToRemove = i;
                    break;
                }
                if (deltaIndexToRemove > -1)
                    DeltaDictionary[itemToRemove.SpriteId].SubItems.RemoveAt(deltaIndexToRemove);
            }

            Image.Save(Globals.GraphicsSubDir + Path.DirectorySeparatorChar + ImagePath, ImageFormat.Png);
            Serialize(Globals.GraphicsSubDir + Path.DirectorySeparatorChar + Path.GetFileNameWithoutExtension(ImagePath) + Globals.XmlFormat);
        }

        protected override string ParsePath(string path)
        {
            var fileName = base.ParsePath(path);
            var parts = fileName.Split('_');
            var spriteId = int.Parse(parts[0]);
            var deltaId = int.Parse(parts[1]);
            _deltaIndexDictionary.Add(fileName, new[] {spriteId, deltaId});
            return fileName;
        }
    }
}


