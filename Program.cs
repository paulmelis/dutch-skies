using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.IO;
using System.Threading;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Microsoft.MixedReality.QR;
using SimpleJSON;
using StereoKit;

namespace DutchSkies
{
    using TileFetchRequest = Tuple<int, int, int, int, int, string[], string>;

    class Program
    {
        enum DetailLevel { NONE, CALLSIGN, FULL };
        const float PLANE_SIZE_METERS = 0.015f;

        // Log
        static List<string> log_lines = new List<string>();
        static string log_text = "";
        static LogLevel log_level = LogLevel.Info;

        static Dictionary<string, PlaneData> plane_data;

        // Map geometry 
        const float REALWORLD_MAP_WIDTH = 1.5f;     // meters
        static float realworld_map_height;
        const float MAP_WINDROSE_SIZE = 0.1f;      // meters

        static Dictionary<string, OSMMap> maps;
        static string current_map_name;
        static OSMMap current_map;
        static Material map_material;
        static float map_scale_km_to_scene;
        static Mesh map_quad;
        static Tex map_texture;

        // Sky mode
        static Dictionary<string, Landmark> landmarks;
        static ObserverData observer;
        
        const float OBSERVER_WINDROSE_SIZE = 1f;    // meters

        // Query        
        const int OPENSKY_QUERY_INTERVAL = 8;

        // XXX rename payload to marker or something
        // Thread event queue (type, data, payload)        
        static ConcurrentQueue<Tuple<string, object, string>> updates_queue;
        // URL requests queue (url, type, binary, payload)
        static ConcurrentQueue<Tuple<string, string, bool, string>> url_requests_queue;
        // Tile fetch queue (mini/j, maxi/j, zoom, payload)
        static BlockingCollection<TileFetchRequest> tile_requests_queue;

        // QR code scanning
        static bool scanning_for_qrcodes;
        static QRCodeWatcher qrcode_watcher;
        static DateTime qrcode_watcher_start;
        //Dictionary<Guid, QRData> poses = new Dictionary<Guid, QRData>();
        static System.Guid last_qrcode_id;

        static void OnLog(LogLevel level, string text)
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

            // Some often used transforms
            Matrix ROT_MIN90_X = Matrix.R(-90f, 0f, 0f);
            Matrix ROT_180_Y = Matrix.R(0f, 180f, 0f);

            // Determine IP address (useful in debugging)
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

            // Planes

            plane_data = new Dictionary<string, PlaneData>();

            // Observer

            observer = new ObserverData();
            
            Mesh observer_cylinder_marker = Mesh.GenerateCylinder(0.002f, 0.02f, Vec3.UnitY, 8);
            Mesh observer_sphere_marker = Mesh.GenerateSphere(0.006f, 8);
            Material observer_marker_material = Default.Material.Copy();
            observer_marker_material[MatParamName.ColorTint] = new Color(1f, 0.5f, 0f);

            landmarks = new Dictionary<string, Landmark>();

            Mesh windrose_mesh = Mesh.GeneratePlane(new Vec2(1f, 1f), -Vec3.Forward, Vec3.Up);
            Material windrose_material = Material.Default.Copy();
            windrose_material[MatParamName.DiffuseTex] = Tex.FromFile("Windrose.png");
            windrose_material.Transparency = Transparency.Blend;
            windrose_material.DepthWrite = false;

            //
            // Maps
            //

            Matrix MAP_PLACEMENT_XFORM = Matrix.T(1f * Vec3.Forward - 0.7f * Vec3.Up);

            maps = new Dictionary<string, OSMMap>();

            map_material = Default.Material.Copy();
            // Disable backface culling on the map for now, for debugging
            map_material.FaceCull = Cull.None;

            map_scale_km_to_scene = 0.001f;

            // Prepare built in maps and select default
            PrepareMaps();
            SetMap("The Netherlands");
            //SetMap("Schiphol Airport");

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

            Matrix floorTransform = Matrix.TS(0, -1.5f, 0, new Vec3(30, 0.1f, 30));
            Material floorMaterial = new Material(Shader.FromFile("floor.hlsl"));
            floorMaterial.Transparency = Transparency.Blend;

            //
            // Start some background threads
            // XXX can probably take more advantage of async/await, but let's use threads for now
            //

            // Queue for receiving updates from threads
            updates_queue = new ConcurrentQueue<Tuple<string, object, string>>();

            // Set query extent, based on map (minlat, maxlat, minlon, maxlon)
            Vec4 data_query_extent = new Vec4(current_map.min_lat, current_map.max_lat, current_map.min_lon, current_map.max_lon);

            // Launch data update thread            
            // Queue for sending updated query range to thread
            ConcurrentQueue<Vec4> query_extent_update_queue = new ConcurrentQueue<Vec4>();
            // Push initial query extent
            query_extent_update_queue.Enqueue(data_query_extent);
            var plane_update_thread = new Thread(FetchPlaneUpdates);
            plane_update_thread.IsBackground = true;
            plane_update_thread.Start(query_extent_update_queue);
            Log.Info("Plane update thread started");

            // Launch config fetch thread
            // Queue for fetching different types of data by URL
            url_requests_queue = new ConcurrentQueue<Tuple<string, string, bool, string>>();
            var url_fetch_thread = new Thread(FetchURLThread);
            url_fetch_thread.IsBackground = true;
            url_fetch_thread.Start();
            Log.Info("URL fetch thread started");

            // Tile fetch thread
            // input: lat/lon range, zoomlevel, tile servers
            tile_requests_queue = new BlockingCollection<TileFetchRequest>(new ConcurrentQueue<TileFetchRequest>());
            var tiles_fetch_thread = new Thread(OSMTiles.FetchMapTiles);
            tiles_fetch_thread.IsBackground = true;            
            tiles_fetch_thread.Start(new Tuple<object,object>(tile_requests_queue,updates_queue));
            Log.Info("Tile fetch thread started");

            // XXX
            //string initial_config = "http://192.168.178.32:8000/config-netherlands-and-schiphol-image.json";
            //string initial_config = "http://192.168.178.32:8000/config-newyork-image.json";
            //string initial_config = "http://192.168.178.32:8000/config-alps-image.json";
            //string initial_config = "http://192.168.178.32:8000/sanfrancisco-osmtiles.json";
            //ScheduleURLFetch(initial_config, "config_data", false, initial_config);

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

            // Main settings

            Pose main_window_pose = new Pose(0.5f, -0.2f, -0.5f, Quat.LookDir(-1, 0, 1));
            Pose log_window_pose = new Pose(0.9f, -0.2f, 0f, Quat.LookDir(-1, 0, 1));

            DetailLevel detail_level = DetailLevel.FULL;
            bool show_log_window = true;
            bool show_flight_units = false;
            bool map_visible = true;
            bool map_show_plane_model = true, sky_show_plane_models = true;
            bool map_show_vlines = true, sky_show_vlines = false;
            bool map_show_track_lines = true, sky_show_trail_lines = true;
            bool map_show_observer = false;
            bool sky_show_landmarks = true;
            bool show_origin = false;            
            int sky_d_trim = 0;         // In 0.1 degree increments
            int sky_v_trim = 0;         // In centimeters

            int num_planes_on_map = 0;
            int num_planes_late = 0, num_planes_missing = 0;
            int num_planes_on_ground = 0;

            const float TRACK_LINE_THICKNESS = 0.001f;
            Color MAP_BASE_COLOR = new Color(1f, 0f, 0.5f);
            Color32 MAP_TRACK_LINE_COLOR = new Color32(0, 0, 255, 255);
            Color SKY_BASE_COLOR = new Color(1f, 0f, 0f);
            Color SKY_TRAIL_LINE_COLOR = new Color(0.4f, 1f, 0.4f);
            Color LANDMARK_VLINE_COLOR = new Color(1f, 0f, 1f);            

            const float SKY_SCALING_THRESHOLD = 3f;
            Matrix SKY_FAR_MODEL_SCALE = Matrix.S(30f);
            Matrix SKY_CLOSE_MODEL_SCALE = Matrix.S(60f);

            // XXX style uses gamma-space color, leading to slight difference with vline color
            TextStyle MAP_TEXT_STYLE = Text.MakeStyle(Default.Font, 0.7f * U.cm, MAP_BASE_COLOR);
            TextStyle SKY_TEXT_STYLE = Text.MakeStyle(Default.Font, 15f * U.m, SKY_BASE_COLOR);
            TextStyle LANDMARK_TEXT_STYLE = Text.MakeStyle(Default.Font, 1f * U.m, LANDMARK_VLINE_COLOR);
            TextStyle MAP_DIMENSION_TEXT_STYLE = Text.MakeStyle(Default.Font, 0.01f * U.m, Color.White);

            Tuple<string, object, string> update;
            string update_type;
            JSONNode root_node;

            int fps_num_frames = 0;
            float fps_start_time = Time.Totalf;
            float fps = 0f;

            // Core application loop
            while (SK.Step(() =>
            {
                double draw_time = DateTimeOffset.Now.ToUnixTimeMilliseconds() * 0.001;
                Vec3 head_pos = Input.Head.position;

                if (SK.System.displayType == Display.Opaque)
                    Default.MeshCube.Draw(floorMaterial, floorTransform);

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
                        Log.Info($"Got updated map image ({map_image_file.Length} bytes), for map {map_to_update}");
                        maps[map_to_update].texture = Tex.FromMemory(map_image_file);
                        if (current_map_name == map_to_update)
                            map_material[MatParamName.DiffuseTex] = maps[map_to_update].texture;
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
                        string config_string = update.Item2 as string;
                        // XXX handle error
                        JSONNode config_root = JSON.Parse(config_string);
                        bool map_updated = false;

                        if (config_root.HasKey("query"))
                        {
                            JSONNode query = config_root["query"];

                            Vec4 extent = new Vec4(query["lat_range"][0], query["lat_range"][1], query["lon_range"][0], query["lon_range"][1]);
                            Log.Info($"Setting plane data query extent to {extent}");
                            query_extent_update_queue.Enqueue(extent);
                        }

                        if (config_root.HasKey("maps"))
                        {
                            JSONArray jmaps = config_root["maps"].AsArray;

                            if (jmaps.Count > 0)
                            {
                                maps.Clear();

                                bool first = true;
                                float min_lat, max_lat, min_lon, max_lon;

                                foreach (JSONNode jmap in jmaps)
                                {
                                    string name = jmap["name"];
                                    Log.Info($"Have new map '{name}'");

                                    JSONNode imgsource = jmap["image_source"];
                                    if (imgsource["type"] == "url")
                                    {
                                        string imgurl = imgsource["url"];
                                        if (!(imgurl.StartsWith("http://") || imgurl.StartsWith("https://")))
                                        {
                                            // Assume path relative to config url
                                            Uri uri = new Uri(update.Item3);
                                            string path = Path.GetDirectoryName(uri.AbsolutePath).Replace("\\", "/");
                                            if (imgurl[0] != '/')
                                                path += '/';
                                            imgurl = $"{uri.GetLeftPart(UriPartial.Authority)}{path}{imgurl}";
                                            Log.Info($"Assuming image source relative to config URL: {imgurl}");
                                        }

                                        Log.Info($"Scheduling fetching of map image {imgurl} for map '{name}'");
                                        ScheduleURLFetch(imgurl, "map_image", true, name);

                                        min_lat = jmap["lat_range"][0];
                                        max_lat = jmap["lat_range"][1];
                                        min_lon = jmap["lon_range"][0];
                                        max_lon = jmap["lon_range"][1];

                                        OSMMap map = maps[name] = new OSMMap(name, min_lat, max_lat, min_lon, max_lon);
                                    }
                                    else if (imgsource["type"] == "osm")
                                    {
                                        if (!jmap.HasKey("lat_range"))
                                        {
                                            Log.Warn("(tile fetch thread) Map specification is missing 'lat_range'!");
                                            continue;
                                        }
                                        if (!jmap.HasKey("lon_range"))
                                        {
                                            Log.Warn("(tile fetch thread) Map specification is missing 'lon_range'!");
                                            continue;
                                        }
                                        if (!jmap.HasKey("zoom"))
                                        {
                                            Log.Warn("(tile fetch thread) Map specification is missing 'zoom'!");
                                            continue;
                                        }

                                        JSONNode j_tile_servers = jmap["image_source"]["tile_servers"];
                                        string[] tile_servers = new string[j_tile_servers.Count];
                                        for (int i = 0; i < j_tile_servers.Count; i++)
                                            tile_servers[i] = j_tile_servers[i];

                                        Vec4 query_extent = new Vec4(jmap["lat_range"][0], jmap["lat_range"][1], jmap["lon_range"][0], jmap["lon_range"][1]);

                                        int min_i, max_i, min_j, max_j;

                                        // The actual map extent will be based on a set of unclipped tiles, so we need it here to set up the
                                        // map correctly before the image fetch is complete.
                                        OSMTiles.ComputeActualMapExtent(
                                            out min_lat, out max_lat, out min_lon, out max_lon,
                                            out min_i, out max_i, out min_j, out max_j,
                                                query_extent, jmap["zoom"]);

                                        OSMMap map = maps[name] = new OSMMap(name, min_lat, max_lat, min_lon, max_lon);

                                        Log.Info($"Scheduling fetching of map tiles for map '{name}' ({min_i}-{max_i}, {min_j}-{max_j}, {jmap["zoom"]})");
                                        ScheduleTileFetch(min_i, max_i, min_j, max_j, jmap["zoom"], tile_servers, name);
                                    }

                                    if (first)
                                    {
                                        // Update map to use first entry
                                        SetMap(name, draw_time);
                                        first = false;
                                    }
                                }

                                map_updated = true;
                            }
                            else
                                Log.Warn("No maps defined in config, not updating!");
                        }

                        if (config_root.HasKey("observer"))
                        {
                            JSONNode jobs = config_root["observer"];
                            observer.lat = jobs["lat"];
                            observer.lon = jobs["lon"];
                            observer.floor_altitude = jobs["alt"];
                        }
                        else
                        {
                            // Need some setting for observer, so pick map center at 0m altitude
                            observer.lat = current_map.center_lat;
                            observer.lon = current_map.center_lon;
                            observer.floor_altitude = 0f;                            
                        }

                        observer.update_map_position(current_map);
                        // XXX Also need sky update

                        if (config_root.HasKey("landmarks"))
                            UpdateLandmarks(config_root["landmarks"]);

                        if (map_updated)
                            plane_data.Clear();
                    }
                    else if (update_type == "map_tilefetch_progress")
                    {
                        // XXX do something with it :)
                    }
                    else
                        Log.Warn($"Unhandled update type '{update_type}!");
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
                        Matrix.T(-REALWORLD_MAP_WIDTH*0.5f + 0.52f*MAP_WINDROSE_SIZE, ABOVE, realworld_map_height*0.5f -0.52f*MAP_WINDROSE_SIZE));

                    // Dimensions
                    Text.Add($"{current_map.width:F0} km", ROT_180_Y*ROT_MIN90_X*Matrix.T(0f, 0f, 0.5f*realworld_map_height+0.01f), MAP_DIMENSION_TEXT_STYLE,
                        TextAlign.TopCenter);
                    Text.Add($"{current_map.height:F0} km", ROT_180_Y*ROT_MIN90_X*Matrix.R(0f,-90f,0f)*Matrix.T(0.5f * REALWORLD_MAP_WIDTH+0.01f, 0f, 0f), MAP_DIMENSION_TEXT_STYLE,
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

                Hierarchy.Push(Matrix.R(0f, -sky_d_trim * 0.1f, 0f)*Matrix.T(0f, sky_v_trim*0.1f, 0f));

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
                windrose_mesh.Draw(windrose_material, ROT_MIN90_X*Matrix.S(OBSERVER_WINDROSE_SIZE)*Matrix.T(0f, -1.5f, 0f));

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
                UI.WindowBegin("Dutch SKies", ref main_window_pose, new Vec2(60, 0) * U.cm, UIWin.Normal);

                UI.Toggle("Flight units", ref show_flight_units);
                UI.SameLine();
                if (UI.Button("Clear tracks"))
                    ClearTracks();
                UI.SameLine();
                // See https://github.com/maluoi/StereoKit/issues/248
                UI.Space(-0.04f);
                if (UI.Toggle("Scan QR code", ref scanning_for_qrcodes))
                {
                    //Log.Info($"qr code button toggled, now {scanning_for_qrcodes}");
                    SetQRCodeScan();
                }
                UI.Label($"{num_planes_on_map} planes shown ({num_planes_on_ground} on ground) • {plane_data.Count} total, {num_planes_late} late, {num_planes_missing} missing");

                UI.HSeparator();
                UI.PushId("map");

                UI.Label("Map:");
                foreach (OSMMap map in maps.Values)
                {
                    UI.SameLine();
                    if (UI.Radio(map.name, current_map_name == map.name))
                    {
                        // Switch map
                        SetMap(map.name, draw_time);

                        // Signal update to query extent
                        data_query_extent = new Vec4(current_map.min_lat, current_map.max_lat, current_map.min_lon, current_map.max_lon);
                        query_extent_update_queue.Enqueue(data_query_extent);
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
                UI.Toggle($"Landmarks({landmarks.Count})", ref sky_show_landmarks);

                UI.Space(0.01f);

                UI.PushId("htrim");
                UI.Label("H Trim (°)");
                UI.SameLine();
                UI.Space(-0.014f);
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
                UI.Label("V Trim (cm)");
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

                UI.PopId();

                UI.HSeparator();
                string time = DateTime.Now.ToString("HH:mm:ss");
                UI.Label("Debug:");
                UI.SameLine();
                UI.Toggle("Log", ref show_log_window);
                UI.SameLine();
                UI.Toggle("Origin", ref show_origin);
                UI.SameLine();
                UI.Label($"IP:{our_ip} • {time} • {fps:F1} FPS");
                UI.SameLine();
                // XXX log window
                UI.WindowEnd();

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
            }));

            if (qrcode_watcher != null)
                qrcode_watcher.Stop();

            SK.Shutdown();
        }

        public static void PrepareMaps()
        {            
            OSMMap map;

            // Whole of the Netherlands
            map = maps["The Netherlands"] = new OSMMap(
                    "The Netherlands",
                    50.513427f, 53.956086f, 2.812500f, 8.085938f, 10
                );

            map.texture = Tex.FromFile("Maps\\netherlands-lon-2.812500-8.085938-lat-50.513427-53.956086-c-5.449219-52.234756-z10-3840x4096.png");

            // Schiphol Airport
            map = maps["Schiphol Airport"] = new OSMMap(
                "Schiphol Airport",
                51.890054f, 52.696361f, 4.042969f, 5.361328f, 12
            );

            map.texture = Tex.FromFile("Maps\\schiphol-lon-4.042969-5.361328-lat-51.890054-52.696361-c-4.702148-52.293208-z12-3840x3840.png");

            // Eindhoven Airport
            map = maps["Eindhoven Airport"] = new OSMMap(
                "Eindhoven Airport",
                51.28940590271678f, 51.6180165487737f, 5.09765625f, 5.712890625f, 12
            );

            map.texture = Tex.FromFile("Maps\\eindhoven.png");
        }

        public static void SetMap(string map, double draw_time=0.0)
        {
            current_map_name = map;
            current_map = maps[map];
            
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

            // Observer will move as well
            observer.update_map_position(current_map);
        }

        public static void ClearTracks()
        {
            foreach (var plane in plane_data.Values)
                plane.ClearTrack();
        }

        static void ScheduleURLFetch(string url, string type, bool binary, string payload)
        {
            url_requests_queue.Enqueue(new Tuple<string, string, bool, string>(url, type, binary, payload));
        }

        static void ScheduleTileFetch(int min_i, int max_i, int min_j, int max_j, int zoom, string[] servers, string map_name)
        {
            TileFetchRequest request = new TileFetchRequest(min_i, max_i, min_j, max_j, zoom, servers, map_name);
            tile_requests_queue.Add(request);
        }

        static async void FetchURLThread()
        {
            // XXX someone wrong with using the blocking collection?
            // YYY probably need a BlockingCollection in the main thread as well, when placing items in it
            //using (BlockingCollection<string> blocking_queue = new BlockingCollection<string>(url_input_queue))
            {
                Tuple<string, string, bool, string> request;
                string type, url, payload;
                bool binary;

                HttpClient http_client = new HttpClient();

                while (true)
                {
                    //Log.Info($"(configuration fetch thread) waiting for url");
                    //url = blocking_queue.Take();

                    if (url_requests_queue.TryDequeue(out request))
                    {
                        // Got updated extent
                        url = request.Item1;
                        type = request.Item2;
                        binary = request.Item3;
                        payload = request.Item4;

                        Log.Info($"(URL fetch thread) Fetching URL {url} (type '{type}', binary {binary}, payload '{payload}')");

                        try
                        {
                            HttpResponseMessage response = await http_client.GetAsync(url);
                            response.EnsureSuccessStatusCode();
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
                            Log.Info("(URL fetch thread): Exception " + e.Message);
                        }
                    }

                    Thread.Sleep(500);
                }
            }
        }

        static async void FetchPlaneUpdates(object obj)
        {
            ConcurrentQueue<Vec4> extent_input_queue = obj as ConcurrentQueue<Vec4>;

            Vec4 extent;
            HttpClient http_client = new HttpClient();

            // Wait for initial extent
            while (!extent_input_queue.TryDequeue(out extent))
                Thread.Sleep(100);

            Log.Info($"(data fetch thread) Initial query extent: lat {extent.x:F6} - {extent.y:F6}, lon {extent.z:F6} - {extent.w:F6}");
            string URL = $"https://opensky-network.org/api/states/all?lamin={extent.x}&lamax={extent.y}&lomin={extent.z}&lomax={extent.w}";

            while (true)
            {
                if (extent_input_queue.TryDequeue(out extent))
                {
                    // Got updated extent
                    Log.Info($"(data fetch thread) Got updated query extent: lat = {extent.x:F6} - {extent.y:F6}, lon = {extent.z:F6} - {extent.w:F6}");
                    URL = $"https://opensky-network.org/api/states/all?lamin={extent.x}&lamax={extent.y}&lomin={extent.z}&lomax={extent.w}";
                }

                try
                {                    
                    HttpResponseMessage response = await http_client.GetAsync(URL);
                    response.EnsureSuccessStatusCode();
                    string body = await response.Content.ReadAsStringAsync();
                    //Log.Info("(data fetch): " + body);

                    JSONNode root_node = JSON.Parse(body);
                    updates_queue.Enqueue(new Tuple<string,object,string>("plane_data", root_node, ""));
                }
                catch (HttpRequestException e)
                {
                    Log.Info("(data fetch): Exception " + e.Message);
                }

                Thread.Sleep(OPENSKY_QUERY_INTERVAL * 1000);
            }
        }

        public static void SetQRCodeScan()
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
        public static void UpdateLandmarks(JSONNode nodes)
        {
            float x, y;
            Matrix M;
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

                // Map (km)
                current_map.Project(out x, out y, lon, lat);
                lm.map_position = new Vec3(x, y, top_altitude / 1000f);

                // Sky (m)
                M = Matrix.R(-lat, 0f, 0f)
                    *
                    Matrix.R(0f, lon - observer.lon, 0f)
                    *
                    Matrix.R(observer.lat, 0f, 0f)
                    *
                    // XXX Should also include have-above-floor distance, but the effect will be minimal
                    Matrix.T(0f, 0f, -(Projection.RADIUS_METERS + observer.floor_altitude));

                Vec3 p = new Vec3(0f, 0f, Projection.RADIUS_METERS + top_altitude);

                lm.sky_position = M.Transform(p);
                Log.Info($"landmark '{id}' sky pos = {lm.sky_position}");
            }
        }

    }
}
