using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Windows.Data.Json;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Microsoft.MixedReality.QR;
using SimpleJSON;
using StereoKit;

namespace DutchSkies
{
    using URLFetchRequest = Tuple<string, string, bool, string>;
    using TileFetchRequest = Tuple<int, int, int, int, int, string[], string>;
    using PostMessageRequest = Tuple<string>;

    class Application
    {
        enum DetailLevel { NONE, CALLSIGN, FULL };
        const float PLANE_SIZE_METERS = 0.015f;

        // Some often used transforms
        static Matrix ROT_90_X = Matrix.R(-90f, 0f, 0f);
        static Matrix ROT_MIN90_X = Matrix.R(-90f, 0f, 0f);
        static Matrix ROT_180_Y = Matrix.R(0f, 180f, 0f);

        // Log
        List<string> log_lines = new List<string>();
        string log_text;
        LogLevel log_level = LogLevel.Info;

        string our_ip;
        float fps;

        // Configurations
        List<string> available_map_sets;
        List<string> available_landmark_sets;
        List<string> available_observers;

        Dictionary<string, MapSet> map_sets;

        MapSet current_map_set;
        string current_map_set_name;
        string current_landmark_set_name;
        string current_observer_name;

        // XXX no longer needed
        string configuration_name;

        // UI
        Pose main_window_pose, log_window_pose;
        bool show_log_window;
        bool show_trim_window;
        bool show_flight_units;

        // Plane data
        ConcurrentQueue<Vec4> query_extent_update_queue;
        Dictionary<string, PlaneData> plane_data;

        int num_planes_on_map;
        int num_planes_late, num_planes_missing;
        int num_planes_on_ground;

        DetailLevel detail_level;
        bool map_visible;
        bool map_show_plane_model, sky_show_plane_models;
        bool map_show_vlines, sky_show_vlines;
        bool map_show_track_lines, sky_show_trail_lines;
        bool map_show_observer;
        bool sky_show_landmarks;
        bool show_origin;

        int sky_d_trim;         // In 0.1 degree increments
        int sky_v_trim;         // In centimeters

        double draw_time;
        Vec3 head_pos;
        Quat head_orientation;

        // Map geometry 
        const float REALWORLD_MAP_WIDTH = 1.5f;     // meters
        const float MAP_WINDROSE_SIZE = 0.1f;      // meters
        float realworld_map_height;

        Dictionary<string, OSMMap> maps_in_current_set;
        string current_map_name;
        OSMMap current_map;
        Material map_material;
        float map_scale_km_to_scene;
        Mesh map_quad;
        Tex map_texture;

        // Sky mode
        // XXX prefix with sky_
        Dictionary<string, Landmark> landmarks;
        List<string> sorted_landmark_names;
        ObserverData observer;

        const float OBSERVER_WINDROSE_SIZE = 1f;    // meters
        AlignmentSolver alignment_solver;
        string current_alignment_landmark;

        bool alignment_mode;
        Pose alignment_window_pose;
        Vec3 alignment_offset;
        float alignment_rotation;
        bool use_alignment_transform;

        // Query        
        const int OPENSKY_QUERY_INTERVAL = 8;

        // XXX rename payload to marker or something
        // Thread event queue (type, data, payload)        
        ConcurrentQueue<Tuple<string, object, string>> updates_queue;
        // URL requests queue (url, type, binary, payload)
        BlockingCollection<URLFetchRequest> url_requests_queue;
        // Tile fetch queue (mini/j, maxi/j, zoom, payload)
        BlockingCollection<TileFetchRequest> tile_requests_queue;
        BlockingCollection<PostMessageRequest> message_send_queue;
        bool discord_messages_enabled;

        // QR code scanning
        bool scanning_for_qrcodes;
        QRCodeWatcher qrcode_watcher;
        DateTime qrcode_watcher_start;
        //Dictionary<Guid, QRData> poses = new Dictionary<Guid, QRData>();
        System.Guid last_qrcode_id;

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

            Application app = new Application();
            app.Run();

            SK.Shutdown();
        }

        void OnLog(LogLevel level, string text)
        {
            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            if (log_lines.Count > 20)
                log_lines.RemoveAt(0);
            text = $"{time} {text}";
            //log_lines.Add(text.Length < 120 ? text : text.Substring(0, 120) + "...\n");
            log_lines.Add(text);

            log_text = "";
            for (int i = 0; i < log_lines.Count; i++)
                log_text += log_lines[i];
        }

        public void Run()
        {
            // Set up logging
            List<string> log_lines = new List<string>();
            log_level = LogLevel.Info;
            Log.Subscribe(OnLog);
            discord_messages_enabled = false;

            // Tweak renderer
            Renderer.SetClip(0.08f, 10000f);
            Renderer.EnableSky = false;

            // Determine IP address (useful in debugging)
            our_ip = "<unknown>";
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

            // Gamepad

            XboxController xbox_controller = new XboxController();


            // Configurations

            configuration_name = "<builtin>";

            //Configuration.DeleteConfigurationsOfType(Configuration.ConfigType.MAP_SET);
            //Configuration.DeleteConfigurationsOfType(Configuration.ConfigType.LANDMARK_SET);
            //Configuration.DeleteConfigurationsOfType(Configuration.ConfigType.OBSERVER);

            current_map_set_name = "";
            current_landmark_set_name = "";
            current_observer_name = "";

            available_map_sets = new List<string>();
            available_landmark_sets = new List<string>();
            available_observers = new List<string>();

            UpdateConfigurationLists();

            Log.Info("--- Available stored configurations ---");
            
            foreach (string s in available_map_sets)
                Log.Info($"[MAP_SET] '{s}'");
            
            foreach (string s in available_map_sets)
                Log.Info($"[LANDMARK_SET] '{s}'");

            foreach (string s in available_observers)
                Log.Info($"[OBSERVER] '{s}'");

            Log.Info("--- Available stored configurations ---");

            // Planes

            plane_data = new Dictionary<string, PlaneData>();

            // Observer

            observer = new ObserverData();

            Mesh observer_cylinder_marker = Mesh.GenerateCylinder(0.002f, 0.02f, Vec3.UnitY, 8);
            Mesh observer_sphere_marker = Mesh.GenerateSphere(0.006f, 8);
            Material observer_marker_material = Default.Material.Copy();
            observer_marker_material[MatParamName.ColorTint] = new Color(1f, 0.5f, 0f);

            landmarks = new Dictionary<string, Landmark>();
            sorted_landmark_names = landmarks.Keys.ToList();
            alignment_solver = new AlignmentSolver();

            Mesh windrose_mesh = Mesh.GeneratePlane(new Vec2(1f, 1f), -Vec3.Forward, Vec3.Up);
            Material windrose_material = Material.Default.Copy();
            windrose_material[MatParamName.DiffuseTex] = Tex.FromFile("Windrose.png");
            windrose_material.Transparency = Transparency.Blend;
            windrose_material.DepthWrite = false;

            //
            // Maps
            //

            Matrix MAP_PLACEMENT_XFORM = Matrix.T(1f * Vec3.Forward - 0.7f * Vec3.Up);

            map_material = Default.Material.Copy();
            // Disable backface culling on the map for now, for debugging
            map_material.FaceCull = Cull.None;

            map_scale_km_to_scene = 0.001f;
            map_sets = new Dictionary<string, MapSet>();

            // Prepare built-in maps and select default
            PrepareBuiltinMaps();
            
            SelectMapSet("<default>");

            //
            // Some models
            //

            // Plane 3D model
            Model plane_model = Model.FromFile("Airplane-cleaned.rotated.glb");
            if (plane_model == null)
                Log.Err("Could not load plane model");

            Matrix MAP_SCALE_PLANE_SIZE = Matrix.S(PLANE_SIZE_METERS);

            // XXX need to figure out why the marker needs to be much smaller compared to the plane model, doesn't make sense
            Mesh plane_ground_marker = Mesh.GenerateCylinder(0.001f, 0.002f, Vec3.UnitY, 8);
            Material plane_marker_material = Default.Material.Copy();
            plane_marker_material[MatParamName.ColorTint] = new Color(0f, 0f, 1f);

            // Floor (for non-seethrough devices)

            //Matrix floorTransform = Matrix.TS(0, -1.5f, 0, new Vec3(30, 0.1f, 30));
            //Material floorMaterial = new Material(Shader.FromFile("floor.hlsl"));
            //floorMaterial.Transparency = Transparency.Blend;

            //
            // Start some background threads
            // XXX can probably take more advantage of async/await, but let's use threads for now
            //

            // Queue for receiving updates from threads
            updates_queue = new ConcurrentQueue<Tuple<string, object, string>>();

            // Set initial query extent, based on map (minlat, maxlat, minlon, maxlon)
            Vec4 data_query_extent = new Vec4(current_map.min_lat, current_map.max_lat, current_map.min_lon, current_map.max_lon);

            // Launch data update thread            
            // Queue for sending updated query range to thread
            query_extent_update_queue = new ConcurrentQueue<Vec4>();
            // Push initial query extent
            query_extent_update_queue.Enqueue(data_query_extent);
            var plane_update_thread = new Thread(FetchPlaneUpdates);
            plane_update_thread.IsBackground = true;
            plane_update_thread.Start(query_extent_update_queue);
            Log.Info("Plane update thread started");

            // Launch config fetch thread
            // Queue for fetching different types of data by URL
            url_requests_queue = new BlockingCollection<URLFetchRequest>(new ConcurrentQueue<URLFetchRequest>());
            var url_fetch_thread = new Thread(FetchURLThread);
            url_fetch_thread.IsBackground = true;
            url_fetch_thread.Start();
            Log.Info("URL fetch thread started");

            // Tile fetch thread
            // input: lat/lon range, zoomlevel, tile servers
            tile_requests_queue = new BlockingCollection<TileFetchRequest>(new ConcurrentQueue<TileFetchRequest>());
            var tiles_fetch_thread = new Thread(OSMTiles.FetchMapTiles);
            tiles_fetch_thread.IsBackground = true;
            tiles_fetch_thread.Start(new Tuple<object, object>(tile_requests_queue, updates_queue));
            Log.Info("Tile fetch thread started");

            message_send_queue = new BlockingCollection<PostMessageRequest>(new ConcurrentQueue<PostMessageRequest>());
            var post_messages_thread = new Thread(PostMessagesThread);
            post_messages_thread.IsBackground = true;
            post_messages_thread.Start();
            Log.Info("Message post thread started");

            // XXX
            string initial_config = "";
            //initial_config = "http://192.168.178.32:8000/config-netherlands-and-schiphol-image.json";
            //initial_config = "http://192.168.178.32:8000/config-netherlands-and-schiphol-osmtiles.json";
            //initial_config = "http://192.168.178.32:8000/config-newyork-image.json";
            //initial_config = "http://192.168.178.32:8000/config-alps-image.json";
            //initial_config = "http://192.168.178.32:8000/sanfrancisco-osmtiles.json";
            //initial_config = "https://surfdrive.surf.nl/files/index.php/s/mzR0FisZZQkKLm1/download?path=%2F&files=observer-and-landmarks-frankendael.json";
            //ScheduleURLFetch(initial_config, "config_data", false, initial_config);
            //initial_config = "http://192.168.178.32:8000/observer-and-landmarks-home-backroom.json";
            initial_config = "http://192.168.178.32:8000/config-netherlands-park.json2";

            if (initial_config != "")
                ScheduleURLFetch(initial_config, "config_data", false, initial_config);

            // Prepare for QR scanning

            // Ask for permission to use the QR code tracking system
            var status = QRCodeWatcher.RequestAccessAsync().Result;
            if (status == QRCodeWatcherAccessStatus.Allowed)
            {
                // Create watcher and set up callbacks
                qrcode_watcher = new QRCodeWatcher();
                qrcode_watcher.Added += (o, qr) =>
                {
                    //Log.Info($"(QR code Added handler) Found QR code: {qr.Code.Id} '{qr.Code.Data}'");
                    updates_queue.Enqueue(new Tuple<string, object, string>("qrcode", qr.Code, ""));
                };
                qrcode_watcher.Updated += (o, qr) =>
                {
                    //Log.Info($"(QR code Updated handler) QR code: {qr.Code.Id} '{qr.Code.Data}'");
                    updates_queue.Enqueue(new Tuple<string, object, string>("qrcode", qr.Code, ""));
                };
                //watcher.Removed += (o, qr) => poses.Remove(qr.Code.Id);
            }
            else
                Log.Info("Cannot perform QR code scanning, no permission given");

            scanning_for_qrcodes = false;

            // Alignment

            alignment_mode = false;
            alignment_window_pose = new Pose(-0.5f, -0.2f, 0f, Quat.LookDir(1, 0, 1));            
            alignment_offset = new Vec3();
            alignment_rotation = 0f;
            use_alignment_transform = false;
            current_alignment_landmark = "";

            // Main settings

            main_window_pose = new Pose(0.5f, -0.2f, -0.5f, Quat.LookDir(-1, 0, 1));
            log_window_pose = new Pose(0.9f, -0.2f, 0f, Quat.LookDir(-1, 0, 1));

            detail_level = DetailLevel.FULL;
            show_log_window = true;
            show_trim_window = false;
            show_flight_units = false;
            map_visible = true;
            map_show_plane_model = true;
            sky_show_plane_models = true;
            map_show_vlines = true;
            sky_show_vlines = false;
            map_show_track_lines = true;
            sky_show_trail_lines = true;
            map_show_observer = false;
            sky_show_landmarks = true;
            show_origin = false;

            sky_d_trim = 0;
            sky_v_trim = 0;

            num_planes_on_map = 0;
            num_planes_late = 0;
            num_planes_missing = 0;
            num_planes_on_ground = 0;

            const float TRACK_LINE_THICKNESS = 0.001f;
            Color MAP_BASE_COLOR = new Color(0.8f, 0f, 0.8f);
            Color32 MAP_TRACK_LINE_COLOR = new Color32(0, 0, 255, 255);
            Color SKY_BASE_COLOR = new Color(1f, 0f, 0f);
            Color SKY_TRAIL_LINE_COLOR = new Color(0.4f, 1f, 0.4f);
            Color LANDMARK_VLINE_COLOR = new Color(1f, 0f, 1f);
            Color ALIGNMENT_TEXT_COLOR = new Color(0f, 1f, 0f);

            Color32 ALIGNMENT_LINE_COLOR = new Color32(0, 255, 0, 255);
            const float ALIGNMENT_LINE_THICKNESS = 0.1f;

            const float SKY_SCALING_THRESHOLD = 3f;
            Matrix SKY_FAR_MODEL_SCALE = Matrix.S(30f);
            Matrix SKY_CLOSE_MODEL_SCALE = Matrix.S(60f);

            // XXX style uses gamma-space color, leading to slight difference with vline color
            TextStyle MAP_TEXT_STYLE = Text.MakeStyle(Default.Font, 0.7f * U.cm, MAP_BASE_COLOR);
            TextStyle SKY_TEXT_STYLE = Text.MakeStyle(Default.Font, 15f * U.m, SKY_BASE_COLOR);
            TextStyle LANDMARK_TEXT_STYLE = Text.MakeStyle(Default.Font, 1f * U.m, LANDMARK_VLINE_COLOR);
            TextStyle MAP_DIMENSION_TEXT_STYLE = Text.MakeStyle(Default.Font, 0.01f * U.m, Color.White);
            TextStyle ALIGNMENT_TEXT_STYLE = Text.MakeStyle(Default.Font, 0.35f * U.m, ALIGNMENT_TEXT_COLOR);

            Tuple<string, object, string> update;
            string update_type;
            JSONNode root_node;

            int fps_num_frames = 0;
            float fps_start_time = Time.Totalf;
            fps = 0f;

            SchedulePostMessage("Entering main loop");

            // Core application loop
            while (SK.Step(() =>
            {
                draw_time = DateTimeOffset.Now.ToUnixTimeMilliseconds() * 0.001;

                head_pos = Input.Head.position;
                head_orientation = Input.Head.orientation;

                //if (SK.System.displayType == Display.Opaque)
                //    Default.MeshCube.Draw(floorMaterial, floorTransform);

                // World origin (for debugging)
                if (show_origin)
                    Lines.AddAxis(Pose.Identity, 0.1f);

                //
                // Process received plane data, if any
                //

                while (!updates_queue.IsEmpty)
                {
                    // XXX handle error
                    updates_queue.TryDequeue(out update);
                    update_type = update.Item1;

                    //Log.Info($"Update type '{update_type}'");

                    if (update_type == "map_image")
                    {
                        byte[] map_image_file = update.Item2 as byte[];
                        string map_to_update = update.Item3;
                        Log.Info($"Got updated map image ({map_image_file.Length} bytes), for map '{map_to_update}'");
                        Tex texture = Tex.FromMemory(map_image_file);
                        if (texture != null)
                        {
                            maps_in_current_set[map_to_update].texture = texture;
                            if (current_map_name == map_to_update)
                                map_material[MatParamName.DiffuseTex] = maps_in_current_set[map_to_update].texture;
                        }
                        else
                            Log.Err($"Could not load map image file!");
                    }
                    else if (update_type == "plane_data")
                    {
                        root_node = update.Item2 as JSONNode;
                        JSONNode states = root_node["states"];
                        Log.Info($"Got {states.Count} state updates");

                        float update_time = Time.Totalf;

                        for (int i = 0; i < states.Count; i++)
                        {
                            JSONNode plane = states[i];

                            // 24-bit ICAO address as string
                            string id = plane[0];

                            if (!plane_data.ContainsKey(id))
                                plane_data[id] = new PlaneData(id);

                            plane_data[id].ProcessDataUpdate(update_time, plane, current_map, observer);
                        }
                    }
                    else if (update_type == "qrcode")
                    {
                        QRCode qrcode = update.Item2 as QRCode;

                        if (qrcode.LastDetectedTime <= qrcode_watcher_start || qrcode.Id == last_qrcode_id)
                            return;

                        // As soon as we find a QR code stop scanning for more and apply the data in the code
                        qrcode_watcher.Stop();
                        scanning_for_qrcodes = false;
                        last_qrcode_id = qrcode.Id;

                        // Make a noise to indicate QR code was recognized
                        Pose pose;
                        World.FromSpatialNode(qrcode.SpatialGraphNodeId, out pose);
                        Default.SoundUnclick.Play(pose.position);

                        string data = qrcode.Data;

                        Log.Info($"Got QR code {qrcode.Id} dtime {qrcode.LastDetectedTime}, starttime {qrcode_watcher_start}");
                        Log.Info($"qr data: '{data}'");
                        Log.Info($"Disabled further QR code scanning");

                        if (data.StartsWith("http://") || data.StartsWith("https://"))
                        {
                            Log.Info($"Scheduling config fetch from {data}");
                            ScheduleURLFetch(data, "config_data", false, data);
                        }
                        else
                            Log.Warn("Ignoring QR code that doesn't look like a URL");
                    }
                    else if (update_type == "config_data")
                    {
                        ProcessConfigurationData(update.Item2 as string, update.Item3);
                    }
                    else if (update_type == "map_tilefetch_progress")
                    {
                        // XXX do something with it :)
                    }
                    else
                        Log.Warn($"Unhandled update type '{update_type}!");
                }

                // Gamepad

                if (alignment_mode && xbox_controller.QueryState())
                {
                    if (xbox_controller.LeftThumbstickX < -0.2f)
                        sky_d_trim--;
                    else if (xbox_controller.LeftThumbstickX > 0.2f)
                        sky_d_trim++;

                    if (xbox_controller.Pressed(XboxController.DPAD_LEFT))
                        sky_d_trim -= 10;
                    else if (xbox_controller.Pressed(XboxController.DPAD_RIGHT))
                        sky_d_trim += 10;

                    if (xbox_controller.Pressed(XboxController.DPAD_UP))
                    {
                        int idx = sorted_landmark_names.IndexOf(current_alignment_landmark);
                        idx = Math.Max(idx - 1, 0);
                        current_alignment_landmark = sorted_landmark_names[idx];
                    }
                    else if (xbox_controller.Pressed(XboxController.DPAD_DOWN))
                    {
                        int idx = sorted_landmark_names.IndexOf(current_alignment_landmark);
                        idx = Math.Min(sorted_landmark_names.Count - 1, idx + 1);
                        current_alignment_landmark = sorted_landmark_names[idx];
                    }

                    if (!use_alignment_transform && xbox_controller.Pressed(XboxController.A))
                    {
                        var hp = Input.Head.position;
                        var ho = Input.Head.orientation.Rotate(new Vec3(0f, 0f, -1f));
                        JsonObject jo = new JsonObject();
                        jo["lm"] = JsonValue.CreateStringValue(current_alignment_landmark);
                        jo["head_pos"] = JsonValue.CreateStringValue($"[{hp.x:F6}, {hp.y:F6}, {hp.z:F6}]");
                        jo["head_ori"] = JsonValue.CreateStringValue($"[{ho.x:F6}, {ho.y:F6}, {ho.z:F6}]");
                        SchedulePostMessage(jo.ToString());
                        alignment_solver.AddObservation(current_alignment_landmark, hp, ho);
                    }
                    else if (!use_alignment_transform && xbox_controller.Pressed(XboxController.X))
                    {
                        SchedulePostMessage($"Remove observations, lm {current_alignment_landmark}, ");
                        alignment_solver.RemoveObservations(current_alignment_landmark);
                    }
                    else if (xbox_controller.Pressed(XboxController.B))
                    {
                        // The found solution transform the observations onto the world coordinate system
                        float res = alignment_solver.Solve(out alignment_offset, out alignment_rotation);

                        SchedulePostMessage($"Solve -> ({alignment_offset.x:F6}, {alignment_offset.y:F6}, {alignment_offset.z:F6}), {alignment_rotation} (energy {res:F6})");
                    }
                    else if (xbox_controller.Pressed(XboxController.Y))
                    {
                        // Toggle use of alignment
                        use_alignment_transform = !use_alignment_transform;
                    }
                }

                //
                // Draw map and planes
                //

                Hierarchy.Push(MAP_PLACEMENT_XFORM);

                // Map

                if (map_visible)
                {
                    const float ABOVE = 0.003f;

                    map_quad.Draw(map_material, ROT_MIN90_X);

                    // XXX can be precomputed, only changes when map changes
                    windrose_mesh.Draw(windrose_material,
                        ROT_MIN90_X *
                        Matrix.S(MAP_WINDROSE_SIZE) *
                        Matrix.T(-REALWORLD_MAP_WIDTH * 0.5f + 0.52f * MAP_WINDROSE_SIZE, ABOVE, realworld_map_height * 0.5f - 0.52f * MAP_WINDROSE_SIZE));

                    // Dimensions
                    Text.Add($"{current_map.width:F0} km", ROT_180_Y * ROT_MIN90_X * Matrix.T(0f, 0f, 0.5f * realworld_map_height + 0.01f), MAP_DIMENSION_TEXT_STYLE,
                        TextAlign.TopCenter);
                    Text.Add($"{current_map.height:F0} km", ROT_180_Y * ROT_MIN90_X * Matrix.R(0f, -90f, 0f) * Matrix.T(0.5f * REALWORLD_MAP_WIDTH + 0.01f, 0f, 0f), MAP_DIMENSION_TEXT_STYLE,
                        TextAlign.CenterLeft);
                }

                // Planes

                num_planes_on_map = 0;
                num_planes_on_ground = 0;
                num_planes_late = 0;
                num_planes_missing = 0;

                foreach (var plane in plane_data.Values)
                {
                    string callsign = $"{plane.callsign}";

                    if (plane.update_state == PlaneData.UpdateState.MISSING)
                    {
                        // Don't bother until it comes back alive again
                        num_planes_missing++;
                        continue;
                    }
                    else if (plane.update_state == PlaneData.UpdateState.LATE)
                    {
                        callsign = $"({callsign})";
                        num_planes_late++;
                    }

                    plane.Update(draw_time);

                    var map_pos = plane.computed_map_position;
                    var pos = ROT_MIN90_X * map_pos * map_scale_km_to_scene;

                    // XXX should use interpolated map-space position
                    if (!current_map.OnMapLatLon(plane.last_lat, plane.last_lon))
                        continue;

                    num_planes_on_map++;

                    if (plane.on_ground)
                    {
                        // XXX could set y to 0, as there seem to be cases where a plane is marked on-ground, but has an incorrect altitude value
                        plane_ground_marker.Draw(plane_marker_material, ROT_MIN90_X * Matrix.R(0f, -plane.last_heading, 0f) * Matrix.T(pos));
                        num_planes_on_ground++;
                        continue;
                    }

                    if (map_show_plane_model)
                    {
                        //Lines.AddAxis(new Pose(plane.computed_position * map_scale_km_to_scene, Quat.FromAngles(0f, 0f, -plane.last_heading)));
                        plane_model.Draw(MAP_SCALE_PLANE_SIZE * ROT_MIN90_X * Matrix.R(-plane.computed_climb_angle * 2f, 0f, 0f)
                            * Matrix.R(0f, -plane.last_heading, 0f) * Matrix.T(pos));
                    }

                    // Plane information

                    Vec3 dir = head_pos - MAP_PLACEMENT_XFORM.Transform(pos);
                    dir.y = 0f;
                    Quat textquat = Quat.LookDir(dir);

                    Vec3 text_pos = pos;
                    TextAlign pos_align = TextAlign.XLeft | TextAlign.YTop;
                    if (pos.y < 0.05f)
                    {
                        pos_align = TextAlign.XLeft | TextAlign.YBottom;
                        text_pos.y = 0.01f;
                    }

                    if (detail_level == DetailLevel.CALLSIGN)
                    {
                        Text.Add(
                            $"{callsign}",
                            Matrix.TR(pos, textquat),
                            MAP_TEXT_STYLE,
                            pos_align,
                            TextAlign.XLeft | TextAlign.YTop,
                            -0.01f, 0.00f);
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

                        Text.Add(
                            $"{callsign}\n{plane.last_heading:F0}°\n{sstring}\n{astring}\n{vstring}",
                            Matrix.TR(pos, textquat),
                            MAP_TEXT_STYLE,
                            pos_align,
                            TextAlign.XLeft | TextAlign.YTop,
                            -0.006f, 0f);
                    }

                    // Plane lines vertically to the ground position

                    if (map_show_vlines)
                        Lines.Add(pos, new Vec3(pos.x, 0f, pos.z), MAP_BASE_COLOR, 0.001f);

                    // Historical track
                    if (map_show_track_lines && plane.map_track_points.Count >= 2)
                    {
                        LinePoint[] lp = new LinePoint[plane.map_track_points.Count];
                        int idx = 0;
                        foreach (Vec3 p in plane.map_track_points)
                            lp[idx++] = new LinePoint(ROT_MIN90_X * p * map_scale_km_to_scene, MAP_TRACK_LINE_COLOR, TRACK_LINE_THICKNESS);
                        Lines.Add(lp);
                    }
                }

                // Observer location on map

                if (map_show_observer && observer.on_map)
                {
                    Vec3 observer_pos = ROT_MIN90_X.Transform(observer.map_position) * map_scale_km_to_scene;
                    observer_cylinder_marker.Draw(observer_marker_material, Matrix.T(0f, 0.005f, 0f) * Matrix.T(observer_pos));
                    observer_sphere_marker.Draw(observer_marker_material, Matrix.T(0f, 0.015f, 0f) * Matrix.T(observer_pos));
                }

                Hierarchy.Pop();

                //
                // Draw planes in sky
                // Assumes Forward (-Z) is pointing North, although a manual trim is applied on top of that
                //

                if (use_alignment_transform)
                {
                    Hierarchy.Push(
                        Matrix.R(0f, alignment_rotation - sky_d_trim * 0.1f, 0f)
                        *
                        Matrix.T(alignment_offset + new Vec3(0f, sky_v_trim * 0.1f, 0f))
                    );
                }
                else
                    Hierarchy.Push(Matrix.R(0f, -sky_d_trim * 0.1f, 0f) * Matrix.T(0f, sky_v_trim * 0.1f, 0f));

                bool scaled;

                foreach (var plane in plane_data.Values)
                {
                    if (plane.on_ground)
                        continue;

                    if (plane.update_state == PlaneData.UpdateState.MISSING)
                        continue;

                    string callsign = plane.callsign;
                    if (plane.update_state == PlaneData.UpdateState.LATE)
                        callsign = $"({callsign})";

                    var pos = ROT_MIN90_X.Transform(plane.computed_sky_position);
                    var prev_pos = ROT_MIN90_X.Transform(plane.previous_sky_position);

                    // Don't bother with planes below the horizon
                    if (pos.y < 0f)
                        continue;

                    // XXX should use inrterpolation map-space position
                    if (!current_map.OnMapLatLon(plane.last_lat, plane.last_lon))
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

                    if (sky_show_plane_models)
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
                        Lines.Add(new Vec3(pos.x, pos.y - 120f, pos.z), new Vec3(pos.x, 0f, pos.z), SKY_BASE_COLOR, 3f);
                    }

                    if (sky_show_trail_lines)
                    {
                        // Trail line
                        Lines.Add(prev_pos, pos, SKY_TRAIL_LINE_COLOR, 3f);
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
                        SKY_TEXT_STYLE,
                        TextAlign.XCenter | TextAlign.YTop,
                        TextAlign.XCenter | TextAlign.YTop,
                        0f, 30f);

                    Text.Add(
                        $"{callsign}\n{astring}\n{sstring}",
                        Matrix.R(textquat) * Matrix.T(pos),
                        SKY_TEXT_STYLE,
                        TextAlign.XCenter | TextAlign.YTop,
                        TextAlign.XCenter | TextAlign.YTop,
                        0f, -25f);
                }

                // Landmarks

                if (sky_show_landmarks)
                {
                    foreach (KeyValuePair<string, Landmark> item in landmarks)
                    {
                        Landmark landmark = item.Value;
                        Vec3 pos = ROT_MIN90_X.Transform(landmark.sky_position);

                        Lines.Add(pos, new Vec3(pos.x, pos.y - landmark.height, pos.z), LANDMARK_VLINE_COLOR, 0.5f);

                        Quat textquat = Quat.LookAt(pos, head_pos, Vec3.UnitY);
                        Text.Add(
                            $"{item.Key}",
                            Matrix.R(textquat) * Matrix.T(pos),
                            LANDMARK_TEXT_STYLE,
                            TextAlign.XCenter | TextAlign.YTop,
                            TextAlign.XCenter | TextAlign.YTop,
                            0f, 2f);
                    }
                }

                // Observer origin and orientation
                // XXX as we don't have an exact distance to the floor use a good guess
                windrose_mesh.Draw(windrose_material, ROT_MIN90_X * Matrix.S(OBSERVER_WINDROSE_SIZE) * Matrix.T(0f, -1.5f, 0f));

                Hierarchy.Pop();

                // Alignment (head coordinate space)                                

                if (alignment_mode && current_alignment_landmark != "")
                {
                    Vec2 size = new Vec2(1 * U.cm, 0);

                    Hierarchy.Push(Input.Head.ToMatrix());

                    Lines.Add(new Vec3(0f, -1.5f, -20f), new Vec3(0f, 1.5f, -20f), ALIGNMENT_LINE_COLOR, ALIGNMENT_LINE_THICKNESS);
                    Lines.Add(new Vec3(-0.2f, 0f, -20f), new Vec3(0.2f, 0f, -20f), ALIGNMENT_LINE_COLOR, ALIGNMENT_LINE_THICKNESS);
                    Text.Add($"[{alignment_solver.ObservationCount(current_alignment_landmark)}] {current_alignment_landmark}",
                        Matrix.R(0f, 180f, 0f) * Matrix.T(0f, -2f, -20f), ALIGNMENT_TEXT_STYLE);
                    Text.Add($"tx {alignment_offset.x}, tz {alignment_offset.z}, r {alignment_rotation}", Matrix.R(0f, 180f, 0f) * Matrix.T(0f, -2.6f, -20f), ALIGNMENT_TEXT_STYLE);
                    if (use_alignment_transform)
                        Text.Add("(alignment transform used)", Matrix.R(0f, 180f, 0f) * Matrix.T(0f, -3.2f, -20f), ALIGNMENT_TEXT_STYLE);

                    /*Pose awin_pose = new Pose(new Vec3(0.25f, 0f, -0.5f), Quat.FromAngles(0f, 180f, 0f));
                    UI.WindowBegin("Alignment", ref awin_pose, size, UIWin.Empty);
                    if (UI.Button(" Mark "))
                    {
                        alignment_solver.AddObservation(current_alignment_landmark,
                            Input.Head.position, Input.Head.orientation.Rotate(new Vec3(0f, 0f, -1f)));
                    }
                    UI.WindowEnd();*/

                    Hierarchy.Pop();
                }

                //                
                // FPS counter
                //

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

                DrawUIWindows();

            })) ;

            if (qrcode_watcher != null)
                qrcode_watcher.Stop();

            // XXX should be in finally clause
            url_requests_queue.Dispose();
            tile_requests_queue.Dispose();            
        }

        public void DrawUIWindows()
        {
            // Main window
            UI.WindowBegin($"Dutch SKies - {configuration_name}", ref main_window_pose, new Vec2(60, 0) * U.cm, UIWin.Normal);

            UI.Toggle("Flight units", ref show_flight_units);
            UI.SameLine();
            if (UI.Button("Clear tracks"))
                ClearTracks();
            UI.SameLine();
            // See https://github.com/maluoi/StereoKit/issues/248
            UI.Space(-0.02f);
            if (UI.Toggle("Scan QR code", ref scanning_for_qrcodes))
            {
                //Log.Info($"qr code button toggled, now {scanning_for_qrcodes}");
                SetQRCodeScan();
            }
            UI.SameLine();
            UI.Space(-0.03f);
            UI.Toggle("Alignment", ref alignment_mode);
            UI.SameLine();
            UI.Toggle("Trim", ref show_trim_window);
            UI.SameLine();
            UI.Space(-0.03f);
            UI.Label($"{num_planes_on_map} planes shown ({num_planes_on_ground} on ground) • {plane_data.Count} planes in query area ({num_planes_late} late, {num_planes_missing} missing)");

            UI.HSeparator();
            UI.PushId("map");

            UI.Label("Map:");
            foreach (OSMMap map in maps_in_current_set.Values)
            {
                UI.SameLine();
                if (UI.Radio(map.id, current_map_name == map.id))
                {
                    // Switch map
                    SelectMap(map.id);
                }
            }

            UI.Toggle("Visible", ref map_visible);
            UI.SameLine();
            UI.Toggle("Planes", ref map_show_plane_model);
            UI.SameLine();
            UI.Toggle("VLines", ref map_show_vlines);
            UI.SameLine();
            UI.Toggle("Track lines", ref map_show_track_lines);
            UI.SameLine();
            UI.Toggle($"Observer '{observer.name}'", ref map_show_observer);

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
            UI.SameLine();
            UI.Toggle("Planes", ref sky_show_plane_models);
            UI.SameLine();
            UI.Toggle("VLines", ref sky_show_vlines);
            UI.SameLine();
            UI.Toggle("Trails", ref sky_show_trail_lines);
            UI.SameLine();
            UI.Toggle($"Landmarks ({landmarks.Count})", ref sky_show_landmarks);

            UI.PopId();

            UI.HSeparator();
            string time = DateTime.Now.ToString("HH:mm:ss");
            UI.Label("Debug:");
            UI.SameLine();
            UI.Toggle("Origin", ref show_origin);
            UI.SameLine();
            UI.Toggle("Log", ref show_log_window);
            UI.SameLine();
            UI.Toggle("Discord", ref discord_messages_enabled);
            UI.SameLine();
            UI.Label($"IP:{our_ip} • {time} • {fps:F1} FPS");
            UI.SameLine();
            UI.WindowEnd();

            // Trim window

            if (show_trim_window)
            {
                // Identity pose orientation has window front pointing to -Z, head identity pose also points to -Z
                Vec3 head_lookdir_xz = Matrix.R(head_orientation).Transform(new Vec3(0f, 0f, -1f));
                head_lookdir_xz.y = 0f;
                Vec3 trim_window_lookdir = -head_lookdir_xz;

                // Use atan2 for y=-z, x=x; gives CCW rotation from +x axis
                float angle = MathF.Atan2(-head_lookdir_xz.z, head_lookdir_xz.x) / MathF.PI * 180f;
                // Round to nearest multiple of N degrees
                angle = MathF.Round(angle / 45f) * 45f;
                // Zero rotation will be -Z (which is +Y and thus angle 90 for atan2)
                angle -= 90f;

                Vec3 window_pos = new Vec3(0f, head_pos.y - 0.2f, 0f) + Matrix.R(0f, angle, 0f).Transform(-0.6f * Vec3.UnitZ);

                Pose trim_window_pose = new Pose(
                    window_pos,
                    // Window needs to face the other way, hence angle+180
                    Quat.FromAngles(0f, angle + 180f, 0f) * Quat.FromAngles(30f, 0f, 0f)   // Tilt the window a bit
                );

                //UI.WindowBegin($"Trim; head lookdir = {head_lookdir_xz} -> angle = {angle}", ref trim_window_pose, new Vec2(60, 0) * U.cm, UIWin.Normal);                    
                UI.WindowBegin("Trim", ref trim_window_pose, new Vec2(50, 0) * U.cm, UIWin.Body);
                UI.Toggle($"Landmarks ({landmarks.Count})", ref sky_show_landmarks);
                UI.SameLine();
                UI.Space(-0.2f);
                if (UI.Button("Close window")) show_trim_window = false;
                UI.PushId("htrim");
                UI.Text("H Trim (°)", TextAlign.XCenter);
                if (UI.Button("◀45")) sky_d_trim -= 450;
                UI.SameLine();
                if (UI.Button("◀5")) sky_d_trim -= 50;
                UI.SameLine();
                if (UI.Button("◀1")) sky_d_trim -= 10;
                UI.SameLine();
                if (UI.Button("◀⅒")) sky_d_trim -= 1;
                UI.SameLine();
                if (UI.Button("0")) sky_d_trim = 0;
                UI.SameLine();
                if (UI.Button("⅒▶")) sky_d_trim += 1;
                UI.SameLine();
                if (UI.Button("1▶")) sky_d_trim += 10;
                UI.SameLine();
                if (UI.Button("5▶")) sky_d_trim += 50;
                UI.SameLine();
                if (UI.Button("45▶")) sky_d_trim += 450;
                UI.PopId();

                UI.Space(0.01f);

                UI.PushId("vtrim");
                UI.Text("V Trim (cm)", TextAlign.XCenter);
                UI.SameLine();
                if (UI.Button("▼100")) sky_v_trim -= 100;
                UI.SameLine();
                if (UI.Button("▼50")) sky_v_trim -= 50;
                UI.SameLine();
                if (UI.Button("▼5")) sky_v_trim -= 5;
                UI.SameLine();
                if (UI.Button("▼1")) sky_v_trim -= 1;
                UI.SameLine();
                if (UI.Button("0")) sky_v_trim = 0;
                UI.SameLine();
                if (UI.Button("▲1")) sky_v_trim += 1;
                UI.SameLine();
                if (UI.Button("▲5")) sky_v_trim += 5;
                UI.SameLine();
                if (UI.Button("▲50")) sky_v_trim += 50;
                UI.SameLine();
                if (UI.Button("▲100")) sky_v_trim += 100;
                UI.PopId();
                UI.WindowEnd();
            }

            // Alignment window

            if (alignment_mode)
            {
                UI.WindowBegin("Alignment", ref alignment_window_pose, new Vec2(60, 0) * U.cm, UIWin.Normal);
                if (UI.Button("Solve"))
                    alignment_solver.Solve(out alignment_offset, out alignment_rotation);
                UI.SameLine();
                UI.Space(-0.02f);
                UI.Toggle("Use alignment", ref use_alignment_transform);
                UI.SameLine();
                UI.Space(-0.02f);
                if (UI.Button("Clear all"))
                    alignment_solver.ClearObservations();
                UI.Text($"tx {alignment_offset.x:F3}, tz {alignment_offset.z:F3}, r {alignment_rotation}", TextAlign.Center);
                int col = 0;
                string caption;
                foreach (string lm_name in sorted_landmark_names)
                {
                    if (col < 3)
                        UI.SameLine();
                    else
                        col = 0;

                    Landmark lm = landmarks[lm_name];
                    caption = $"[{alignment_solver.ObservationCount(lm.id)}] {lm.id}";

                    if (UI.Radio(caption, current_alignment_landmark == lm.id))
                        current_alignment_landmark = lm.id;

                    col++;
                }
                UI.WindowEnd();
            }

            // Log window

            if (show_log_window)
            {
                UI.WindowBegin("Log", ref log_window_pose, new Vec2(80, 0) * U.cm, UIWin.Normal);
                UI.Text(log_text);
                if (UI.Radio("Diagnostic", log_level == LogLevel.Diagnostic)) { log_level = Log.Filter = LogLevel.Diagnostic; }
                UI.SameLine();
                if (UI.Radio("Info", log_level == LogLevel.Info)) { log_level = Log.Filter = LogLevel.Info; }
                UI.SameLine();
                if (UI.Radio("Warning", log_level == LogLevel.Warning)) { log_level = Log.Filter = LogLevel.Warning; }
                UI.SameLine();
                if (UI.Radio("Error", log_level == LogLevel.Error)) { log_level = Log.Filter = LogLevel.Error; }
                UI.WindowEnd();
            }
        }

        public void PrepareBuiltinMaps()
        {
            MapSet map_set = new MapSet("<default>");
            OSMMap map;

            // Whole of the Netherlands
            map = new OSMMap(
                    "The Netherlands",
                    50.51342652633955f, 53.9560855309879f, 2.8125f, 8.0859375f, 10);
            map.texture = Tex.FromFile("Maps\\netherlands.png");
            map_set.Add("The Netherlands", map);

            // Schiphol Airport
            map = new OSMMap(
                "Schiphol Airport",
                51.89005393521691f, 52.69636107827448f, 4.04296875f, 5.361328125f, 12
            );
            map.texture = Tex.FromFile("Maps\\schiphol.png");
            map_set.Add("Schiphol Airport", map);

            // Eindhoven Airport
            map = new OSMMap(
                "Eindhoven Airport",
                51.28940590271678f, 51.6180165487737f, 5.09765625f, 5.712890625f, 12
            );
            map.texture = Tex.FromFile("Maps\\eindhoven.png");
            map_set.Add("Eindhoven Airport", map);

            map_sets["<default>"] = map_set;
        }

        public void ObserverChanged()
        {
            observer.update_map_position(current_map);
            foreach (PlaneData plane in plane_data.Values)
                plane.ObserverChange(observer);
            RecomputeLandmarkPositions();
        }

        public void ClearTracks()
        {
            foreach (var plane in plane_data.Values)
                plane.ClearTrack();
        }

        void ScheduleURLFetch(string url, string type, bool binary, string payload)
        {
            url_requests_queue.Add(new URLFetchRequest(url, type, binary, payload));
        }

        void ScheduleTileFetch(int min_i, int max_i, int min_j, int max_j, int zoom, string[] servers, string map_name)
        {
            TileFetchRequest request = new TileFetchRequest(min_i, max_i, min_j, max_j, zoom, servers, map_name);
            tile_requests_queue.Add(request);
        }
         void SchedulePostMessage(string message)
        {
            if (!discord_messages_enabled)
                return;
            PostMessageRequest request = new PostMessageRequest(message);
            message_send_queue.Add(request);
        }

        async void FetchURLThread()
        {
            URLFetchRequest request;
            string type, url, payload;
            bool binary;

            HttpClient http_client = new HttpClient();

            while (true)
            {
                //Log.Info($"(configuration fetch thread) waiting for url");
                request = url_requests_queue.Take();

                // Got updated extent
                url = request.Item1;
                type = request.Item2;
                binary = request.Item3;
                payload = request.Item4;

                Log.Info($"(URL fetch) Fetching URL {url} (type '{type}', binary {binary}, payload '{payload}')");

                try
                {
                    HttpResponseMessage response = await http_client.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                    {
                        Log.Err($"(URL fetch) HTTP error {response.StatusCode} while attempting to fetch {url}!");
                        continue;
                    }

                    if (binary)
                    {
                        byte[] data = await response.Content.ReadAsByteArrayAsync();
                        updates_queue.Enqueue(new Tuple<string, object, string>(type, data, payload));
                    }
                    else
                    {
                        string text = await response.Content.ReadAsStringAsync();
                        updates_queue.Enqueue(new Tuple<string, object, string>(type, text, payload));
                    }

                }
                catch (Exception e)
                {
                    Log.Err($"(URL fetch) Exception      : {e.Message}");
                    if (e.InnerException != null)
                        Log.Err($"(URL fetch) Inner exception: {e.InnerException.Message}");
                }
            }
        }

        async void PostMessagesThread()
        {
            PostMessageRequest request;
            string message;
            string webhook_url = "";
            string json_message;
            StringContent data;

            HttpClient http_client = new HttpClient();

            while (true)
            {
                //Log.Info($"(configuration fetch thread) waiting for url");
                request = message_send_queue.Take();
                message = request.Item1;

                if (webhook_url == "")
                {
                    Log.Info($"No discord webhook set, not sending message '{message}'");
                    continue;
                }

                Log.Info($"(Post message) Sending '{message}'");

                JsonObject dataobj = new JsonObject();
                dataobj["content"] = JsonValue.CreateStringValue(message);
                json_message = dataobj.ToString();

                data = new StringContent(json_message, Encoding.UTF8, "application/json");

                try
                {
                    HttpResponseMessage response = await http_client.PostAsync(webhook_url, data);
                    if (!response.IsSuccessStatusCode)
                    {
                        Log.Err($"(Post message) HTTP error {response.StatusCode} while attempting to post to {webhook_url}!");
                        continue;
                    }
                }
                catch (Exception e)
                {
                    Log.Err($"(Post message) Exception      : {e.Message}");
                    if (e.InnerException != null)
                        Log.Err($"(Post message) Inner exception: {e.InnerException.Message}");
                }

                // Avoid TooManyRequests error
                // XXX handle errors in a better way by reposting after a backoff
                Thread.Sleep(200);
            }
        }


        async void FetchPlaneUpdates(object obj)
        {
            ConcurrentQueue<Vec4> extent_input_queue = obj as ConcurrentQueue<Vec4>;

            Vec4 extent;
            HttpClient http_client = new HttpClient();

            // Wait for initial extent
            while (!extent_input_queue.TryDequeue(out extent))
                Thread.Sleep(100);

            Log.Info($"(Data fetch) Initial query extent: lat {extent.x:F6} to {extent.y:F6}; lon {extent.z:F6} to {extent.w:F6}");
            string URL = $"https://opensky-network.org/api/states/all?lamin={extent.x}&lamax={extent.y}&lomin={extent.z}&lomax={extent.w}";

            while (true)
            {
                if (extent_input_queue.TryDequeue(out extent))
                {
                    // Got updated extent
                    Log.Info($"(Data fetch) Got updated query extent: lat {extent.x:F6} to {extent.y:F6}; lon {extent.z:F6} to {extent.w:F6}");
                    URL = $"https://opensky-network.org/api/states/all?lamin={extent.x}&lamax={extent.y}&lomin={extent.z}&lomax={extent.w}";
                }

                try
                {
                    HttpResponseMessage response = await http_client.GetAsync(URL);
                    response.EnsureSuccessStatusCode();
                    string body = await response.Content.ReadAsStringAsync();
                    //Log.Info("(Data fetch): " + body);

                    JSONNode root_node = JSON.Parse(body);
                    updates_queue.Enqueue(new Tuple<string, object, string>("plane_data", root_node, ""));
                }
                catch (Exception e)
                {
                    Log.Err($"(Data fetch) Exception      : {e.Message}");
                    if (e.InnerException != null)
                        Log.Err($"(Data fetch) Inner exception: {e.InnerException.Message}");
                }

                Thread.Sleep(OPENSKY_QUERY_INTERVAL * 1000);
            }
        }

        public void SetQRCodeScan()
        {
            //Log.Info($"SetQRCodeScan, scanning_for_qr_codes = {scanning_for_qrcodes}");
            if (qrcode_watcher == null)
            {
                Log.Info("Cannot start QR code scanning, no permission given!");
                scanning_for_qrcodes = false;
                return;
            }

            if (scanning_for_qrcodes)
            {
                last_qrcode_id = Guid.Empty;
                qrcode_watcher_start = DateTime.Now;
                qrcode_watcher.Start();
                Log.Info("Started QR code watcher");
            }
            else
            {
                Log.Info("Stopping QR code watcher");
                qrcode_watcher.Stop();
            }
        }
        public void UpdateLandmarks(JSONNode nodes)
        {
            Landmark lm;

            landmarks.Clear();

            foreach (JSONNode n in nodes)
            {
                // XXX needs more checks
                string id = n["id"];
                float lat = n["lat"];
                float lon = n["lon"];
                float top_altitude = n["topalt"];
                float bottom_altitude = n["botalt"];

                lm = landmarks[id] = new Landmark(id, lat, lon, top_altitude, bottom_altitude);

                SchedulePostMessage($"landmark {n.ToString()}");
            }

            sorted_landmark_names = landmarks.Keys.ToList();

            RecomputeLandmarkPositions();
        }

        public void RecomputeLandmarkPositions()
        {
            float x, y;
            Matrix M;

            alignment_solver.ClearReferences();

            SchedulePostMessage($"RecomputeLandmarkPositions: observer lat {observer.lat:F6}, lon {observer.lon:F6}, alt {observer.floor_altitude:F6}");

            foreach (Landmark lm in landmarks.Values)
            {
                // Map (km)
                current_map.Project(out x, out y, lm.lon, lm.lat);
                lm.map_position = new Vec3(x, y, lm.top_altitude / 1000f);

                // Sky (m)
                M = Matrix.R(-lm.lat, 0f, 0f)
                    *
                    Matrix.R(0f, lm.lon - observer.lon, 0f)
                    *
                    Matrix.R(observer.lat, 0f, 0f)
                    *
                    // XXX Should also include have-above-floor distance, but the effect will be minimal
                    Matrix.T(0f, 0f, -(Projection.RADIUS_METERS + observer.floor_altitude));

                Vec3 p = new Vec3(0f, 0f, Projection.RADIUS_METERS + lm.top_altitude);

                lm.sky_position = M.Transform(p);

                Log.Info($"Landmark '{lm.id}' sky pos reference (Z-up) = {lm.sky_position}");
                SchedulePostMessage($"(skypos reference, Z-up) landmark {lm.id} lat {lm.lat}, lon {lm.lon} -> {lm.sky_position.x:F6}, {lm.sky_position.y:F6}, {lm.sky_position.z:F6}");

                // Note: Z-up to Y-up
                // XXX Should really alter the above transform to produce Y-up positions directly
                alignment_solver.SetReference(lm.id, ROT_90_X.Transform(lm.sky_position));
            }
        }

        public void SelectMapSet(string id)
        {
            Log.Info($"Selecting map-set '{id}'");

            // XXX check id exists
            current_map_set = map_sets[id];
            current_map_set_name = id;

            maps_in_current_set = current_map_set.maps;

            SelectMap(current_map_set.default_map);
            // XXX fold into SelectMap?
            observer.update_map_position(current_map);            
        }

        public void SelectMap(string id)
        {
            Log.Info($"Selecting map '{id}'");

            current_map_name = id;
            current_map = maps_in_current_set[id];

            // Compute MR size for map
            realworld_map_height = REALWORLD_MAP_WIDTH * current_map.height / current_map.width;
            Log.Info($"Map physical size {REALWORLD_MAP_WIDTH:F2} x {realworld_map_height:F2} m");
            map_quad = Mesh.GeneratePlane(new Vec2(REALWORLD_MAP_WIDTH, realworld_map_height), -Vec3.Forward, Vec3.Up);
            map_scale_km_to_scene = REALWORLD_MAP_WIDTH / current_map.width;

            map_texture = current_map.texture;
            if (map_texture != null)
            {
                // Texture will be set later, after retrieval
                map_material[MatParamName.DiffuseTex] = map_texture;
            }
            else
                map_material[MatParamName.DiffuseTex] = Tex.White;

            // Need to recompute extrapolated plane map positions 
            foreach (PlaneData plane in plane_data.Values)
            {
                plane.MapChange(current_map);
                plane.Update(draw_time);
            }

            observer.update_map_position(current_map);
        }

        public void UpdateConfigurationLists()
        {
            ConfigurationStore.List(ConfigurationStore.ConfigType.MAP_SET, ref available_map_sets);
            ConfigurationStore.List(ConfigurationStore.ConfigType.LANDMARK_SET, ref available_landmark_sets);
            ConfigurationStore.List(ConfigurationStore.ConfigType.OBSERVER, ref available_observers);
        }

        public void ProcessConfigurationData(string config_string, string config_url)
        {
            // XXX handle error
            JSONNode config_root = JSON.Parse(config_string);

            configuration_name = config_url;
            if (configuration_name.Length > 63)
                configuration_name = configuration_name.Substring(0, 30) + "..." + configuration_name.Substring(configuration_name.Length - 30);

            bool current_map_updated = false;
            bool observer_updated = false;
            bool explicit_query_extent = false;

            /*if (config_root.HasKey("query"))
            {
                JSONNode query = config_root["query"];

                Vec4 extent = new Vec4(query["lat_range"][0], query["lat_range"][1], query["lon_range"][0], query["lon_range"][1]);
                Log.Info($"Setting plane data query extent to {extent}");
                query_extent_update_queue.Enqueue(extent);
                explicit_query_extent = true;
            }*/

            if (config_root.HasKey("map_sets"))
            {
                JSONArray jmap_sets = config_root["map_sets"].AsArray;
                JSONArray jmaps;
                MapSet map_set;
                string map_set_id;
                OSMMap map;

                bool first = true;
                float min_lat, max_lat, min_lon, max_lon;
                int set_idx = 0;
                
                foreach (JSONNode jmap_set in jmap_sets)
                {
                    if (!jmap_set.HasKey("id"))
                    {
                        // XXX list index in sets in message
                        Log.Err($"Map-set (index {set_idx}) does not have 'id' field, ignoring!");
                        set_idx++;
                        continue;
                    }

                    map_set_id = jmap_set["id"];

                    // XXX avoid '<default>'?
                    if (map_set_id == "")
                    {
                        Log.Err($"Map-set (index {set_idx}) should not have empty 'id' field, ignoring!");
                        set_idx++;
                        continue;
                    }

                    if (!jmap_set.HasKey("items") || jmap_set["items"].Count == 0)
                    {
                        Log.Err($"Map-set '{map_set_id}' does not contain any maps, ignoring!");
                        set_idx++;
                        continue;
                    }

                    map_set = new MapSet(map_set_id);
                    jmaps = jmap_set["items"].AsArray;

                    // Optional list of tile servers
                    Dictionary<string, TileServerConfiguration> tile_server_configurations = new Dictionary<string, TileServerConfiguration>();

                    if (jmap_set.HasKey("tile_servers"))
                    {
                        foreach (JSONNode jtileserver in jmap_set["tile_servers"])
                        {
                            TileServerConfiguration ts = TileServerConfiguration.FromJSON(jtileserver);
                            tile_server_configurations[ts.id] = ts;
                        }
                    }

                    // Maps in map set
                    foreach (JSONNode jmap in jmaps)
                    {
                        string map_id = jmap["id"];
                        Log.Info($"Have new map '{map_id}'");

                        if (!jmap.HasKey("lat_range"))
                        {
                            Log.Err("Map specification is missing 'lat_range'!");
                            continue;
                        }
                        if (!jmap.HasKey("lon_range"))
                        {
                            Log.Err("Map specification is missing 'lon_range'!");
                            continue;
                        }

                        JSONNode imgsource = jmap["image_source"];

                        if (imgsource["type"] == "url")
                        {
                            string imgurl = imgsource["url"];
                            if (!(imgurl.StartsWith("http://") || imgurl.StartsWith("https://")))
                            {
                                // Assume path relative to config url
                                Uri uri = new Uri(config_url);
                                string path = Path.GetDirectoryName(uri.AbsolutePath).Replace("\\", "/");
                                if (imgurl[0] != '/')
                                    path += '/';
                                imgurl = $"{uri.GetLeftPart(UriPartial.Authority)}{path}{imgurl}";
                                Log.Info($"Assuming image source relative to config URL: {imgurl}");
                            }

                            // XXX needs to include (map_set, name) to make sure fetched result gets put in the right place
                            Log.Info($"Scheduling fetching of map image {imgurl} for map '{map_id}'");
                            ScheduleURLFetch(imgurl, "map_image", true, map_id);

                            min_lat = jmap["lat_range"][0];
                            max_lat = jmap["lat_range"][1];
                            min_lon = jmap["lon_range"][0];
                            max_lon = jmap["lon_range"][1];

                            map = new OSMMap(map_id, min_lat, max_lat, min_lon, max_lon);
                            map_set.Add(map_id, map);
                        }
                        else if (imgsource["type"] == "tile_server")
                        {
                            if (!jmap.HasKey("zoom"))
                            {
                                Log.Err("Map specification '{map_id}' uses tile-server, but is missing 'zoom' value!");
                                continue;
                            }

                            Vec4 query_extent = new Vec4(jmap["lat_range"][0], jmap["lat_range"][1], jmap["lon_range"][0], jmap["lon_range"][1]);

                            int min_i, max_i, min_j, max_j;

                            // The actual map extent will be based on a set of unclipped tiles, so we need it here to set up the
                            // map correctly before the image fetch is complete.
                            OSMTiles.ComputeActualMapExtent(
                                out min_lat, out max_lat, out min_lon, out max_lon,
                                out min_i, out max_i, out min_j, out max_j,
                                    query_extent, jmap["zoom"]);

                            map = new OSMMap(map_id, min_lat, max_lat, min_lon, max_lon);
                            map_set.Add(map_id, map);

                            // XXX needs to include (map_set, name)
                            // XXX should not schedule from here, but when selecting this map set
                            Log.Info($"Scheduling fetching of map tiles for map '{map_id}' ({min_i}-{max_i}, {min_j}-{max_j}, {jmap["zoom"]})");
                            ScheduleTileFetch(min_i, max_i, min_j, max_j, jmap["zoom"], 
                                tile_server_configurations[imgsource["id"]].urls, map_id);
                        }
                        else
                            Log.Warn($"Unknown map image-source type '{imgsource["type"]}'!");
                    }

                    // XXX check if valid map_set config, before storing
                    ConfigurationStore.Store(ConfigurationStore.ConfigType.MAP_SET, map_set_id, jmap_set);

                    map_sets[map_set_id] = map_set;

                    if (first)
                    {
                        // Switch to this map set
                        SelectMapSet(map_set_id);
                        first = false;
                        current_map_updated = true;
                    }
                }
            }

            if (config_root.HasKey("observer") && config_root["observer"].HasKey("id"))  // Guard against observer: {}
            {
                JSONNode jobs = config_root["observer"];
                observer.name = jobs["id"];
                observer.lat = jobs["lat"];
                observer.lon = jobs["lon"];
                observer.floor_altitude = jobs["alt"];
                observer_updated = true;
            }
            else if (current_map_updated)
            {
                // Need some setting for observer, so pick map center at 0m altitude
                Log.Info("No observer set in configuration, so picking map center");
                observer.name = "<map center>";
                observer.lat = current_map.center_lat;
                observer.lon = current_map.center_lon;
                observer.floor_altitude = 0f;
                observer_updated = true;
            }

            if (config_root.HasKey("landmarks") && config_root["landmarks"].Count > 0)
            {
                UpdateLandmarks(config_root["landmarks"]);
                if (sorted_landmark_names.Count > 0)
                    current_alignment_landmark = sorted_landmark_names[0];
            }

            if (observer_updated)
                ObserverChanged();

            if (current_map_updated && !explicit_query_extent)
            {
                // Maps changed, but no query extent given. Set extent to union of all maps,
                // to cover the whole area
                float min_lat = 1e6f;
                float min_lon = 1e6f;
                float max_lat = -1e6f;
                float max_lon = -1e6f;

                foreach (OSMMap map in maps_in_current_set.Values)
                {
                    min_lat = MathF.Min(min_lat, map.min_lat);
                    max_lat = MathF.Max(max_lat, map.max_lat);
                    min_lon = MathF.Min(min_lon, map.min_lon);
                    max_lon = MathF.Max(max_lon, map.max_lon);
                }

                Vec4 extent = new Vec4(min_lat, max_lat, min_lon, max_lon);
                Log.Info($"Setting plane data query extent to {extent}, based on union of map extents (no explicit extent given)");
                query_extent_update_queue.Enqueue(extent);
            }
        }
    }
}
