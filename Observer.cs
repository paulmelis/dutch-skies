using System.Collections.Generic;
using StereoKit;
using SimpleJSON;

namespace DutchSkies
{
    public class Observer
    {
        public string id;
        public float lat, lon;
        public float floor_altitude;  // meters
        public Vec3 map_position;
        public bool on_map;         // XXX 

        public Observer(string id, float lat, float lon, float floor_altitude)
        {
            this.id = id;
            this.lat = lat;
            this.lon = lon;
            this.floor_altitude = floor_altitude;
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
    }

    public class ObserverSet
    {
        public string id;
        public Dictionary<string, Observer> observers;
        public string default_observer;

        public ObserverSet(string id)
        {
            this.id = id;
            observers = new Dictionary<string, Observer>();
            default_observer = "";
        }

        public void Add(string ob_id, Observer observer)
        {
            Log.Info($"ObserverSet.Add '{ob_id}'");
            observers[ob_id] = observer;
            if (observers.Count == 1)
                default_observer = ob_id;
        }
    }
}