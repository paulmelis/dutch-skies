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
    using URLFetchRequest = Tuple<string, string, bool, object>;
    using TileFetchRequest = Tuple<int, int, int, int, int, string[], OSMMap>;
    using PostMessageRequest = Tuple<string>;
    using Update = Tuple<string, object, object>;

    class Application
    {
        enum DetailLevel { NONE, CALLSIGN, FULL };
        const float PLANE_SIZE_METERS = 0.015f;

        // Some often used transforms
        static Matrix ROT_90_X = Matrix.R(-90f, 0f, 0f);
        static Matrix ROT_MIN90_X = Matrix.R(-90f, 0f, 0f);
        static Matrix ROT_180_Y = Matrix.R(0f, 180f, 0f);

        // Log
        List<string> log_lines;
        string log_text;
        LogLevel log_level = LogLevel.Info;

        string our_ip;
        float fps;
        bool show_origin;
        string discord_webhook_url;

        // Configurations
        List<string> stored_map_sets;
        List<string> stored_landmark_sets;
        List<string> stored_observer_sets;

        Dictionary<string, MapSet> map_sets;
        MapSet current_map_set;
        string current_map_set_name;

        Dictionary<string, LandmarkSet> landmark_sets;
        LandmarkSet current_landmark_set;
        string current_landmark_set_name;

        Dictionary<string, ObserverSet> observer_sets;
        ObserverSet current_observer_set;
        string current_observer_set_name;
        Observer map_center_observer;

        // XXX need to use a different value to show in the UI
        string current_configuration_name;

        // UI
        Pose main_window_pose, log_window_pose, configuration_window_pose;
        bool show_log_window;
        bool show_trim_window;
        bool show_configuration_window;

        // Plane data
        ConcurrentQueue<Vec4> query_extent_update_queue;
        Dictionary<string, PlaneData> plane_data;

        int num_planes_on_map;
        int num_planes_late, num_planes_missing;
        int num_planes_on_ground;

        DetailLevel map_detail_level;
        bool map_visible;
        bool map_show_plane_model, sky_show_plane_models;
        bool map_show_vlines, sky_show_vlines;
        bool map_show_track_lines, sky_show_trail_lines;
        bool map_show_observer;
        bool sky_show_landmarks;
        bool use_flight_units;

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
        Dictionary<string, Landmark> landmarks_in_current_set;
        List<string> sorted_landmark_names;

        Dictionary<string, Observer> observers_in_current_set;
        string current_observer_name;
        Observer current_observer;

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
        ConcurrentQueue<Update> updates_queue;
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
            log_lines = new List<string>();
            log_level = LogLevel.Info;
            Log.Subscribe(OnLog);

            discord_messages_enabled = false;
            if (ConfigurationStore.LoadOption("discord_webhook", out discord_webhook_url))
                Log.Info($"Have Discord webhook {discord_webhook_url}");

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

            //
            // Start some background threads
            // XXX can probably take more advantage of async/await, but let's use threads for now
            //

            // Queue for receiving updates from threads
            updates_queue = new ConcurrentQueue<Update>();

            // Launch data update thread            
            // Queue for sending updated query range to thread
            query_extent_update_queue = new ConcurrentQueue<Vec4>();
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

            // Configurations

            current_configuration_name = "<builtin>";

            current_map_set_name = "";
            current_landmark_set_name = "";
            current_observer_set_name = "";

            stored_map_sets = new List<string>();
            stored_landmark_sets = new List<string>();
            stored_observer_sets = new List<string>();

            UpdateConfigurationLists();

            Log.Info("--- Available stored configurations ---");

            foreach (string s in stored_map_sets)
                Log.Info($"[MAP_SET] '{s}'");

            foreach (string s in stored_map_sets)
                Log.Info($"[LANDMARK_SET] '{s}'");

            foreach (string s in stored_observer_sets)
                Log.Info($"[OBSERVER_SET] '{s}'");

            Log.Info("--- Available stored configurations ---");

            // Planes

            plane_data = new Dictionary<string, PlaneData>();

            // Observer

            Mesh observer_cylinder_marker = Mesh.GenerateCylinder(0.002f, 0.02f, Vec3.UnitY, 8);
            Mesh observer_sphere_marker = Mesh.GenerateSphere(0.006f, 8);
            Material observer_marker_material = Default.Material.Copy();
            observer_marker_material[MatParamName.ColorTint] = new Color(1f, 0.5f, 0f);

            current_observer = null;
            observers_in_current_set = null;

            landmarks_in_current_set = new Dictionary<string, Landmark>();
            sorted_landmark_names = landmarks_in_current_set.Keys.ToList();
            sorted_landmark_names.Sort();

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

            landmark_sets = new Dictionary<string, LandmarkSet>();
            observer_sets = new Dictionary<string, ObserverSet>();

            ObserverSet map_center_observer_set = new ObserverSet("<default>");
            // Observer position will be updated when switching maps
            map_center_observer = new Observer("<map center>", 0f, 0f, 0f);
            current_observer = map_center_observer;
            map_center_observer_set.Add("<map center>", current_observer);
            observer_sets.Add("<default>", map_center_observer_set);
            
            SelectMapSet("<default>");
            SelectObserverSet("<default>");     // Assumes current map is set

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
            if (plane_ground_marker == null)
            {
                Log.Err("Could not generate plane ground marker, something is really screwed! I'm giving up...");
                SK.Quit();
            }
            Material plane_marker_material = Default.Material.Copy();
            plane_marker_material[MatParamName.ColorTint] = new Color(0f, 0f, 1f);

            // Floor (for non-seethrough devices)

            //Matrix floorTransform = Matrix.TS(0, -1.5f, 0, new Vec3(30, 0.1f, 30));
            //Material floorMaterial = new Material(Shader.FromFile("floor.hlsl"));
            //floorMaterial.Transparency = Transparency.Blend;

            // Initial config (for debugging)

            // XXX
            string initial_config = "";
            //initial_config = "http://192.168.178.32:8000/config-netherlands-and-schiphol-image.json";
            //initial_config = "http://192.168.178.32:8000/config-netherlands-and-schiphol-osmtiles.json";
            //initial_config = "http://192.168.178.32:8000/config-newyork-image.json";
            //initial_config = "http://192.168.178.32:8000/config-alps-image.json";            
            //initial_config = "http://192.168.178.32:8000/sanfrancisco-osmtiles.json";            
            //initial_config = "http://192.168.178.32:8000/observer-and-landmarks-home-backroom.json";

            //initial_config = "http://192.168.178.32:8000/config-alps-surfdriveimage.json2";
            //initial_config = "http://192.168.178.32:8000/sanfrancisco-image-surfdrive.json2";                                    
            //initial_config = "http://192.168.178.32:8000/config-netherlands-park-osm.json2";

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
                    updates_queue.Enqueue(new Update("qrcode", qr.Code, ""));
                };
                qrcode_watcher.Updated += (o, qr) =>
                {
                    //Log.Info($"(QR code Updated handler) QR code: {qr.Code.Id} '{qr.Code.Data}'");
                    updates_queue.Enqueue(new Update("qrcode", qr.Code, ""));
                };
                //watcher.Removed += (o, qr) => poses.Remove(qr.Code.Id);
            }
            else
                Log.Info("Cannot perform QR code scanning, no permission given");

            scanning_for_qrcodes = false;

            // Alignment

            alignment_mode = false;            
            alignment_offset = new Vec3();
            alignment_rotation = 0f;
            use_alignment_transform = false;
            current_alignment_landmark = "";

            // Main settings

            main_window_pose = new Pose(0.5f, -0.2f, -0.5f, Quat.LookDir(-1, 0, 1));
            log_window_pose = new Pose(0.9f, -0.2f, 0f, Quat.LookDir(-1, 0, 1));
            configuration_window_pose = new Pose(-0.5f, -0.2f, 0f, Quat.LookDir(1, 0, 1));
            alignment_window_pose = new Pose(-0.9f, -0.2f, 0f, Quat.LookDir(1, 0, 1));

            map_detail_level = DetailLevel.FULL;
            show_log_window = true;
            show_trim_window = false;
            show_configuration_window = true;

            map_visible = true;
            map_show_plane_model = true;
            sky_show_plane_models = true;
            map_show_vlines = true;
            sky_show_vlines = false;
            map_show_track_lines = true;
            sky_show_trail_lines = true;
            map_show_observer = false;
            sky_show_landmarks = true;
            use_flight_units = false;
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
            Color LANDMARK_VLINE_COLOR_HIGHLIGHTED = new Color(0f, 1f, 0f);
            Color ALIGNMENT_TEXT_COLOR = new Color(0f, 1f, 0f);

            Color32 ALIGNMENT_LINE_COLOR = new Color32(0, 255, 0, 255);
            const float ALIGNMENT_LINE_THICKNESS = 0.1f;

            const float SKY_SCALING_THRESHOLD = 3f;
            Matrix SKY_FAR_MODEL_SCALE = Matrix.S(30f);
            Matrix SKY_CLOSE_MODEL_SCALE = Matrix.S(60f);

            // XXX style uses gamma-space color, leading to slight difference with vline color
            TextStyle MAP_TEXT_STYLE = Text.MakeStyle(Default.Font, 0.7f * U.cm, MAP_BASE_COLOR);
            TextStyle SKY_TEXT_STYLE = Text.MakeStyle(Default.Font, 15f * U.m, SKY_BASE_COLOR);
            TextStyle LANDMARK_TEXT_STYLE = Text.MakeStyle(Default.Font, 2f * U.m, LANDMARK_VLINE_COLOR);
            TextStyle LANDMARK_TEXT_STYLE_HIGHLIGHTED = Text.MakeStyle(Default.Font, 2f * U.m, LANDMARK_VLINE_COLOR_HIGHLIGHTED);
            TextStyle MAP_DIMENSION_TEXT_STYLE = Text.MakeStyle(Default.Font, 0.01f * U.m, Color.White);
            TextStyle ALIGNMENT_TEXT_STYLE = Text.MakeStyle(Default.Font, 0.35f * U.m, ALIGNMENT_TEXT_COLOR);

            Update update;
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
                        OSMMap map_to_update = update.Item3 as OSMMap;
                        Log.Info($"Got updated map image ({map_image_file.Length} bytes), for map '{map_to_update.id}'");
                        Tex texture = Tex.FromMemory(map_image_file);
                        if (texture != null)
                        {
                            map_to_update.texture = texture;
                            if (current_map_name == map_to_update.id)
                                map_material[MatParamName.DiffuseTex] = maps_in_current_set[map_to_update.id].texture;
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

                            plane_data[id].ProcessDataUpdate(update_time, plane, current_map, current_observer);
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
                        {
                            Log.Info("Parsing QR code that doesn't look like URL as JSON");
                            try
                            {
                                JSON.Parse(data);
                                ProcessConfigurationData(data, "");
                            }
                            catch (Exception e)
                            {
                                Log.Warn($"Discarding QR code data, neither a URL or parseable JSON");
                            }
                        }
                    }
                    else if (update_type == "config_data")
                    {
                        ProcessConfigurationData(update.Item2 as string, update.Item3 as string);
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

                    if (xbox_controller.Pressed(XboxController.A))
                    {
                        if (!use_alignment_transform)
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
                        else
                            Log.Warn("Not adding observation, as alignment transform is active");
                    }
                    else if (xbox_controller.Pressed(XboxController.X))
                    {
                        if (!use_alignment_transform)
                        {
                            SchedulePostMessage($"Remove observations, lm {current_alignment_landmark}, ");
                            alignment_solver.RemoveObservations(current_alignment_landmark);
                        }
                        else
                            Log.Warn("Not removing observations, as alignment transform is active");
                    }
                    else if (xbox_controller.Pressed(XboxController.B))
                    {
                        // The found solution transform the observations onto the world coordinate system
                        UpdateAlignmentSolution();
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

                    if (map_detail_level == DetailLevel.CALLSIGN)
                    {
                        Text.Add(
                            $"{callsign}",
                            Matrix.TR(pos, textquat),
                            MAP_TEXT_STYLE,
                            pos_align,
                            TextAlign.XLeft | TextAlign.YTop,
                            -0.01f, 0.00f);
                    }
                    else if (map_detail_level == DetailLevel.FULL)
                    {
                        float vrate = plane.last_vertical_rate;
                        string vstring = " ";
                        string astring = "";
                        string sstring = "";

                        if (use_flight_units)
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

                if (map_show_observer && current_observer.on_map)
                {
                    Vec3 observer_pos = ROT_MIN90_X.Transform(current_observer.map_position) * map_scale_km_to_scene;
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

                    if (use_flight_units)
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
                    Color color;
                    TextStyle style;

                    foreach (KeyValuePair<string, Landmark> item in landmarks_in_current_set)
                    {
                        Landmark landmark = item.Value;
                        Vec3 pos = ROT_MIN90_X.Transform(landmark.sky_position);

                        color = LANDMARK_VLINE_COLOR;
                        style = LANDMARK_TEXT_STYLE;
                        if (alignment_mode && item.Key == current_alignment_landmark)
                        {
                            color = LANDMARK_VLINE_COLOR_HIGHLIGHTED;
                            style = LANDMARK_TEXT_STYLE_HIGHLIGHTED;
                        }

                        Lines.Add(pos, new Vec3(pos.x, pos.y - landmark.height, pos.z), color, 2f);

                        Quat textquat = Quat.LookAt(pos, head_pos, Vec3.UnitY);
                        Text.Add(
                            $"{item.Key}",
                            Matrix.R(textquat) * Matrix.T(pos),
                            style,
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

                    /*
                    Pose awin_pose = new Pose(new Vec3(0.25f, 0f, -0.5f), Quat.FromAngles(0f, 180f, 0f));
                    UI.WindowBegin("Alignment", ref awin_pose, size, UIWin.Empty);
                    if (UI.Button(" Mark "))
                    {
                        alignment_solver.AddObservation(current_alignment_landmark,
                            Input.Head.position, Input.Head.orientation.Rotate(new Vec3(0f, 0f, -1f)));
                    }
                    UI.WindowEnd();
                    */

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

        public float UpdateAlignmentSolution()
        {
            // Clear trim, so the solution shown isn't altered by the trim
            sky_d_trim = 0;
            sky_v_trim = 0;

            float res = alignment_solver.Solve(out alignment_offset, out alignment_rotation);

            SchedulePostMessage($"Solve -> ({alignment_offset.x:F6}, {alignment_offset.y:F6}, {alignment_offset.z:F6}), {alignment_rotation} (energy {res:F6})");

            return res;
        }

        public void DrawUIWindows()
        {
            int col;
            string caption;
            Vec2 size;

            UI.EnableFarInteract = false;

            // Main window
            UI.WindowBegin($"Dutch SKies - {current_configuration_name}", ref main_window_pose, new Vec2(60, 0) * U.cm, UIWin.Normal);

            UI.Toggle("Flight units", ref use_flight_units);
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
            UI.Toggle("Trim", ref show_trim_window);
            UI.SameLine();
            UI.Toggle("Alignment", ref alignment_mode);
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
            UI.Toggle("Observer", ref map_show_observer);

            UI.Label("Plane details");
            UI.SameLine();
            if (UI.Radio("None", map_detail_level == DetailLevel.NONE)) map_detail_level = DetailLevel.NONE;
            UI.SameLine();
            if (UI.Radio("Callsign", map_detail_level == DetailLevel.CALLSIGN)) map_detail_level = DetailLevel.CALLSIGN;
            UI.SameLine();
            if (UI.Radio("Full", map_detail_level == DetailLevel.FULL)) map_detail_level = DetailLevel.FULL;

            UI.PopId();

            UI.HSeparator();
            UI.PushId("sky");

            UI.Label("Sky");
            UI.SameLine();
            UI.Toggle("Planes", ref sky_show_plane_models);
            UI.SameLine();
            UI.Toggle("VLines", ref sky_show_vlines);
            UI.SameLine();
            UI.Toggle("Trails", ref sky_show_trail_lines);
            UI.SameLine();
            UI.Toggle($"Landmarks ({landmarks_in_current_set.Count})", ref sky_show_landmarks);

            UI.Label("Observer:");
            foreach (Observer ob in observers_in_current_set.Values)
            {
                UI.SameLine();
                if (UI.Radio(ob.id, current_observer_name == ob.id))
                {
                    // Switch observer
                    SelectObserver(ob.id);
                }
            }

            UI.PopId();

            UI.HSeparator();
            string time = DateTime.Now.ToString("HH:mm:ss");
            UI.Label("Debug:");
            UI.SameLine();
            UI.Toggle("Origin", ref show_origin);
            UI.SameLine();
            UI.Toggle("Log", ref show_log_window);
            if (discord_webhook_url != "")
            {
                UI.SameLine();
                UI.Toggle("Discord", ref discord_messages_enabled);
            }
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
                UI.Toggle($"Landmarks ({landmarks_in_current_set.Count})", ref sky_show_landmarks);
                UI.SameLine();
                UI.Space(-0.2f);
                if (UI.Button("Close window")) show_trim_window = false;
                UI.Text($"H {sky_d_trim*0.1f:F1}°, V {sky_v_trim} cm");
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
                    UpdateAlignmentSolution();
                UI.SameLine();
                UI.Space(-0.02f);
                UI.Toggle("Use alignment", ref use_alignment_transform);
                UI.SameLine();
                UI.Space(-0.02f);
                if (UI.Button("Clear all"))
                    alignment_solver.ClearObservations();
                UI.Text($"tx {alignment_offset.x:F3}, tz {alignment_offset.z:F3}, r {alignment_rotation}", TextAlign.Center);

                col = 0;
                size = new Vec2(8 * U.cm, 0);
                foreach (string lm_name in sorted_landmark_names)
                {
                    if (col < 4) UI.SameLine(); else col = 0;

                    Landmark lm = landmarks_in_current_set[lm_name];
                    caption = $"[{alignment_solver.ObservationCount(lm.id)}] {lm.id}";

                    if (UI.Radio(caption, current_alignment_landmark == lm.id, size))
                        current_alignment_landmark = lm.id;

                    col++;
                }
                UI.WindowEnd();
            }

            if (show_configuration_window)
            {
                size = new Vec2(8 * U.cm, 0);

                UI.WindowBegin("Configurations", ref configuration_window_pose, new Vec2(50, 0) * U.cm, UIWin.Normal);

                UI.PushId("map-sets");
                UI.Label("Map sets");                
                col = 0;                
                foreach (string name in stored_map_sets)
                {
                    if (col < 4) UI.SameLine(); else col = 0;

                    if (UI.Radio(name, current_map_set_name == name, size))
                        SelectMapSet(name);

                    col++;
                }
                UI.PopId();

                UI.PushId("observer-sets");
                UI.Label("Observer sets");                
                col = 0;
                foreach (string name in stored_observer_sets)
                {
                    if (col < 4) UI.SameLine(); else col = 0;

                    if (UI.Radio(name, current_observer_set_name == name, size))
                        SelectObserverSet(name);                        

                    col++;
                }
                UI.PopId();

                UI.PushId("landmark-sets");
                UI.Label("Landmark sets");
                col = 0;
                foreach (string name in stored_landmark_sets)
                {
                    if (col < 4) UI.SameLine(); else col = 0;

                    if (UI.Radio(name, current_landmark_set_name == name, size))
                        SelectLandmarkSet(name);

                    col++;
                }
                UI.PopId();

                UI.Space(0.03f);

                UI.Label("*DELETE*");
                UI.SameLine();
                if (UI.Button("Map sets"))
                {
                    ConfigurationStore.DeleteAllOfType(ConfigurationStore.ConfigType.MAP_SET);
                    UpdateConfigurationLists();
                    SelectMapSet("<default>");
                }
                UI.SameLine();
                if (UI.Button("Observer sets"))
                {
                    ConfigurationStore.DeleteAllOfType(ConfigurationStore.ConfigType.OBSERVER_SET);
                    UpdateConfigurationLists();
                    SelectObserverSet("<default>");
                }
                UI.SameLine();
                if (UI.Button("Landmark sets"))
                {
                    ConfigurationStore.DeleteAllOfType(ConfigurationStore.ConfigType.LANDMARK_SET);
                    UpdateConfigurationLists();
                    // XXX clear landmark set
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

            UI.EnableFarInteract = true;
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

            map_set.query_extent = new Vec4(50.51342652633955f, 53.9560855309879f, 2.8125f, 8.0859375f);
            map_sets["<default>"] = map_set;            
        }

        public void ObserverChanged()
        {
            current_observer.update_map_position(current_map);
            foreach (PlaneData plane in plane_data.Values)
                plane.ObserverChange(current_observer);
            RecomputeLandmarkPositions();
        }

        public void ClearTracks()
        {
            foreach (var plane in plane_data.Values)
                plane.ClearTrack();
        }

        public void ClearAllPlaneData()
        {
            plane_data.Clear();            
        }

        void ScheduleURLFetch(string url, string type, bool binary, object payload)
        {
            url_requests_queue.Add(new URLFetchRequest(url, type, binary, payload));
        }

        void ScheduleTileFetch(int min_i, int max_i, int min_j, int max_j, int zoom, string[] servers, OSMMap map)
        {
            TileFetchRequest request = new TileFetchRequest(min_i, max_i, min_j, max_j, zoom, servers, map);
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
            string type, url;
            object payload;
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

                Log.Info($"(URL fetch) Fetching URL {url} (type '{type}', binary {binary}, payload {payload})");

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
                        updates_queue.Enqueue(new Update(type, data, payload));
                    }
                    else
                    {
                        string text = await response.Content.ReadAsStringAsync();
                        updates_queue.Enqueue(new Update(type, text, payload));
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
            string json_message;
            StringContent data;

            HttpClient http_client = new HttpClient();

            while (true)
            {
                //Log.Info($"(configuration fetch thread) waiting for url");
                request = message_send_queue.Take();
                message = request.Item1;

                if (discord_webhook_url == "")
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
                    HttpResponseMessage response = await http_client.PostAsync(discord_webhook_url, data);
                    if (!response.IsSuccessStatusCode)
                    {
                        Log.Err($"(Post message) HTTP error {response.StatusCode} while attempting to post to {discord_webhook_url}!");
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

            string URL = "";
            Vec4 extent;

            HttpClient http_client = new HttpClient();

            while (true)
            {
                if (extent_input_queue.TryDequeue(out extent))
                {
                    // Got updated extent
                    Log.Info($"(Data fetch) Got updated query extent: lat {extent.x:F6} to {extent.y:F6}; lon {extent.z:F6} to {extent.w:F6}");
                    URL = $"https://opensky-network.org/api/states/all?lamin={extent.x}&lamax={extent.y}&lomin={extent.z}&lomax={extent.w}";
                }

                if (URL != "")
                {
                    try
                    {
                        HttpResponseMessage response = await http_client.GetAsync(URL);
                        response.EnsureSuccessStatusCode();
                        string body = await response.Content.ReadAsStringAsync();
                        //Log.Info("(Data fetch): " + body);

                        JSONNode root_node = JSON.Parse(body);
                        updates_queue.Enqueue(new Update("plane_data", root_node, ""));
                    }
                    catch (Exception e)
                    {
                        Log.Err($"(Data fetch) Exception      : {e.Message}");
                        if (e.InnerException != null)
                            Log.Err($"(Data fetch) Inner exception: {e.InnerException.Message}");
                    }

                    Thread.Sleep(OPENSKY_QUERY_INTERVAL * 1000);
                }
                else
                    Thread.Sleep(100);
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

        // Map sets

        public MapSet ParseMapSet(JSONNode jmap_set, string config_url="")
        {
            JSONArray jmaps;
            MapSet map_set;
            string map_set_id;
            OSMMap map;
            float min_lat, min_lon, max_lat, max_lon;
            float overall_min_lat = 1e6f, overall_min_lon = 1e6f;
            float overall_max_lat = -1e6f, overall_max_lon = -1e6f;

            if (!jmap_set.HasKey("id"))
            {
                // XXX list index in sets in message
                Log.Warn($"Map-set does not have 'id' field!");
                return null;
            }

            map_set_id = jmap_set["id"];

            // XXX avoid '<default>'?
            if (map_set_id == "")
            {
                Log.Warn($"Map-set should not have empty 'id' field!");
                return null;
            }

            if (!jmap_set.HasKey("items") || jmap_set["items"].Count == 0)
            { 
                Log.Warn($"Map-set '{map_set_id}' does not contain any maps!");
                // XXX return anyway?
                return null;
            }

            map_set = new MapSet(map_set_id);
            jmaps = jmap_set["items"].AsArray;

            if (config_url == "" && jmap_set.HasKey("config_url"))
                config_url = jmap_set["config_url"];

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
                if (!jmap.HasKey("id"))
                {
                    Log.Err($"Map does not have 'id' set, ignoring whole map set!");
                    return null;
                }

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
                        Log.Info($"Assuming image source relative to config URL: '{imgurl}'");
                    }

                    min_lat = jmap["lat_range"][0];
                    max_lat = jmap["lat_range"][1];
                    min_lon = jmap["lon_range"][0];
                    max_lon = jmap["lon_range"][1];

                    map = new OSMMap(map_id, min_lat, max_lat, min_lon, max_lon);
                    map_set.Add(map_id, map);

                    overall_min_lat = MathF.Min(overall_min_lat, min_lat);
                    overall_max_lat = MathF.Max(overall_max_lat, max_lat);
                    overall_min_lon = MathF.Min(overall_min_lon, min_lon);
                    overall_max_lon = MathF.Max(overall_max_lon, max_lon);

                    Log.Info($"Scheduling fetching of map image {imgurl} for map '{map.id}'");
                    ScheduleURLFetch(imgurl, "map_image", true, map);

                }
                else if (imgsource["type"] == "tile_server")
                {
                    if (!jmap.HasKey("zoom"))
                    {
                        Log.Err("Map specification '{map_id}' uses tile-server, but is missing 'zoom' value, ignoring map!");
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

                    string tile_server_id = imgsource["id"];
                    if (!tile_server_configurations.ContainsKey(tile_server_id))
                    {
                        Log.Err("Map specification '{map_id}' is using unknown tile-server id '{tile_server_id}', ignoring map!");
                        continue;
                    }
                    
                    overall_min_lat = MathF.Min(overall_min_lat, min_lat);
                    overall_max_lat = MathF.Max(overall_max_lat, max_lat);
                    overall_min_lon = MathF.Min(overall_min_lon, min_lon);
                    overall_max_lon = MathF.Max(overall_max_lon, max_lon);

                    map_set.Add(map_id, map);

                    // XXX should not schedule from here, but when selecting this map set
                    Log.Info($"Scheduling fetching of map tiles for map '{map.id}' ({min_i}-{max_i}, {min_j}-{max_j}, {jmap["zoom"]})");
                    ScheduleTileFetch(min_i, max_i, min_j, max_j, jmap["zoom"], tile_server_configurations[tile_server_id].urls, map);
                }
                else
                    Log.Warn($"Unknown map image-source type '{imgsource["type"]}'!");
            }

            map_set.query_extent = new Vec4(overall_min_lat, overall_max_lat, overall_min_lon, overall_max_lon);
            Log.Info($"Setting plane data query extent to {map_set.query_extent}, based on union of map extents (map-set '{map_set.id}')");            
                        
            return map_set;
        }

        public void SelectMapSet(string id)
        {
            JSONNode jmapset;

            Log.Info($"Selecting map-set '{id}'");

            if (!map_sets.ContainsKey(id))
            {
                if (!stored_map_sets.Contains(id))
                {
                    Log.Err($"Map-set '{id}' not available, nor stored!");
                    return;
                }

                jmapset = ConfigurationStore.Load(ConfigurationStore.ConfigType.MAP_SET, id);
                if (jmapset == null)
                {
                    Log.Err($"Error loading map-set '{id}' from storage!");
                    return;
                }

                map_sets[id] = ParseMapSet(jmapset);
            }
                
            current_map_set = map_sets[id];
            current_map_set_name = id;

            maps_in_current_set = current_map_set.maps;

            SelectMap(current_map_set.default_map);

            // XXX Update/clear observer
            // XXX fold into SelectMap?
            current_observer.update_map_position(current_map);

            ClearAllPlaneData();

            Log.Info($"Setting plane data query extent to {current_map_set.query_extent}");
            query_extent_update_queue.Enqueue(current_map_set.query_extent);
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

            map_center_observer.lat = current_map.center_lat;
            map_center_observer.lon = current_map.center_lon;
            current_observer.update_map_position(current_map);
        }

        // Observer sets

        public void SelectObserverSet(string id)
        {
            JSONNode jobserverset;

            Log.Info($"Selecting observer-set '{id}'");

            if (!observer_sets.ContainsKey(id))
            {
                if (!stored_observer_sets.Contains(id))
                {
                    Log.Err($"Observer-set '{id}' not available, nor stored!");
                    return;
                }

                jobserverset = ConfigurationStore.Load(ConfigurationStore.ConfigType.OBSERVER_SET, id);
                if (jobserverset == null)
                {
                    Log.Err($"Error loading observer-set '{id}' from storage!");
                    return;
                }

                observer_sets[id] = ParseObserverSet(jobserverset);
            }

            current_observer_set = observer_sets[id];
            current_observer_set_name = id;

            observers_in_current_set = current_observer_set.observers;

            SelectObserver(current_observer_set.default_observer);
        }

        public void SelectObserver(string id)
        {
            Log.Info($"Selecting observer '{id}'");

            current_observer_name = id;
            current_observer = observers_in_current_set[id];

            current_observer.update_map_position(current_map);
            RecomputeLandmarkPositions();

            foreach (PlaneData pd in plane_data.Values)
                pd.ObserverChange(current_observer);            
        }

        public ObserverSet ParseObserverSet(JSONNode jobserver_set)
        {
            JSONArray jobservers;
            ObserverSet observer_set;
            string observer_set_id;
            Observer observer;

            if (!jobserver_set.HasKey("id"))
            {
                // XXX list index in sets in message
                Log.Warn($"Observer-set does not have 'id' field!");
                return null;
            }

            observer_set_id = jobserver_set["id"];

            // XXX avoid '<default>'?
            if (observer_set_id == "")
            {
                Log.Warn($"Observer-set should not have empty 'id' field!");
                return null;
            }

            if (!jobserver_set.HasKey("items") || jobserver_set["items"].Count == 0)
            {
                Log.Warn($"Observer-set '{observer_set_id}' does not contain any observers!");
                // XXX return anyway?
                return null;
            }

            observer_set = new ObserverSet(observer_set_id);
            jobservers = jobserver_set["items"].AsArray;

            foreach (JSONNode n in jobservers)
            {
                // XXX needs more checks
                string ob_id = n["id"];
                float lat = n["lat"];
                float lon = n["lon"];
                float floor_altitude = n["floor_altitude"];

                observer = new Observer(ob_id, lat, lon, floor_altitude);
                observer_set.Add(ob_id, observer);
            }

            return observer_set;
        }

        // Landmark sets

        public void SelectLandmarkSet(string id)
        {
            JSONNode jlandmarkset;

            Log.Info($"Selecting landmark-set '{id}'");

            if (!landmark_sets.ContainsKey(id))
            {
                if (!stored_landmark_sets.Contains(id))
                {
                    Log.Err($"Landmark-set '{id}' not available, nor stored!");
                    return;
                }

                jlandmarkset = ConfigurationStore.Load(ConfigurationStore.ConfigType.LANDMARK_SET, id);
                if (jlandmarkset == null)
                {
                    Log.Err($"Error loading landmark-set '{id}' from storage!");
                    return;
                }

                landmark_sets[id] = ParseLandmarkSet(jlandmarkset);
            }

            current_landmark_set = landmark_sets[id];
            current_landmark_set_name = id;            

            landmarks_in_current_set = current_landmark_set.landmarks;
            sorted_landmark_names = landmarks_in_current_set.Keys.ToList();
            sorted_landmark_names.Sort();

            current_alignment_landmark = "";
            if (sorted_landmark_names.Count > 0)
                current_alignment_landmark = sorted_landmark_names[0];

            RecomputeLandmarkPositions();
        }

        public LandmarkSet ParseLandmarkSet(JSONNode jlandmark_set)
        {
            JSONArray jlandmarks;
            LandmarkSet landmark_set;
            string landmark_set_id;
            Landmark landmark;

            if (!jlandmark_set.HasKey("id"))
            {
                // XXX list index in sets in message
                Log.Warn($"Landmark-set does not have 'id' field!");
                return null;
            }

            landmark_set_id = jlandmark_set["id"];

            // XXX avoid '<default>'?
            if (landmark_set_id == "")
            {
                Log.Warn($"Landmark-set should not have empty 'id' field!");
                return null;
            }

            if (!jlandmark_set.HasKey("items") || jlandmark_set["items"].Count == 0)
            {
                Log.Warn($"Landmark-set '{landmark_set_id}' does not contain any landmarks!");
                // XXX return anyway?
                return null;
            }

            landmark_set = new LandmarkSet(landmark_set_id);
            jlandmarks = jlandmark_set["items"].AsArray;

            foreach (JSONNode n in jlandmarks)
            {
                // XXX needs more checks
                string lm_id = n["id"];
                float lat = n["lat"];
                float lon = n["lon"];
                float top_altitude = n["topalt"];
                float bottom_altitude = n["botalt"];

                landmark = new Landmark(lm_id, lat, lon, top_altitude, bottom_altitude);
                landmark_set.Add(lm_id, landmark);
            }

            return landmark_set;
        }

        public void RecomputeLandmarkPositions()
        {
            float x, y;
            Matrix M;

            alignment_solver.ClearReferences();

            SchedulePostMessage($"RecomputeLandmarkPositions: observer lat {current_observer.lat:F6}, lon {current_observer.lon:F6}, alt {current_observer.floor_altitude:F6}");

            foreach (Landmark lm in landmarks_in_current_set.Values)
            {
                // Map (km)
                current_map.Project(out x, out y, lm.lon, lm.lat);
                lm.map_position = new Vec3(x, y, lm.top_altitude / 1000f);

                // Sky (m)
                M = Matrix.R(-lm.lat, 0f, 0f)
                    *
                    Matrix.R(0f, lm.lon - current_observer.lon, 0f)
                    *
                    Matrix.R(current_observer.lat, 0f, 0f)
                    *
                    // XXX Should also include have-above-floor distance, but the effect will be minimal
                    Matrix.T(0f, 0f, -(Projection.RADIUS_METERS + current_observer.floor_altitude));

                Vec3 p = new Vec3(0f, 0f, Projection.RADIUS_METERS + lm.top_altitude);

                lm.sky_position = M.Transform(p);

                Log.Info($"Landmark '{lm.id}' sky pos reference (Z-up) = {lm.sky_position}");
                SchedulePostMessage($"(skypos reference, Z-up) landmark {lm.id} lat {lm.lat}, lon {lm.lon} -> {lm.sky_position.x:F6}, {lm.sky_position.y:F6}, {lm.sky_position.z:F6}");

                // Note: Z-up to Y-up
                // XXX Should really alter the above transform to produce Y-up positions directly
                alignment_solver.SetReference(lm.id, ROT_90_X.Transform(lm.sky_position));
            }
        }

        // Configurations

        public void UpdateConfigurationLists()
        {
            ConfigurationStore.List(ConfigurationStore.ConfigType.MAP_SET, ref stored_map_sets);
            stored_map_sets.Insert(0, "<default>");            

            ConfigurationStore.List(ConfigurationStore.ConfigType.OBSERVER_SET, ref stored_observer_sets);
            stored_observer_sets.Insert(0, "<default>");

            ConfigurationStore.List(ConfigurationStore.ConfigType.LANDMARK_SET, ref stored_landmark_sets);
        }

        public void ProcessConfigurationData(string config_string, string config_url)
        {
            // XXX handle error
            JSONNode config_root = JSON.Parse(config_string);
            Log.Info($"config_string = '{config_string}', parsed to '{config_root.ToString()}'");

            // XXX only set this when config actually changes
            current_configuration_name = config_url;
            if (current_configuration_name.Length > 63)
                current_configuration_name = current_configuration_name.Substring(0, 30) + "..." + current_configuration_name.Substring(current_configuration_name.Length - 30);

            bool observer_updated = false;

            if (config_root.HasKey("map_sets"))
            {
                JSONArray jmap_sets = config_root["map_sets"].AsArray;
                MapSet map_set;
                bool first = true;
                int set_idx = 0;

                foreach (JSONNode jmap_set in jmap_sets)
                {                    
                    map_set = ParseMapSet(jmap_set, config_url);

                    if (map_set == null)
                    {
                        Log.Warn($"Map-set (index {set_idx} could not be parsed, ignoring");
                        continue;
                    }

                    map_sets[map_set.id] = map_set;

                    // Hack in the original config_url, so we can restore properly later on
                    jmap_set["config_url"] = config_url;

                    ConfigurationStore.Store(ConfigurationStore.ConfigType.MAP_SET, map_set.id, jmap_set);                    

                    if (first)
                    {
                        // Switch to this map set
                        SelectMapSet(map_set.id);
                        first = false;
                    }

                    set_idx++;
                }
            }

            if (config_root.HasKey("landmark_sets"))
            {
                JSONArray jlandmark_sets = config_root["landmark_sets"].AsArray;
                LandmarkSet landmark_set;
                bool first = true;
                int set_idx = 0;

                foreach (JSONNode jlandmark_set in jlandmark_sets)
                {
                    landmark_set = ParseLandmarkSet(jlandmark_set);

                    if (landmark_set == null)
                    {
                        Log.Warn($"Landmark-set (index {set_idx} could not be parsed, ignoring");
                        continue;
                    }

                    landmark_sets[landmark_set.id] = landmark_set;

                    ConfigurationStore.Store(ConfigurationStore.ConfigType.LANDMARK_SET, landmark_set.id, jlandmark_set);

                    if (first)
                    {
                        // Switch to this landmark set
                        SelectLandmarkSet(landmark_set.id);
                        first = false;
                    }

                    set_idx++;
                }
            }

            if (config_root.HasKey("observer_sets"))
            {
                JSONArray jobserver_sets = config_root["observer_sets"].AsArray;
                ObserverSet observer_set;
                bool first = true;
                int set_idx = 0;

                foreach (JSONNode jobserver_set in jobserver_sets)
                {
                    observer_set = ParseObserverSet(jobserver_set);

                    if (observer_set == null)
                    {
                        Log.Warn($"Observer-set (index {set_idx} could not be parsed, ignoring");
                        continue;
                    }

                    observer_sets[observer_set.id] = observer_set;

                    ConfigurationStore.Store(ConfigurationStore.ConfigType.OBSERVER_SET, observer_set.id, jobserver_set);

                    if (first)
                    {
                        // Switch to this observer set
                        SelectObserverSet(observer_set.id);
                        first = false;
                    }

                    set_idx++;
                }
            }

            if (config_root.HasKey("discord_webhook"))
            {
                JsonObject obj = JsonObject.Parse(config_string);
                discord_webhook_url = obj["discord_webhook"].GetString();
                ConfigurationStore.StoreOption("discord_webhook", discord_webhook_url);
                Log.Info($"Discord webhook set to {discord_webhook_url}");
            }

            if (observer_updated)
                ObserverChanged();

            UpdateConfigurationLists();
        }
    }
}
