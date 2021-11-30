using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Networking;
using Windows.Networking.Connectivity;
using SimpleJSON;
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

            public Landmark(string id, float lat, float lon, float topalt, float botalt=0f)
            {
                this.id = id;
                this.lat = lat;
                this.lon = lon;
                this.top_altitude = topalt;
                this.bottom_altitude = botalt;
                height = topalt - botalt;
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
            map_position = new Vec3(x, y, floor_altitude/1000f);
            Log.Info($"observer map pos = {x:F6}, {y:F6}");

            Matrix M;

            foreach (KeyValuePair<string,Landmark> item in landmarks)
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

    public class MapConfiguration
    {
        public MapConfiguration(string name, float minlat, float maxlat, float minlon, float maxlon, int zm, int width, int height)
        {
            this.name = name;
            this.min_lat = minlat;
            this.max_lat = maxlat;
            this.min_lon = minlon;
            this.max_lon = maxlon;
            this.zoom = zm;
            this.image_width = width;
            this.image_height = height;
            this.texture = null;
        }

        public string name;
        public float min_lat, max_lat;
        public float min_lon, max_lon;
        public int zoom;

        public Tex texture;
        public int image_width, image_height;
    };


    class Program
    {
        enum DetailLevel { NONE, CALLSIGN, FULL };

        static List<string> log_lines = new List<string>();
        static string log_text = "";

        static void Main(string[] args)
        {
            // To get , as thousand separator
            CultureInfo.CurrentCulture = CultureInfo.CreateSpecificCulture("en-US");

            // Initialize StereoKit
            SKSettings settings = new SKSettings
            {
                appName = "DutchSkies",
                assetsFolder = "Assets",
                //displayPreference = DisplayMode.Flatscreen,
                //noFlatscreenFallback = true,
            };

            if (!SK.Initialize(settings))
                Environment.Exit(1);

            // Set up log window
            Log.Subscribe(OnLog);

            // Tweak renderer
            Renderer.SetClip(0.08f, 10000f);
            Renderer.EnableSky = false;

            // Determine IP address
            string our_ip = "<unknown>";
            foreach (HostName localHostName in NetworkInformation.GetHostNames())
            {
                if (localHostName.IPInformation != null)
                {
                    if (localHostName.Type == HostNameType.Ipv4)
                    {
                        our_ip = localHostName.ToString();
                        break;
                    }
                }
            }

            // Maps

            const float REALWORLD_MAP_WIDTH = 1.5f; // meters

            Matrix ROT_MIN90_X = Matrix.R(-90f, 0f, 0f);
            Matrix ROT_180_Y = Matrix.R(0f, 180f, 0f);
            Matrix MAP_PLACEMENT_XFORM = Matrix.T(1f * Vec3.Forward - 0.7f * Vec3.Up);

            // Configurations

            Dictionary<string, MapConfiguration> map_configurations = new Dictionary<string, MapConfiguration>();

            // Whole of the Netherlands
            map_configurations["netherlands"] = new MapConfiguration(
                "The Netherlands",
                50.513427f, 53.956086f, 2.812500f, 8.085938f,
                10, 3840, 4096
            );

            map_configurations["netherlands"].texture = Tex.FromFile("Maps\\netherlands-lon-2.812500-8.085938-lat-50.513427-53.956086-c-5.449219-52.234756-z10-3840x4096.png");

            // Schiphol
            map_configurations["schiphol"] = new MapConfiguration(
                "Schiphol Airport",
                52.052490f, 52.536273f, 4.042969f, 5.361328f,
                12, 3840, 2304
            );

            map_configurations["schiphol"].texture = Tex.FromFile("Maps\\schiphol-lon-4.042969-5.361328-lat-52.052490-52.536273-c-4.702148-52.294382-z12-3840x2304.png");

            // Map
            OSMMap osm_map = new OSMMap();

            // Set current map
            osm_map.Switch(map_configurations["netherlands"]);
            float map_geo_height = REALWORLD_MAP_WIDTH * osm_map.height / osm_map.width;
            float map_scale_km_to_scene = REALWORLD_MAP_WIDTH / osm_map.width;
            Log.Info($"Map geometry size = {REALWORLD_MAP_WIDTH} x {map_geo_height}");
            Mesh map_quad = Mesh.GeneratePlane(new Vec2(REALWORLD_MAP_WIDTH, map_geo_height), -Vec3.Forward, Vec3.Up);
            Material map_material = Default.Material.Copy();

            // Update queue
            ConcurrentQueue<Tuple<string, object>> updates = new ConcurrentQueue<Tuple<string, object>>();

#if true
            Tex map_texture = osm_map.current_configuration.texture;
            map_material[MatParamName.DiffuseTex] = map_texture;
            // Disable backface culling on the map for now, for debugging
            map_material.FaceCull = Cull.None;
#else
            // XXX map extent is off when dynamically tiles
            Tex map_texture = null;
            var map_thread = new Thread(OSMTiles.FetchMapTiles);
            map_thread.IsBackground = true;
            map_thread.Start(new Tuple<ConcurrentQueue<Tuple<string,object>>, MapConfiguration>(updates, osm_map.current_configuration));
            Log.Info("Map tile fetch thread started");
#endif
            // Plane 3D model
            Model plane_model = Model.FromFile("Airplane-cleaned.rotated.glb");
            if (plane_model == null)
                Log.Err("Could not load plane model");

            const float PLANE_SIZE_M = 0.015f;  // Decent size
            Matrix MAP_SCALE_PLANE_SIZE = Matrix.S(PLANE_SIZE_M);

            // XXX need to figure out why the marker needs to be much smaller compared to the plane model, doesn't make sense
            Mesh plane_ground_marker = Mesh.GenerateCylinder(0.001f, 0.002f, Vec3.UnitY, 8);
            Material plane_marker_material = Default.Material.Copy();
            plane_marker_material[MatParamName.ColorTint] = new Color(0f, 0f, 1f);

            ObserverData observer = new ObserverData();
            // XXX needs to update when switching maps
            observer.update_map_position(osm_map);
            Mesh observer_marker = Mesh.GenerateCylinder(0.001f, 0.01f, Vec3.UnitY, 8);
            Material observer_marker_material = Default.Material.Copy();
            observer_marker_material[MatParamName.ColorTint] = new Color(1f, 0.5f, 0f);

            // Floor (for non-seethrough devices)

            Matrix floorTransform = Matrix.TS(0, -1.5f, 0, new Vec3(30, 0.1f, 30));
            Material floorMaterial = new Material(Shader.FromFile("floor.hlsl"));
            floorMaterial.Transparency = Transparency.Blend;

            // Launch data update thread            
            var data_thread = new Thread(FetchPlaneUpdates);
            data_thread.IsBackground = true;
            data_thread.Start(updates);
            Log.Info("Data thread started");

            // Initial head pose in physical space is apparently taken as origin, with
            // head view direction as forward (-Z), Y is up, X to the right, i.e. right-handed
            //
            // Line.AddAxis shows an axis with these directions:
            // Red = Vec3.Right = +X
            // Green = Vec3.Up = +Y
            // Blue = Vec3.Forward = -Z (NOTE!)

            Pose main_window_pose = new Pose(0.5f, -0.2f, -0.5f, Quat.LookDir(-1, 0, 1));
            Pose log_window_pose = new Pose(0.9f, -0.2f, 0f, Quat.LookDir(-1, 0, 1));

            DetailLevel detail_level = DetailLevel.FULL;
            bool show_log_window = true;
            bool show_flight_units = false;
            bool map_visible = true;
            bool map_show_planes = true, sky_show_planes = true;
            bool map_show_vlines = true, sky_show_vlines = false;
            bool map_show_track_lines = true, sky_show_trail_lines = true;
            bool map_show_observer = false;
            bool sky_show_landmarks = true;
            bool show_origin = false;
            int num_map_planes = 0;
            int sky_y_trim = 0;         // In 0.1 degree increments

            const float track_line_thickness = 0.001f;
            Color32 track_line_color = new Color32(0, 0, 255, 255);
            Color VLINE_COLOR = new Color(1f, 0f, 0f);
            Color SKY_TRACK_LINE_COLOR = new Color(0.4f, 1f, 0.4f);
            Color LANDMARK_VLINE_COLOR = new Color(1f, 0f, 1f);

            const float SKY_SCALING_THRESHOLD = 3f;
            Matrix SKY_FAR_MODEL_SCALE = Matrix.S(30f);
            Matrix SKY_CLOSE_MODEL_SCALE = Matrix.S(60f);

            TextStyle text_style_map = Text.MakeStyle(Default.Font, 0.5f * U.cm, new Color(1f, 0f, 0f));
            TextStyle text_style_sky = Text.MakeStyle(Default.Font, 15f * U.m, VLINE_COLOR);
            TextStyle text_style_landmark = Text.MakeStyle(Default.Font, 1f * U.m, LANDMARK_VLINE_COLOR);

            Dictionary<string, PlaneData> plane_data = new Dictionary<string, PlaneData>();

            Tuple<string, object> update;
            string update_type;
            JSONNode root_node;

            int fps_num_frames = 0;
            float fps_start_time = Time.Totalf;
            float fps = 0f;

            // Core application loop
            while (SK.Step(() =>
            {
                if (SK.System.displayType == Display.Opaque)
                    Default.MeshCube.Draw(floorMaterial, floorTransform);

                // World origin (for debugging)
                if (show_origin)
                    Lines.AddAxis(Pose.Identity, 0.1f);

                //
                // Process received plane data, if any
                //

                while (!updates.IsEmpty)
                {
                    // XXX handle error

                    updates.TryDequeue(out update);
                    update_type = update.Item1;

                    Log.Info($"Update type '{update_type}'");

                    if (update_type == "map_image")
                    {
                        // XXX need to handle image update that doesn't match current map
                        Log.Info("Got updated map image");
                        map_material[MatParamName.DiffuseTex] = Tex.FromMemory(update.Item2 as byte[]);
                        // Disable backface culling on the map for now, for debugging
                        map_material.FaceCull = Cull.None;
                    }
                    else if (update_type == "plane_data")
                    {
                        root_node = update.Item2 as JSONNode;
                        JSONNode states = root_node["states"];
                        Log.Info("Got {0} state updates", states.Count);

                        float update_time = Time.Totalf;

                        for (int i = 0; i < states.Count; i++)
                        {
                            JSONNode plane = states[i];

                            // 24-bit ICAO address as string
                            string id = plane[0];

                            if (!plane_data.ContainsKey(id))
                                plane_data[id] = new PlaneData(id);

                            plane_data[id].ProcessDataUpdate(update_time, plane, osm_map, observer);
                        }
                    }
                }

                //
                // Draw map and planes
                //

                double draw_time = DateTimeOffset.Now.ToUnixTimeMilliseconds() * 0.001;
                Vec3 head_pos = Input.Head.position;

                Hierarchy.Push(MAP_PLACEMENT_XFORM);

                // Map

                if (map_visible)
                    map_quad.Draw(map_material, ROT_MIN90_X);

                // Planes

                num_map_planes = 0;

                foreach (var plane in plane_data.Values)
                {
                    if (plane.updateState == PlaneData.UpdateState.MISSING)
                        continue;

                    plane.Update(draw_time);
                    num_map_planes++;

                    var pos = ROT_MIN90_X * plane.computed_map_position * map_scale_km_to_scene;

                    if (plane.on_ground)
                    {
                        // XXX could set y to 0, as there seem to be cases where a plane is marked on-ground, but has an incorrect altitude value
                        plane_ground_marker.Draw(plane_marker_material, ROT_MIN90_X * Matrix.R(0f, -plane.last_heading, 0f) * Matrix.T(pos));
                        continue;
                    }

                    if (map_show_planes)
                    {
                        //Lines.AddAxis(new Pose(plane.computed_position * map_scale_km_to_scene, Quat.FromAngles(0f, 0f, -plane.last_heading)));
                        plane_model.Draw(MAP_SCALE_PLANE_SIZE * ROT_MIN90_X * Matrix.R(-plane.computed_climb_angle * 2f, 0f, 0f)
                            * Matrix.R(0f, -plane.last_heading, 0f) * Matrix.T(pos));
                    }

                    // Plane information

                    // XXX also for sky planes?
                    string callsign = "${plane.callsign}";
                    if (plane.updateState == PlaneData.UpdateState.LATE)
                        callsign += "*";
                    else if (plane.updateState == PlaneData.UpdateState.MISSING)
                        callsign = "(" + callsign + ")";

                    if (detail_level == DetailLevel.CALLSIGN)
                    {
                        Text.Add(
                            $"{plane.callsign}",
                           ROT_180_Y * Matrix.T(pos),
                            text_style_map,
                            TextAlign.XLeft | TextAlign.YTop,
                            TextAlign.XLeft | TextAlign.YTop,
                            -0.01f, 0f);
                    }
                    else if (detail_level == DetailLevel.FULL)
                    {
                        float vrate = plane.last_vertical_rate;
                        string vstring = " ";
                        string astring = "";
                        string sstring = "";

                        if (show_flight_units)
                        {
                            sstring = $"{plane.last_velocity * 1.94384449f:N0} kn";

                            int fl = (int)MathF.Round(plane.computed_barometric_altitude / 30.48f);
                            astring = $"FL {fl:D3}";

                            vrate = vrate / 0.3048f * 60f;
                            if (vrate > 1f)
                                vstring = $"▲ {vrate:F0} ft/min";
                            else if (vrate < -1f)
                                vstring = $"▼ {-vrate:F0} ft/min";
                        }
                        else
                        {
                            sstring = $"{plane.last_velocity * 3.6f:N0} km / h";
                            astring = $"{plane.computed_barometric_altitude:N0} m";

                            if (vrate > 1f)
                                vstring = $"▲ {vrate:F0} m/s";
                            else if (vrate < -1f)
                                vstring = $"▼ {-vrate:F0} m/s";
                        }

                        Vec3 text_pos = pos;
                        TextAlign pos_align = TextAlign.XLeft | TextAlign.YTop;
                        if (pos.y < 0.05f)
                        {
                            pos_align = TextAlign.XLeft | TextAlign.YBottom;
                            text_pos.y = 0.01f;
                        }

                        Text.Add(
                            $"{plane.callsign}\n{plane.last_heading:F0}°\n{sstring}\n{astring}\n{vstring}",
                            ROT_180_Y * Matrix.T(text_pos),
                            text_style_map,
                            pos_align,
                            TextAlign.XLeft | TextAlign.YTop,
                            -0.006f, 0f);
                    }

                    // Plane lines vertically to the ground position

                    if (map_show_vlines)
                        Lines.Add(pos, new Vec3(pos.x, 0f, pos.z), VLINE_COLOR, 0.001f);

                    // Historical track
                    if (map_show_track_lines && plane.map_track_points.Count >= 2)
                    {
                        LinePoint[] lp = new LinePoint[plane.map_track_points.Count];
                        int idx = 0;
                        foreach (Vec3 p in plane.map_track_points)
                            lp[idx++] = new LinePoint(ROT_MIN90_X * p * map_scale_km_to_scene, track_line_color, track_line_thickness);
                        Lines.Add(lp);
                    }
                }

                // Observer location (on map)

                if (map_show_observer)
                {
                    Vec3 observer_pos = ROT_MIN90_X.Transform(observer.map_position) * map_scale_km_to_scene;
                    observer_marker.Draw(observer_marker_material, Matrix.T(0f, 0.005f, 0f) * Matrix.T(observer_pos));
                }

                Hierarchy.Pop();

                //
                // Draw planes in sky
                // Assumes Forward (-Z) is pointing North, although a manual trim is applied on top of that
                //

                Hierarchy.Push(Matrix.R(0f, sky_y_trim * 0.1f, 0f));

                bool scaled;

                foreach (var plane in plane_data.Values)
                {
                    var pos = ROT_MIN90_X.Transform(plane.computed_sky_position);
                    var prev_pos = ROT_MIN90_X.Transform(plane.previous_sky_position);

                    if (plane.on_ground)
                        continue;

                    // Don't bother with planes below the horizon
                    if (pos.y < 0f)
                        continue;

                    if (plane.observer_distance > SKY_SCALING_THRESHOLD)
                    {
                        // To avoid large clipping distances move plane along the line from plane to viewer,
                        // with a smaller plane model to avoid a weird scaling visual issues

                        // Vector from head to plane 
                        Vec3 v = pos - Input.Head.position;
                        v.Normalize();

                        pos *= SKY_SCALING_THRESHOLD / plane.observer_distance;
                        prev_pos *= SKY_SCALING_THRESHOLD / plane.observer_distance;
                        //Log.Info($"[{plane.callsign}] position scaled to {pos}");

                        scaled = true;
                    }
                    else
                        scaled = false;

                    if (sky_show_planes)
                    {
                        // Plane
                        if (scaled)
                        {
                            // Plane far away, draw closer but smaller
                            plane_model.Draw(SKY_FAR_MODEL_SCALE * ROT_MIN90_X * Matrix.R(0f, -plane.last_heading, 0f) * Matrix.T(pos));
                        }
                        else
                        {
                            // Plane is close, use model with "realistic" scale
                            plane_model.Draw(SKY_CLOSE_MODEL_SCALE * ROT_MIN90_X * Matrix.R(0f, -plane.last_heading, 0f) * Matrix.T(pos));
                        }
                    }

                    if (sky_show_vlines)
                    {
                        // Vertical line, start slightly below plane to make room for text
                        Lines.Add(new Vec3(pos.x, pos.y - 120f, pos.z), new Vec3(pos.x, 0f, pos.z), VLINE_COLOR, 3f);
                    }

                    if (sky_show_trail_lines)
                    {
                        // Trail line
                        Lines.Add(prev_pos, pos, SKY_TRACK_LINE_COLOR, 3f);
                    }

                    // Text labels
                    Quat textquat = Quat.LookAt(pos, head_pos, Vec3.UnitY);

                    string astring = "";
                    string sstring = "";

                    if (show_flight_units)
                    {
                        sstring = $"{plane.last_velocity * 1.94384449f:N0} kn";
                        int fl = (int)MathF.Round(plane.computed_geometric_altitude / 30.48f);
                        astring = $"FL {fl:D3} (G)";
                    }
                    else
                    {
                        sstring = $"{plane.last_velocity * 3.6f:N0} km / h";
                        astring = $"{plane.computed_geometric_altitude:N0} m (G)";
                    }

                    Text.Add(
                        $"({plane.observer_distance:F0} km)",
                        Matrix.R(textquat) * Matrix.T(pos),
                        text_style_sky,
                        TextAlign.XCenter | TextAlign.YTop,
                        TextAlign.XCenter | TextAlign.YTop,
                        0f, 30f);

                    Text.Add(
                        $"{plane.callsign}\n{astring}\n{sstring}",
                        Matrix.R(textquat) * Matrix.T(pos),
                        text_style_sky,
                        TextAlign.XCenter | TextAlign.YTop,
                        TextAlign.XCenter | TextAlign.YTop,
                        0f, -25f);
                }

                // Landmarks

                if (sky_show_landmarks)
                {
                    foreach (KeyValuePair<string, ObserverData.Landmark> item in observer.landmarks)
                    {
                        ObserverData.Landmark landmark = item.Value;
                        Vec3 pos = ROT_MIN90_X.Transform(landmark.sky_position);

                        Lines.Add(pos, new Vec3(pos.x, pos.y - landmark.height, pos.z), LANDMARK_VLINE_COLOR, 0.5f);

                        Quat textquat = Quat.LookAt(pos, head_pos, Vec3.UnitY);
                        Text.Add(
                            $"{item.Key}",
                            Matrix.R(textquat) * Matrix.T(pos),
                            text_style_landmark,
                            TextAlign.XCenter | TextAlign.YTop,
                            TextAlign.XCenter | TextAlign.YTop,
                            0f, 2f);
                    }
                }

                Hierarchy.Pop();

                // FPS counter

                fps_num_frames++;
                float now = Time.Totalf;
                if (now - fps_start_time > 0.5f)
                {
                    fps = fps_num_frames / (now - fps_start_time);
                    fps_num_frames = 0;
                    fps_start_time = now;
                }

                //
                // UI (drawn late, so we can show accurate statistics)
                //

                // Main window
                UI.WindowBegin("Dutch Skies", ref main_window_pose, new Vec2(50, 0) * U.cm, UIWin.Normal);

                UI.Toggle("Flight units", ref show_flight_units);
                UI.SameLine();
                if (UI.Button("Clear tracks"))
                {
                    foreach (var plane in plane_data.Values)
                        plane.ClearTracks();
                }
                UI.SameLine();
                UI.Label($"{plane_data.Count} planes seen, {num_map_planes} active");

                UI.HSeparator();
                UI.PushId("map");

                UI.Labe("Map:");
                UI.Toggle("Visible", ref map_visible);
                UI.SameLine();
                UI.Toggle("Planes", ref map_show_planes);
                UI.SameLine();
                UI.Toggle("VLines", ref map_show_vlines);
                UI.SameLine();
                UI.Toggle("Track lines", ref map_show_track_lines);
                UI.SameLine();
                UI.Toggle("Observer", ref map_show_observer);

                UI.Label("Plane details");
                UI.SameLine();
                if (UI.Radio("None", detail_level == DetailLevel.NONE)) detail_level = DetailLevel.NONE;
                UI.SameLine();
                if (UI.Radio("Callsign", detail_level == DetailLevel.CALLSIGN)) detail_level = DetailLevel.CALLSIGN;
                UI.SameLine();
                if (UI.Radio("Full", detail_level == DetailLevel.FULL)) detail_level = DetailLevel.FULL;

                UI.PopId();

                UI.HSeparator();
                UI.PushId("sky");

                UI.Label("Sky:");
                UI.Toggle("Planes", ref sky_show_planes);
                UI.SameLine();
                UI.Toggle("VLines", ref sky_show_vlines);
                UI.SameLine();
                UI.Toggle("Trails", ref sky_show_trail_lines);
                UI.SameLine();
                UI.Toggle("Landmarks", ref sky_show_landmarks);

                UI.Label("Y Trim");
                UI.SameLine();
                if (UI.Button("< 5")) sky_y_trim -= 50;
                UI.SameLine();
                if (UI.Button("< 1")) sky_y_trim -= 10;
                UI.SameLine();
                if (UI.Button("< ⅒")) sky_y_trim -= 1;
                UI.SameLine();
                if (UI.Button("Z")) sky_y_trim = 0;
                UI.SameLine();
                if (UI.Button("⅒ >")) sky_y_trim += 1;
                UI.SameLine();
                if (UI.Button("1 >")) sky_y_trim += 10;
                UI.SameLine();
                if (UI.Button("5 >")) sky_y_trim += 50;

                UI.PopId();

                UI.HSeparator();
                string time = DateTime.Now.ToString("HH:mm:ss");
                UI.Label("Debug:");
                UI.SameLine();
                UI.Toggle("Log", ref show_log_window);                
                UI.SameLine();
                UI.Toggle("Origin", ref show_origin);
                UI.SameLine();
                UI.Label($"IP:{our_ip}  {fps:F1} FPS  {time}");
                UI.SameLine();
                // XXX log window
                UI.WindowEnd();

                // Log window

                if (show_log_window)
                {
                    UI.WindowBegin("Log", ref log_window_pose, new Vec2(80, 0) * U.cm, UIWin.Normal);
                    UI.Text(log_text);
                    UI.WindowEnd();
                }
            })) ;

            SK.Shutdown();
        }

        static void OnLog(LogLevel level, string text)
        {
            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            if (log_lines.Count > 20)
                log_lines.RemoveAt(0);
            text = $"{time} {text}";
            log_lines.Add(text.Length < 100 ? text : text.Substring(0, 100) + "...\n");

            log_text = "";
            for (int i = 0; i < log_lines.Count; i++)
                log_text += log_lines[i];
        }

        // XXX need to make the extent dynamic, based on the current map
        static async void FetchPlaneUpdates(object update_queue_obj)
        {
            const string URL = "https://opensky-network.org/api/states/all?lamin=50.513427&lomin=2.812500&lamax=53.748711&lomax=7.734375";

            ConcurrentQueue<Tuple<string,object>> update_queue = update_queue_obj as ConcurrentQueue<Tuple<string, object>>;

            while (true)
            {
                try
                {
                    HttpClient client = new HttpClient();
                    HttpResponseMessage response = await client.GetAsync(URL);
                    response.EnsureSuccessStatusCode();
                    string body = await response.Content.ReadAsStringAsync();
                    //Log.Info("(data fetch): " + body);

                    JSONNode root_node = JSON.Parse(body);
                    update_queue.Enqueue(new Tuple<string,object>("plane_data", root_node));
                }
                catch (HttpRequestException e)
                {
                    Log.Info("(data fetch): Exception " + e.Message);
                }

                Thread.Sleep(8 * 1000);
            }
        }
    }
}
