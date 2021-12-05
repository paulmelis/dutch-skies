using System.Collections.Generic;
using StereoKit;
using SimpleJSON;

namespace DutchSkies
{
    public class ObserverData
    {
        public string name;
        public float lat, lon;
        public float floor_altitude;  // meters
        public Vec3 map_position;
        public bool on_map;        

        public ObserverData()
        {
            // SURF building at Amsterdam Science Park
            name = "SURF, Amsterdam";
            lat = 52.357036140185144f;
            lon = 4.954487434653384f;
            floor_altitude = /* street level */ -3.56f + 4f /* one floor */;
            on_map = false;            
        }

        public void update_map_position(OSMMap map)
        {
            on_map = lat >= map.min_lat && lat <= map.max_lat && lon >= map.min_lon && lon <= map.max_lon;

            float x = 0f, y = 0f;
            map.Project(out x, out y, lon, lat);
            map_position = new Vec3(x, y, floor_altitude / 1000f);
            Log.Info($"Observer: lat {lat:F6}, lon {lon:F6}, alt {floor_altitude} m (map pos = {x:F6}, {y:F6})");
        }
    };
}