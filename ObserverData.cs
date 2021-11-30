using System.Collections.Generic;
using StereoKit;

namespace DutchSkies
{
    public class ObserverData
    {
        public float lat, lon;
        public float floor_altitude;  // meters
        public Vec3 map_position;
        public Dictionary<string, Landmark> landmarks;
        public class Landmark
        {
            public string id;
            public float lat, lon;
            public float top_altitude;
            public float bottom_altitude;
            public float height;
            public Vec3 map_position;
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

        public ObserverData()
        {
            // SURF building at Amsterdam Science Park
            lat = 52.357036140185144f;
            lon = 4.954487434653384f;
            floor_altitude = /* street level */ -3.56f + 4f /* one floor */;
            landmarks = new Dictionary<string, Landmark>();
        }

        public void update_map_position(OSMMap map)
        {
            float x = 0f, y = 0f;
            map.Project(ref x, ref y, lon, lat);
            map_position = new Vec3(x, y, floor_altitude / 1000f);
            Log.Info($"observer map pos = {x:F6}, {y:F6}");

            Matrix M;

            foreach (KeyValuePair<string, Landmark> item in landmarks)
            {
                Landmark landmark = item.Value;
                float landmark_lat = landmark.lat;
                float landmark_lon = landmark.lon;
                float landmark_top_altitude = landmark.top_altitude;

                // Map position (unused currently)
                map.Project(ref x, ref y, landmark_lon, landmark_lat);
                item.Value.map_position = new Vec3(x, y, landmark_top_altitude / 1000f);

                M = Matrix.R(-landmark_lat, 0f, 0f)
                    *
                    Matrix.R(0f, landmark_lon - lon, 0f)
                    *
                    Matrix.R(lat, 0f, 0f)
                    *
                    // XXX Should also include have-above-floor distance, but the effect will be minimal
                    Matrix.T(0f, 0f, -(Projection.RADIUS_KILOMETERS + floor_altitude * 0.001f));

                Vec3 p = new Vec3(0f, 0f, Projection.RADIUS_KILOMETERS + landmark_top_altitude * 0.001f);

                landmark.sky_position = M.Transform(p) * 1000f;
            }
        }
    };
}