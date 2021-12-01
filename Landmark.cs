using StereoKit;

namespace DutchSkies
{
    public class Landmark
    {
        public string id;
        public float lat, lon;
        public float top_altitude;
        public float bottom_altitude;
        public float height;
        public Vec3 map_position;       // XXX currently unused
        public Vec3 sky_position;

        public Landmark(string id, float lat, float lon, float topalt, float botalt = 0f)
        {
            this.id = id;
            this.lat = lat;
            this.lon = lon;
            top_altitude = topalt;
            bottom_altitude = botalt;
            height = top_altitude - bottom_altitude;
            map_position = new Vec3();
            sky_position = new Vec3();
        }
    };
}
