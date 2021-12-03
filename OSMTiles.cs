using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;    // For extension method public static System.IO.Stream AsStream(this Windows.Storage.Streams.IBuffer source);
using System.Threading;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
using StereoKit;
using SimpleJSON;

namespace DutchSkies
{
    class OSMTiles
    {
        const int TILE_SIZE = 256;

        // After https://wiki.openstreetmap.org/wiki/Slippy_map_tilenames
        // Return tile number that contains given lat/lon coordinate
        public static void CoordinateToTile(out int xtile, out int ytile, float lat_deg, float lon_deg, int zoom)
        {
            float lat_rad = lat_deg / 180f * MathF.PI;
            float n = MathF.Pow(2f, zoom);
            xtile = (int)((lon_deg + 180f) / 360f * n);
            ytile = (int)((1f - MathF.Log(MathF.Tan(lat_rad) + (1f / MathF.Cos(lat_rad))) / MathF.PI) / 2f * n);
        }

        // Convert tile number and zoom to lat/lon coordinate of NW-corner of tile
        // Use the function with xtile+1 and/or ytile+1 to get the other corners.
        // With xtile+0.5 & ytile+0.5 it will return the center of the tile.
        public static void TileNWCorner(out float lat_deg, out float lon_deg, int xtile, int ytile, int zoom)
        {
            float n = MathF.Pow(2f, zoom);
            lon_deg = xtile / n * 360f - 180f;
            float lat_rad = MathF.Atan(MathF.Sinh(MathF.PI * (1f - 2f * ytile / n)));
            lat_deg = lat_rad / MathF.PI * 180f;
        }

        public static async void FetchMapTiles(object tuple)
        {
            Tuple<object, object> args = tuple as Tuple<object, object>;
            BlockingCollection<JSONNode> input_queue = args.Item1 as BlockingCollection<JSONNode>;
            ConcurrentQueue<Tuple<string, object, string>> result_queue = args.Item2 as ConcurrentQueue<Tuple<string, object, string>>;

            JSONNode map_spec;
            JSONNode tile_servers;
            string map_name;
            float min_lat, max_lat;
            float min_lon, max_lon;
            int zoom;

            while (true)
            {
                map_spec = input_queue.Take();
                map_name = map_spec["name"];

                Log.Info($"(tile fetch thread) Fetching tiles for map '{map_name}'");

                if (!map_spec.HasKey("lat_range"))
                {
                    Log.Warn("(tile fetch thread) Map specification is missing 'lat_range'!");
                    continue;
                }
                if (!map_spec.HasKey("lon_range"))
                {
                    Log.Warn("(tile fetch thread) Map specification is missing 'lon_range'!");
                    continue;
                }
                if (!map_spec.HasKey("zoom"))
                {
                    Log.Warn("(tile fetch thread) Map specification is missing 'zoom'!");
                    continue;
                }

                min_lat = map_spec["lat_range"][0];
                max_lat = map_spec["lat_range"][1];
                min_lon = map_spec["lon_range"][0];
                max_lon = map_spec["lon_range"][1];
                zoom = map_spec["zoom"];
                tile_servers = map_spec["image_source"]["tile_servers"];

                // Determine tiles needed
                float lat = 0.5f * (min_lat + max_lat);
                float lon = 0.5f * (min_lon + max_lon);

                int dummy, minj, maxj, mini, maxi;

                CoordinateToTile(out dummy, out maxj, min_lat, lon, zoom);
                CoordinateToTile(out dummy, out minj, max_lat, lon, zoom);

                CoordinateToTile(out mini, out dummy, lat, min_lon, zoom);
                CoordinateToTile(out maxi, out dummy, lat, max_lon, zoom);

                Log.Info($"(tile fetch thread) Tile range needed: i {mini}, {maxi}; j {minj}, {maxj}");

                int nrows = maxj - minj + 1;
                int ncols = maxi - mini + 1;
                int num_tiles_to_fetch = nrows * ncols;

                // Determine lat/lon range from tile extent
                float minlat, minlon, maxlat, maxlon;
                TileNWCorner(out maxlat, out minlon, mini, minj, zoom);
                TileNWCorner(out minlat, out maxlon, maxi + 1, maxj + 1, zoom);
                float center_lat = 0.5f * (minlat + maxlat);
                float center_lon = 0.5f * (minlon + maxlon);
                Log.Info($"(tile fetch thread) Map range: {minlat}, {minlon} -> {maxlat}, {maxlon}");
                Log.Info($"(tile fetch thread) Map center: {center_lat}, {center_lon}");

                // Compute map size at center (only used to print)
                const float R = 6378136.98f;
                float r = R * MathF.Cos(0.5f * (min_lat + max_lat) / 180f * MathF.PI);
                float lon_diff = max_lon - min_lon;
                float map_width = lon_diff / 360f * 2f * MathF.PI * r;
                float map_height = (max_lat - min_lat) / 360f * 2f * MathF.PI * R;
                Log.Info($"(tile fetch thread) Map size at center = {map_width / 1000f:F3} x {map_height / 1000f:F3}");

                Log.Info($"(tile fetch thread) Retrieving {ncols} x {nrows} OSM tiles ({ncols * TILE_SIZE} x {nrows * TILE_SIZE} pixels)");

                const int TARGA_HEADER_SIZE = 18;
                int width = ncols * TILE_SIZE;
                int height = nrows * TILE_SIZE;
                byte[] full_map_pixels = new byte[TARGA_HEADER_SIZE + width * height * 3];

                int full_map_row_size = width * 3;  // RGB
                int tile_row_size = TILE_SIZE * 4;  // BGRA
                int tiles_fetched = 0;
                int server_idx = 0;
                string url;

                for (int j = minj; j <= maxj; j++)
                {
                    int destrow = (j - minj) * TILE_SIZE;

                    for (int i = mini; i <= maxi; i++)
                    {
                        try
                        {
                            url = tile_servers[server_idx];
                            url = url.Replace("{zoom}", zoom.ToString());
                            url = url.Replace("{x}", i.ToString());
                            url = url.Replace("{y}", j.ToString());

                            // XXX send progress update to main thread
                            //Log.Info($"(tile fetch thread) Fetching tile {i},{j} via {url}");

                            var rass = RandomAccessStreamReference.CreateFromUri(new Uri(url));
                            IRandomAccessStream ras = await rass.OpenReadAsync();
                            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(ras);
                            SoftwareBitmap bmp = await decoder.GetSoftwareBitmapAsync();
                            //Log.Info($"bitmap is {bmp.PixelWidth} x {bmp.PixelHeight} ({bmp.BitmapPixelFormat}, {bmp.BitmapAlphaMode})");
                            if (bmp.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                                Log.Warn("(tile fetch thread) Tile bitmap not in Bgra8 format, image will be broken!");

                            PixelDataProvider pdp = await decoder.GetPixelDataAsync();
                            byte[] tile_pixels = pdp.DetachPixelData();

                            // Paste tile pixels in full map image
                            int destcol = (i - mini) * TILE_SIZE;
                            for (int jj = 0; jj < TILE_SIZE; jj++)
                            {
                                for (int ii = 0; ii < TILE_SIZE; ii++)
                                {
                                    full_map_pixels[(destrow + jj) * full_map_row_size + (destcol + ii) * 3 + 0] = tile_pixels[jj * tile_row_size + ii * 4 + 2];
                                    full_map_pixels[(destrow + jj) * full_map_row_size + (destcol + ii) * 3 + 1] = tile_pixels[jj * tile_row_size + ii * 4 + 1];
                                    full_map_pixels[(destrow + jj) * full_map_row_size + (destcol + ii) * 3 + 2] = tile_pixels[jj * tile_row_size + ii * 4 + 0];
                                }
                            }

                            tiles_fetched++;
                            result_queue.Enqueue(new Tuple<string, object, string>("map_tilefetch_progress", 100f * tiles_fetched / num_tiles_to_fetch, map_name));

                            server_idx = (server_idx + 1) % tile_servers.Count;
                        }
                        catch (Exception e)
                        {
                            Log.Err($"(tile fetch thread) Exception while fetching tile {i},{j}: {e.Message}");
                        }
                    }
                }

                // Set TARGA header
                full_map_pixels[0] = 0;     // no ID information
                full_map_pixels[1] = 0;     // no color map
                full_map_pixels[2] = 2;     // uncompressed RGB
                full_map_pixels[3] = 0;     // color map spec (unused)
                full_map_pixels[4] = 0;
                full_map_pixels[5] = 0;
                full_map_pixels[6] = 0;
                full_map_pixels[7] = 0;
                full_map_pixels[8] = 0;     // X origin (2 bytes)
                full_map_pixels[9] = 0;
                full_map_pixels[10] = 0;     // Y origin (2 bytes)
                full_map_pixels[11] = 0;
                full_map_pixels[12] = (byte)(width & 0xff);     // Image width (2 bytes)
                full_map_pixels[13] = (byte)(width >> 8);
                full_map_pixels[14] = (byte)(height & 0xff);    // Image height (2 bytes)
                full_map_pixels[15] = (byte)(height >> 8);
                full_map_pixels[16] = 24;                       // Pixel depth in bits
                full_map_pixels[17] = 1 << 5;                   // Image descriptor (Y-flip)

                result_queue.Enqueue(new Tuple<string, object, string>("map_image", full_map_pixels, map_name));

                /*
                Windows.Storage.StorageFolder storageFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
                Log.Info($"FOLDER = {storageFolder.Path}");
                Windows.Storage.StorageFile sampleFile = await storageFolder.CreateFileAsync("map.tga", Windows.Storage.CreationCollisionOption.ReplaceExisting);
                await Windows.Storage.FileIO.WriteBytesAsync(sampleFile, full_map_pixels);
                */
            }
        }
    }
}
