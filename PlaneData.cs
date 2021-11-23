using System;
using SimpleJSON;
using StereoKit;

// XXX use Vec3 for _x, _y, _z fields
// XXX rename last_... to known_...

// Data on a single plane, including code to extrapolate its position and orientation based on the received data
public class PlaneData
{
    public const string UNKNOWN_CALLSIGN_STRING = "<unknown>";

    public enum UpdateState { NORMAL, LATE, MISSING };
    public enum ViewMode { MAQUETTE, SKY };
    
    public OSMMap map;

    // Last data received from opensky

    public string id;                   // 24-bit ICAO address as string
    public string callsign;             // KL1234. Might initially be empty, and then get set on a later data update
    // public string last_type;         // A-330, GRND, etc

    public float session_time_received; // Time.Totalf when this data point was last updated
    public int update_timestamp;        // Update timestamp (seconds since UNIX epoch) associated with data update from opensky
    public UpdateState updateState;

    public float last_lat, last_lon;    // degrees
    public float last_heading;          // [0,360[ degrees
    public float last_altitude;         // meters
    public float last_speed;            // m/s
    public float last_vertical_rate;    // m/s

    protected float last_x, last_y, last_z;        // Map-centric location (units = km, origin = map origin)
    protected float dx, dy, dz;                    // Change in x, y, z (units = km/s)

    // Extrapolated position and orientation
    public float computed_climb_angle;
    public float computed_height;
    public Vec3 computed_position;

    public PlaneData(string id, OSMMap map)
    {
        this.id = id;
        updateState = UpdateState.NORMAL;
        callsign = UNKNOWN_CALLSIGN_STRING;
        this.map = map;
    }
    public void ProcessDataUpdate(float session_time_received, JSONNode node)
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

        if (callsign == UNKNOWN_CALLSIGN_STRING)
        {
            string s = ((string)(node[1])).Trim();
            if (s != "")
            {
                Log.Info($"Got callsign for {id}: {s}");
                this.callsign = s;
            }
        }

        if (updateState != UpdateState.NORMAL)
        {
            Log.Info($"Plane {id} ({callsign}) came back alive");
            updateState = UpdateState.NORMAL;
        }

        this.session_time_received = session_time_received;
        this.update_timestamp = time_position;

        last_lon = node[5];            // 4. ...
        last_lat = node[6];            // 52. ...
        last_heading = node[10];       // 0-360
        last_altitude = node[13];      // geometric altitude (meters)
        last_speed = node[9];          // velocity over ground (m/s)
        last_vertical_rate = node[11]; // vertical climb rate in m/s, >0 = climbing, <0 = descending

        // vertical_rate > 0 (= climb) implies negative X rotation
        computed_climb_angle = 0.0f;
        if (last_speed > 1.0e-6f)
            computed_climb_angle = -MathF.Atan(last_vertical_rate / last_speed) / MathF.PI * 180.0f;

        // Compute speed vectors used for extrapolating position
        float speed_km_s = last_speed / 1000.0f;                    // m/s -> km/s
        // XXX any point to interpolate this? we don't have an angular (i.e. heading rate) speed
        float heading_radians = last_heading * MathF.PI / 180f;
        dx = MathF.Sin(heading_radians) * speed_km_s;
        dy = MathF.Cos(heading_radians) * speed_km_s;
        dz = last_vertical_rate / 1000.0f;                          // m/s -> km/s

        // Maquette mode
        {
            // Simple projection, based on map projection (disregards Earth curvature)
            last_x = last_y = 0.0f;
            map.Project(ref last_x, ref last_y, this.last_lon, this.last_lat);
            last_z = this.last_altitude / 1000.0f;

            computed_position = new Vec3(last_x, last_y, last_z);
        }
        /*else if (viewMode == ViewMode.SKY)
        {
            // Compute point p on Earth surface, relative to map center lat/lon

            Matrix M =
                Matrix.R(Quaternion.AngleAxis(-last_lon, new Vector3(0f, 0f, 1f)))
                *
                Matrix.R(Quaternion.AngleAxis(last_lat, new Vector3(1f, 0f, 0f)));

            Vec3 p = new Vec3(0.0f, Projection.RADIUS_KILOMETERS + 0.001f * last_height, 0.0f);

            p = map.EarthToMapCentric.Transform(M.Transform(p));
            //Log.Debug(String.Format("lat {0:F6}, lon {1:F6} -> p = {2:F6}, {3:F6}, {4:F6} (map centric)", last_lat, last_lon, p.x, p.y, p.z));

            last_x = p.x;
            last_y = p.y;
            last_z = p.z;

            //Log.Debug(String.Format("lat = {0:F6}, lon = {1:F6}, height = {2:F6} -> {3:F6}, {4:F6}, {5:F6}", last_lat, last_lon, last_height, last_x, last_y, last_z));
        }*/
    }

    public void Update(float session_time)
    {
        if (updateState == UpdateState.MISSING)
        {
            // Don't update position until it comes back alive again
            return;
        }

        // In both NORMAL and LATE data states we keep the plane moving
        float t_diff = session_time - session_time_received;

        // Extrapolate position based on last data received
        computed_position = new Vec3(last_x + dx * t_diff, last_y + dy * t_diff, last_z + dz * t_diff);
        computed_height = last_altitude + t_diff * last_vertical_rate;

        // Determine new state

        if (updateState == UpdateState.NORMAL && t_diff > 60.0f)
        {
            Log.Info(String.Format("Marking plane {0} ({1}) LATE, as we haven't had data updates in {2:F3}s", id, callsign, t_diff));
            updateState = UpdateState.LATE;
        }
        else if (updateState == UpdateState.LATE && t_diff > 120.0f)
        {
            // Haven't had update for a long time, mark as missing
            Log.Info(String.Format("Making plane {0} ({1}) MISSING as we haven't had data updates  in {2:F3}s", id, callsign, t_diff));
            updateState = UpdateState.MISSING;
        }
    }
};
