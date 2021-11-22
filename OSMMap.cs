using System;
using System.Collections.Generic;
using StereoKit;

public struct MapConfiguration
{
    public MapConfiguration(float minlat, float maxlat, float minlon, float maxlon, int zm, int width, int height)
    {
        this.min_lat = minlat;
        this.max_lat = maxlat;
        this.min_lon = minlon;
        this.max_lon = maxlon;
        this.zoom = zm;
        this.image_width = width;
        this.image_height = height;
    }

    public float min_lat, max_lat;
    public float min_lon, max_lon;
    public int zoom;

    public int image_width, image_height;
}

public class OSMMap 
{
    public MapConfiguration current_configuration;
    public string current_configuration_name;

    public float center_lat, center_lon;
    public float width, height;             // kilometers

    public float min_x, max_x;              // EPSG 3857 extents
    public float min_y, max_y;

    public Matrix EarthToMapCentric;

    protected Dictionary<string, MapConfiguration>  configurations;

    public OSMMap()
    {
        configurations = new Dictionary<string, MapConfiguration>();

        // Whole of the Netherlands, center south-west of Soest
        configurations["netherlands"] = new MapConfiguration(
            50.513427f, 53.748711f, 2.812500f, 7.734375f, 
            10, 3584, 3840
        );

        // Schiphol
        configurations["schiphol"] = new MapConfiguration(
            52.106505f, 52.536273f, 4.130859f, 5.361328f,
            12, 3584, 2048
        );

        // Home
        configurations["home"] = new MapConfiguration(
            52.350860f, 52.353796f, 4.930115f, 4.937668f, 
            19, 2816, 1792
        );

        // Office
       configurations["office"] = new MapConfiguration(
            52.355474f, 52.358409f, 4.951401f, 4.958954f,
            19, 2816, 1792
        );

        Switch("netherlands");
    }

    public void Switch(string name)
    {
        Log.Info("OSMMap.Switch " + name);

        // XXX Update texture on map geometry

        // Update projection and such
        current_configuration = configurations[name];
        current_configuration_name = name;

        this.center_lat = 0.5f * (current_configuration.min_lat + current_configuration.max_lat);
        this.center_lon = 0.5f * (current_configuration.min_lon + current_configuration.max_lon);
        Log.Info("Center lat = " + this.center_lat + ", lon = " + this.center_lon);

        // XXX update to handle XY plane map
        // Given spherical Earth right-handed model with 
        // - Radius = R = RADIUS_KILOMETERS
        // - +Y axis through the north pole
        // - +X "east", i.e. through lat=0, lon=90, i.e. (R, 0, 0)
        // - +Z through lat=0, lon=0, i.e. (0, 0, R)
        // Compute transform needed to rotate and translate map center at (lat, lon) located
        // on the Earth surface onto (0, 0, 0), matching the local Up axis at (lat, lon) to the +Y axis
        EarthToMapCentric =
            Matrix.R(0f, -this.center_lon, 0f)
            *
            Matrix.R((90f-this.center_lat), 0f, 0f)
            *
            Matrix.T(new Vec3(0f, -Projection.RADIUS_KILOMETERS, 0f))
            ;

#if true
        Log.Info("EarthToMapCentric(0,0,R) -> "+EarthToMapCentric.Transform(new Vec3(0, 0, Projection.RADIUS_KILOMETERS)));
        Log.Info("EarthToMapCentric(0,0,R+12) -> "+EarthToMapCentric.Transform(new Vec3(0, 0, Projection.RADIUS_KILOMETERS+12.0f)));

        Matrix M =
            Matrix.R(-(90f-this.center_lat), 0f, 0f)
            *
            Matrix.R(0f, this.center_lon, 0f)
            ;

        Vec3 p = new Vec3(0f, Projection.RADIUS_KILOMETERS + 12f, 0f);

        p = EarthToMapCentric.Transform(M.Transform(p));
        Log.Info(String.Format("center lat, lon at 12km -> p = {0:F6}, {1:F6}, {2:F6} (map centric)", p.x, p.y, p.z));
#endif
        // Compute width of map at center latitude
        float r = Projection.RADIUS_KILOMETERS * MathF.Cos(center_lat / 180.0f * MathF.PI);    // Radius at center latitude in kilometers
        width = (current_configuration.max_lon - current_configuration.min_lon) / 360.0f * 2.0f * MathF.PI * r;
        height = 1.0f * current_configuration.image_height / current_configuration.image_width * width;
        Log.Info("Map size (kilometers) = " + width + " x " + height);

        // XXX this gives an approximate x,y range
        float xx = 0.0f, yy = 0.0f;
        Projection.epsg4326_to_epsg3857(ref min_x, ref yy, current_configuration.min_lon, center_lat);
        Projection.epsg4326_to_epsg3857(ref max_x, ref yy, current_configuration.max_lon, center_lat);
        Projection.epsg4326_to_epsg3857(ref xx, ref min_y, center_lon, current_configuration.min_lat);
        Projection.epsg4326_to_epsg3857(ref xx, ref max_y, center_lon, current_configuration.max_lat);

        Log.Info("Map X range: " + min_x + ", " + max_x);
        Log.Info("Map Y range: " + min_y + ", " + max_y);

        // EPSG:4326 lon 5, lat 52 -> EPSG:3857 556597.45, 6800125.45 -> -18.68516, -17.84164 (NL map centric, center = 5.27, 52.13)
        //Project(ref xx, ref yy, 5f, 52f);
        //Log.Info($"lon 5, lat 52 -> {xx}, {yy}");
    }

    // Output x and y are in kilometers, relative to the map center
    public void Project(ref float x, ref float y, float lon, float lat)
    {
        float xx = 0.0f;
        float yy = 0.0f;

        Projection.epsg4326_to_epsg3857(ref xx, ref yy, lon, lat);
        //Log.Info($"project() {lon}, {lat} -> x {xx}, y {yy}");

        // XXX can precompute x and y extents
        x = ((xx - min_x) / (max_x - min_x) - 0.5f) * width;
        y = ((yy - min_y) / (max_y - min_y) - 0.5f) * height;
    }
}
