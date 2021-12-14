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
    using TileFetchRequest = Tuple<int, int, int, int, int, string[], string>;

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
            //Log.Info($"{lat_deg}, {lon_deg}, {zoom} -> {n}, {xtile}, {ytile}");
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

        public static void ComputeActualMapExtent(
            out float min_lat, out float max_lat, out float min_lon, out float max_lon,
            out int min_i, out int max_i, out int min_j, out int max_j,
            Vec4 query_extent, int zoom)
        {
            float q_min_lat, q_max_lat, q_min_lon, q_max_lon;
            q_min_lat = query_extent.x;
            q_max_lat = query_extent.y;
            q_min_lon = query_extent.z;
            q_max_lon = query_extent.w;

            float q_center_lat = 0.5f * (q_min_lat + q_max_lat);
            float q_center_lon = 0.5f * (q_min_lon + q_max_lon);

            int dummy;

            CoordinateToTile(out dummy, out max_j, q_min_lat, q_center_lon, zoom);
            CoordinateToTile(out dummy, out min_j, q_max_lat, q_center_lon, zoom);

            CoordinateToTile(out min_i, out dummy, q_center_lat, q_min_lon, zoom);
            CoordinateToTile(out max_i, out dummy, q_center_lat, q_max_lon, zoom);

            // Determine tiles needed
            Log.Info($"(ComputeActualMapExtent) Tile range needed: i {min_i}, {max_i}; j {min_j}, {max_j}");

            // Determine actual lat/lon range from tile extent
            TileNWCorner(out max_lat, out min_lon, min_i, min_j, zoom);
            TileNWCorner(out min_lat, out max_lon, max_i + 1, max_j + 1, zoom);
            float a_center_lat = 0.5f * (min_lat + max_lat);
            float a_center_lon = 0.5f * (min_lon + max_lon);

            Log.Info($"(ComputeActualMapExtentd) Map range: {min_lat}, {min_lon} -> {max_lat}, {max_lon}");
            Log.Info($"(ComputeActualMapExtent) Map center: {a_center_lat}, {a_center_lon}");

            // Compute map size at center (only used to print)
            const float R = 6378136.98f;
            float r = R * MathF.Cos(0.5f * (min_lat + max_lat) / 180f * MathF.PI);
            float lon_diff = max_lon - min_lon;
            float map_width = lon_diff / 360f * 2f * MathF.PI * r;
            float map_height = (max_lat - min_lat) / 360f * 2f * MathF.PI * R;
            Log.Info($"(ComputeActualMapExtent) Map size at center = {map_width / 1000f:F3} x {map_height / 1000f:F3}");
        }

        public static async void FetchMapTiles(object tuple)
        {
            Tuple<object, object> args = tuple as Tuple<object, object>;
            BlockingCollection<TileFetchRequest> input_queue = args.Item1 as BlockingCollection<TileFetchRequest>;
            ConcurrentQueue<Tuple<string, object, string>> result_queue = args.Item2 as ConcurrentQueue<Tuple<string, object, string>>;
            
            TileFetchRequest request;
            int min_i, max_i, min_j, max_j;
            int zoom;
            string[] tile_servers;
            string map_name;

            while (true)
            {
                request = input_queue.Take();

                min_i = request.Item1;
                max_i = request.Item2;
                min_j = request.Item3;
                max_j = request.Item4;
                zoom = request.Item5;
                tile_servers = request.Item6;
                map_name = request.Item7;

                Log.Info($"(tile fetch) Fetching tiles for map '{map_name}'");

                // get tile extent, zoom, server list

                int nrows = max_j - min_j + 1;
                int ncols = max_i - min_i + 1;
                int num_tiles_to_fetch = nrows * ncols;

                Log.Info($"(tile fetch) Retrieving {ncols} x {nrows} OSM tiles ({ncols * TILE_SIZE} x {nrows * TILE_SIZE} pixels)");

                const int TARGA_HEADER_SIZE = 18;
                int width = ncols * TILE_SIZE;
                int height = nrows * TILE_SIZE;
                byte[] full_map_pixels = new byte[TARGA_HEADER_SIZE + width * height * 3];

                int full_map_row_size = width * 3;  // RGB
                int tile_row_size = TILE_SIZE * 4;  // BGRA
                int tiles_fetched = 0;
                int server_idx = 0;
                string url = "";

                for (int j = min_j; j <= max_j; j++)
                {
                    int destrow = (j - min_j) * TILE_SIZE;

                    for (int i = min_i; i <= max_i; i++)
                    {
                        try
                        {
                            url = tile_servers[server_idx];
                            url = url.Replace("{zoom}", zoom.ToString());
                            url = url.Replace("{x}", i.ToString());
                            url = url.Replace("{y}", j.ToString());

                            // XXX send progress update to main thread
                            //Log.Info($"(tile fetch) Fetching tile {i},{j} via {url}");

                            var rass = RandomAccessStreamReference.CreateFromUri(new Uri(url));
                            IRandomAccessStream ras = await rass.OpenReadAsync();
                            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(ras);
                            SoftwareBitmap bmp = await decoder.GetSoftwareBitmapAsync();
                            //Log.Info($"bitmap is {bmp.PixelWidth} x {bmp.PixelHeight} ({bmp.BitmapPixelFormat}, {bmp.BitmapAlphaMode})");
                            if (bmp.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                                Log.Warn("(tile fetch) Tile bitmap not in Bgra8 format, image will be broken!");

                            PixelDataProvider pdp = await decoder.GetPixelDataAsync();
                            byte[] tile_pixels = pdp.DetachPixelData();

                            // Paste tile pixels in full map image
                            int destcol = (i - min_i) * TILE_SIZE;
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

                            server_idx = (server_idx + 1) % tile_servers.Length;
                        }
                        catch (Exception e)
                        {
                            Log.Err($"(tile fetch) Exception while fetching tile {i},{j} from {url}: {e.Message}");
                            if (e.InnerException != null)
                                Log.Err($"(tile fetch) Inner exception: {e.InnerException.Message}");

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
