using System;
using System.Collections.Generic;
using SimpleJSON;
using StereoKit;

// XXX rename last_... to known_...?

namespace DutchSkies
{

    // Data on a single plane, including code to extrapolate its position and orientation based on the received data
    public class PlaneData
    {
        public const string UNKNOWN_CALLSIGN_STRING = "<unknown>";

        public enum UpdateState { NORMAL, LATE, MISSING };

        // Last data received from opensky

        public string id;                   // 24-bit ICAO address as string
        public string callsign;             // KL1234. Might initially be empty, and then get set on a later data update
                                            // public string last_type;         // A-330, GRND, etc

        public float session_time_received; // Time.Totalf when this data point was last updated
        public int update_timestamp;        // Update timestamp (seconds since UNIX epoch) associated with data update from opensky
        public UpdateState update_state;

        public float last_lat, last_lon;    // degrees
        public float last_heading;          // [0,360[ degrees
        public float last_geometric_altitude;         // meters
        public bool have_geometric_altitude;
        public float last_barometric_altitude;         // meters
        public bool have_barometric_altitude;
        public float last_velocity;         // m/s
        public float last_vertical_rate;    // m/s
        public bool on_ground;

        protected Vec3 last_map_position;   // Map-centric location (units = km, origin = map origin)
        protected Vec3 last_sky_position;   // Sky-centric location (units = m, origin = observer location)
        protected Vec3 last_delta;          // Change in x, y, z (units = km/s)
        
        public List<Vec3> track_points;     // OpenSky data
        public List<Vec3> map_track_points; // Map-centric

        // Extrapolated position and orientation
        public Vec3 computed_map_position;  // Map units (kilometers)
        public Vec3 computed_sky_position;  // Meters
        public float computed_barometric_altitude;     // Meters
        public float computed_geometric_altitude;
        public float computed_climb_angle;  // Degrees
        public float observer_distance;     // Kilometers

        public Vec3 previous_map_position;
        public Vec3 previous_sky_position;

        protected bool first_data;

        public PlaneData(string id)
        {
            this.id = id;
            update_state = UpdateState.NORMAL;
            callsign = UNKNOWN_CALLSIGN_STRING;
            last_geometric_altitude = 0f;
            last_barometric_altitude = 0f;
            have_geometric_altitude = false;
            have_barometric_altitude = false;
            first_data = true;
            track_points = new List<Vec3>();
            map_track_points = new List<Vec3>();
        }

        public void ProcessDataUpdate(float session_time_received, JSONNode node, OSMMap map, ObserverData observer)
        {
            if (node[3] == null)
            {
                Log.Warn($"Plane {id}: time_position is null, ignoring data update!");
                return;
            }

            int time_position = node[3];

            if (time_position == update_timestamp)
            {
                // Same data as previously received, ignore
                return;
            }

            if (!first_data)
            {
                previous_map_position = last_map_position;
                previous_sky_position = last_sky_position;
            }

            if (callsign == UNKNOWN_CALLSIGN_STRING)
            {
                string s = ((string)(node[1])).Trim();
                if (s != "")
                {
                    //Log.Info($"Got callsign for {id}: {s}");
                    this.callsign = s;
                }
            }

            if (update_state != UpdateState.NORMAL)
            {
                Log.Info($"Plane {id} ({callsign}) came back alive");
                update_state = UpdateState.NORMAL;
            }

            this.session_time_received = session_time_received;
            this.update_timestamp = time_position;

            have_geometric_altitude = have_barometric_altitude = false;

            last_lon = node[5];             // 4. ...
            last_lat = node[6];             // 52. ...
            last_heading = node[10];        // 0-360
            if (node[7] != null)            // barometric altitude (meters)
            {
                last_geometric_altitude = node[7];    
                have_geometric_altitude = true;
            }
            if (node[13] != null)      // geometric altitude (meters)
            {
                last_barometric_altitude = node[13];   
                have_barometric_altitude = true;
            }
            last_velocity = node[9];        // velocity over ground (m/s)
            last_vertical_rate = node[11];  // vertical climb rate in m/s, >0 = climbing, <0 = descending
            on_ground = node[8];

            // vertical_rate > 0 (= climb) implies negative X rotation
            computed_climb_angle = 0.0f;
            if (last_velocity > 1.0e-6f)
                computed_climb_angle = -MathF.Atan(last_vertical_rate / last_velocity) / MathF.PI * 180.0f;

            // Compute speed vectors used for extrapolating position, m/s -> km/s
            float speed_km_s = last_velocity / 1000.0f;
            // XXX any point to interpolate this? we don't have an angular (i.e. heading rate) speed
            float heading_radians = last_heading * MathF.PI / 180f;

            // In km/s
            last_delta = new Vec3(
                MathF.Sin(heading_radians) * speed_km_s,
                MathF.Cos(heading_radians) * speed_km_s,
                last_vertical_rate / 1000.0f
            );

            // Map
            // Simple projection, based on map projection (disregards Earth curvature)

            float last_x, last_y;
            map.Project(out last_x, out last_y, last_lon, last_lat);

            //Log.Info($"[{id}] {last_lat:F6}, {last_lon:F6} -> {last_x:F6}, {last_y:F6}");

            //float last_geometric_altitude_km = last_geometric_altitude / 1000f;
            float last_barometric_altitude_km = last_barometric_altitude / 1000f;

            // Map position is based on barometric altitude
            last_map_position = new Vec3(last_x, last_y, last_barometric_altitude_km);
            computed_map_position = last_map_position;

            // Track points
            if (!on_ground)
            {
                track_points.Add(new Vec3(last_lat, last_lon, last_barometric_altitude));
                map_track_points.Add(last_map_position);
            }
            else
                ClearTrack();

            MapChange(map);

            // Sky position, based on barometric altitude

            // Given spherical Earth right-handed model with 
            // - Radius = R = RADIUS_KILOMETERS
            // - +Y axis through the north pole
            // - +X "east", i.e. through lat=0, lon=90, i.e. (R, 0, 0)
            // - +Z through lat=0, lon=0, i.e. (0, 0, R)
            // Compute transform needed to rotate and translate observer location (lat, lon) located
            // on the Earth surface onto (0, 0, 0), matching the local Up axis at (lat, lon) to the +Z axis

            // XXX a number of these can be precomputed and stored in ObserverData
            Matrix M =
                Matrix.R(-last_lat, 0f, 0f)
                *
                Matrix.R(0f, last_lon - observer.lon, 0f)
                *
                Matrix.R(observer.lat, 0f, 0f)
                *
                // XXX Should also include head-above-floor distance, but the effect will be minimal
                Matrix.T(0f, 0f, -(Projection.RADIUS_METERS + observer.floor_altitude + 1.5f));

            Vec3 p = new Vec3(0f, 0f, Projection.RADIUS_METERS + last_geometric_altitude);
            
            last_sky_position = M.Transform(p);

            // XXX distance from projection center, not head position, but should relatively be only very little off
            observer_distance = last_sky_position.Length / 1000f;
            //Log.Info($"[{callsign}] lat {last_lat:F6}, lon {last_lon:F6} -> p = {last_sky_position.x:F6}, {last_sky_position.y:F6}, {last_sky_position.z:F6}; dist {sky_distance:F0} km (sky position)");

            if (first_data)
            {
                previous_map_position = last_map_position;
                previous_sky_position = last_sky_position;
            }

            first_data = false;
        }
        public void MapChange(OSMMap map)
        {
            // Map
            // Simple projection, based on map projection (disregards Earth curvature)

            float last_x, last_y;
            map.Project(out last_x, out last_y, last_lon, last_lat);

            //Log.Info($"[{id}] {last_lat:F6}, {last_lon:F6} -> {last_x:F6}, {last_y:F6}");

            //float last_geometric_altitude_km = last_geometric_altitude / 1000f;
            float last_barometric_altitude_km = last_barometric_altitude / 1000f;

            // Map position is based on barometric altitude
            last_map_position = new Vec3(last_x, last_y, last_barometric_altitude_km);
            computed_map_position = last_map_position;

            // Recompute map track points
            map_track_points.Clear();
            foreach (Vec3 p in track_points)
            {                
                map.Project(out last_x, out last_y, p.y, p.x);
                map_track_points.Add(new Vec3(last_x, last_y, p.z / 1000f));
            }
        }

        public void ObserverChange(ObserverData observer)
        {
            Matrix M =
                Matrix.R(-last_lat, 0f, 0f)
                *
                Matrix.R(0f, last_lon - observer.lon, 0f)
                *
                Matrix.R(observer.lat, 0f, 0f)
                *
                // XXX Should also include head-above-floor distance, but the effect will be minimal
                Matrix.T(0f, 0f, -(Projection.RADIUS_METERS + observer.floor_altitude + 1.5f));

            Vec3 p = new Vec3(0f, 0f, Projection.RADIUS_METERS + last_geometric_altitude);

            last_sky_position = M.Transform(p);
            computed_sky_position = last_sky_position;
            previous_sky_position = last_sky_position;

            // XXX distance from projection center, not head position, but should relatively be only very little off
            observer_distance = last_sky_position.Length / 1000f;
            //Log.Info($"[{callsign}] lat {last_lat:F6}, lon {last_lon:F6} -> p = {last_sky_position.x:F6}, {last_sky_position.y:F6}, {last_sky_position.z:F6}; dist {sky_distance:F0} km (sky position)");
        }

        // update_time is seconds since Unix epoch
        public void Update(double update_time)
        {
            if (update_state == UpdateState.MISSING)
            {
                // Don't update position until it comes back alive again
                return;
            }

            // In both NORMAL and LATE data states we keep the plane moving
            // Extrapolate positions based on last data received
            float t_diff = (float)(update_time - update_timestamp);
            computed_barometric_altitude = last_barometric_altitude + t_diff * last_vertical_rate;
            computed_geometric_altitude = last_geometric_altitude + t_diff * last_vertical_rate;
            computed_map_position = last_map_position + t_diff * last_delta;
            computed_sky_position = last_sky_position + t_diff * last_delta * 1000f;

            // Determine new state

            if (update_state == UpdateState.NORMAL && t_diff > 60.0f)
            {
                Log.Info($"Marking plane {id} ({callsign}) LATE, as we haven't had data updates in {t_diff:F3}s");
                update_state = UpdateState.LATE;
            }
            else if (update_state == UpdateState.LATE && t_diff > 120.0f)
            {
                // Haven't had update for a long time, mark as missing
                Log.Info($"Marking plane {id} ({callsign}) MISSING as we haven't had data updates  in {t_diff:F3}s");
                update_state = UpdateState.MISSING;
            }
        }

        public void ClearTrack()
        {
            track_points.Clear();
            map_track_points.Clear();
        }
    };

}
