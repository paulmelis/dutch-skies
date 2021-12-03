using System;
using System.Collections.Generic;
using StereoKit;

namespace DutchSkies
{
    public class OSMMap
    {
        public string name;

        // EPSG:4326 (WGS84)
        public float min_lat, max_lat;
        public float min_lon, max_lon;
        public float center_lat, center_lon;
        public int zoom;

        // Image
        public Tex texture;
        
        // EPSG:3857 extents, in meters
        public float min_x, max_x;              
        public float min_y, max_y;
        public float x_extent, y_extent;

        // Approximate map size in kilometers
        public float width, height;             

        public OSMMap(string name, float minlat, float maxlat, float minlon, float maxlon, int zm=0)
        {
            this.name = name;
            min_lat = minlat;
            max_lat = maxlat;
            min_lon = minlon;
            max_lon = maxlon;
            zoom = zm;

            center_lat = 0.5f * (min_lat + max_lat);
            center_lon = 0.5f * (min_lon + max_lon);
            Log.Info("Center lat = " + center_lat + ", lon = " + center_lon);

            // Compute width of map at center latitude
            // Radius at center latitude in kilometers
            float r = Projection.RADIUS_KILOMETERS * MathF.Cos(center_lat / 180.0f * MathF.PI);    
            width = (max_lon - min_lon) / 360.0f * 2.0f * MathF.PI * r;

            // Height based on map aspect in pixels: 360.3565
            // Height based on latitudes: 360.1501
            //height = 1.0f * current_configuration.image_height / current_configuration.image_width * width;
            height = 1.0f * (max_lat - min_lat) / 360.0f * 2.0f * MathF.PI * Projection.RADIUS_KILOMETERS;

            Log.Info($"Approximate map size (kilometers) = {width:F3} x {height:F3}");

            // This gives an approximate (but reasonable) X, Y range
            float xx, yy;
            Projection.epsg4326_to_epsg3857(out min_x, out yy, min_lon, center_lat);
            Projection.epsg4326_to_epsg3857(out max_x, out yy, max_lon, center_lat);
            Projection.epsg4326_to_epsg3857(out xx, out min_y, center_lon, min_lat);
            Projection.epsg4326_to_epsg3857(out xx, out max_y, center_lon, max_lat);

            Log.Info($"Map X range: {min_x:F6}, {max_x:F6}");
            Log.Info($"Map Y range: {min_y:F6}, {max_y:F6}");

            x_extent = max_x - min_x;
            y_extent = max_y - min_y;

            // EPSG:4326 lon 5, lat 52 -> EPSG:3857 556597.45, 6800125.45 -> -18.68516, -17.84164 (NL map centric, center = 5.27, 52.13)
            //Project(ref xx, ref yy, 5f, 52f);
            //Log.Info($"lon 5, lat 52 -> {xx}, {yy}");

            // Image to be set later
            texture = null;
        }

        // Input in WGS84 coordinates
        // Output x and y are in *kilometers*, relative to the map center
        public void Project(out float x, out float y, float lon, float lat)
        {
            float xx;
            float yy;

            Projection.epsg4326_to_epsg3857(out xx, out yy, lon, lat);
            //Log.Info($"project() {lon}, {lat} -> x {xx}, y {yy}");

            x = ((xx - min_x) / x_extent - 0.5f) * width;
            y = ((yy - min_y) / y_extent - 0.5f) * height;
        }
        public bool OnMapLatLon(float lat, float lon)
        {
            return lat >= min_lat && lat <= max_lat && lon >= min_lon && lon <= max_lon;
        }

    }
}
