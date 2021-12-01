﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Microsoft.MixedReality.QR;
using SimpleJSON;
using StereoKit;

namespace DutchSkies
{
    class Program
    {
        enum DetailLevel { NONE, CALLSIGN, FULL };
        const float PLANE_SIZE_METERS = 0.015f;

        // Log
        static List<string> log_lines = new List<string>();
        static string log_text = "";

        static Dictionary<string, PlaneData> plane_data;

        // Map geometry 
        const float REALWORLD_MAP_WIDTH = 1.5f;     // meters
        static Dictionary<string, OSMMap> maps;
        static string current_map_name;
        static OSMMap current_map;
        static Material map_material;
        static float map_scale_km_to_scene;
        static Mesh map_quad;
        static Tex map_texture;

        static ObserverData observer;

        // Query
        static Vec4 data_query_extent;
        const int OPENSKY_QUERY_INTERVAL = 8;

        // Thread event queue
        static ConcurrentQueue<Tuple<string, object>> updates_queue;

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
            log_lines.Add(text.Length < 120 ? text : text.Substring(0, 120) + "...\n");

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

            // Set query extent, based on map
            data_query_extent = new Vec4(current_map.min_lat, current_map.max_lat, current_map.min_lon, current_map.max_lon);

            // Plane 3D model
            Model plane_model = Model.FromFile("Airplane-cleaned.rotated.glb");
            if (plane_model == null)
                Log.Err("Could not load plane model");

            Matrix MAP_SCALE_PLANE_SIZE = Matrix.S(PLANE_SIZE_METERS);

            // XXX need to figure out why the marker needs to be much smaller compared to the plane model, doesn't make sense
            Mesh plane_ground_marker = Mesh.GenerateCylinder(0.001f, 0.002f, Vec3.UnitY, 8);
            Material plane_marker_material = Default.Material.Copy();
            plane_marker_material[MatParamName.ColorTint] = new Color(0f, 0f, 1f);

            observer = new ObserverData();
            observer.update_map_position(current_map);
            Mesh observer_cylinder_marker = Mesh.GenerateCylinder(0.002f, 0.02f, Vec3.UnitY, 8);
            Mesh observer_sphere_marker = Mesh.GenerateSphere(0.006f, 8);
            Material observer_marker_material = Default.Material.Copy();
            observer_marker_material[MatParamName.ColorTint] = new Color(1f, 0.5f, 0f);

            // Floor (for non-seethrough devices)

            Matrix floorTransform = Matrix.TS(0, -1.5f, 0, new Vec3(30, 0.1f, 30));
            Material floorMaterial = new Material(Shader.FromFile("floor.hlsl"));
            floorMaterial.Transparency = Transparency.Blend;

            // Start some background threads
            // XXX can probably take more advantage of async/await, but let's use threads for now

            // Queue for receiving updates from threads
            updates_queue = new ConcurrentQueue<Tuple<string, object>>();

            // Launch data update thread            
            // Queue for sending updated query range to thread
            ConcurrentQueue<Vec4> query_update_queue = new ConcurrentQueue<Vec4>();
            query_update_queue.Enqueue(data_query_extent);
            var plane_update_thread = new Thread(FetchPlaneUpdates);
            plane_update_thread.IsBackground = true;
            plane_update_thread.Start(query_update_queue);
            Log.Info("Plane update thread started");

            // Launch config fetch thread
            // Queue for sending config URL to thread
            ConcurrentQueue<string> config_update_queue = new ConcurrentQueue<string>();
            var config_fetch_thread = new Thread(FetchConfiguration);
            config_fetch_thread.IsBackground = true;
            config_fetch_thread.Start(config_update_queue);
            Log.Info("Config fetch thread started");

            // Prepare for QR scanning

            // Ask for permission to use the QR code tracking system
            var status = QRCodeWatcher.RequestAccessAsync().Result;
            if (status == QRCodeWatcherAccessStatus.Allowed)
            {
                // Create watcher and set it up
                qrcode_watcher = new QRCodeWatcher();
                qrcode_watcher.Added += (o, qr) =>
                {
                    //Log.Info($"(QR code Added handler) Found QR code: {qr.Code.Id} '{qr.Code.Data}'");
                    updates_queue.Enqueue(new Tuple<string, object>("qrcode", qr.Code));
                };
                qrcode_watcher.Updated += (o, qr) =>
                {
                    //Log.Info($"(QR code Updated handler) QR code: {qr.Code.Id} '{qr.Code.Data}'");
                    updates_queue.Enqueue(new Tuple<string, object>("qrcode", qr.Code));
                };
                //watcher.Removed += (o, qr) => poses.Remove(qr.Code.Id);
            }
            else
                Log.Info("Cannot perform QR code scanning, no permission given");

            scanning_for_qrcodes = false;

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

            const float TRACK_LINE_THICKNESS = 0.001f;
            Color MAP_BASE_COLOR = new Color(0.5f, 0f, 1f);
            Color32 MAP_TRACK_LINE_COLOR = new Color32(0, 0, 255, 255);
            Color SKY_BASE_COLOR = new Color(1f, 0f, 0f);
            Color SKY_TRAIL_LINE_COLOR = new Color(0.4f, 1f, 0.4f);
            Color LANDMARK_VLINE_COLOR = new Color(1f, 0f, 1f);

            const float SKY_SCALING_THRESHOLD = 3f;
            Matrix SKY_FAR_MODEL_SCALE = Matrix.S(30f);
            Matrix SKY_CLOSE_MODEL_SCALE = Matrix.S(60f);

            // XXX style uses gamma-space color, leading to slight different with vline color
            TextStyle MAP_TEXT_STYLE = Text.MakeStyle(Default.Font, 0.5f * U.cm, MAP_BASE_COLOR);
            TextStyle SKY_TEXT_STYLE = Text.MakeStyle(Default.Font, 15f * U.m, SKY_BASE_COLOR);
            TextStyle LANDMARK_TEXT_STYLE = Text.MakeStyle(Default.Font, 1f * U.m, LANDMARK_VLINE_COLOR);

            plane_data = new Dictionary<string, PlaneData>();

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

                while (!updates_queue.IsEmpty)
                {
                    // XXX handle error

                    updates_queue.TryDequeue(out update);
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

                        // XXX this seems to cause problems
                        //Pose pose;
                        //World.FromSpatialNode(qrcode.SpatialGraphNodeId, out pose);
                        //Default.SoundClick.Play(pose.position);

                        string data = qrcode.Data;

                        Log.Info($"Got QR code {qrcode.Id} dtime {qrcode.LastDetectedTime}, starttime {qrcode_watcher_start}");
                        Log.Info($"qr data: '{data}'");
                        Log.Info($"Disabled further QR code scanning");

                        if (data.StartsWith("http://") || data.StartsWith("https://"))
                        {
                            Log.Info($"Scheduling config fetch from {data}");
                            config_update_queue.Enqueue(data);
                        }
                        else
                            Log.Warn("Ignoring QR code that doesn't look like a URL");
                    }
                    else if (update_type == "config_data")
                    {
                        JSONNode config_root = update.Item2 as JSONNode;

                        if (config_root.HasKey("observers"))
                        {
                            JSONNode obs = config_root["observers"][1];
                            observer.lat = obs["lat"];
                            observer.lon = obs["lon"];
                            observer.floor_altitude = obs["alt"];
                        }

                        if (config_root.HasKey("landmarks"))
                            observer.update_landmarks(config_root["landmarks"], current_map);

                        observer.update_map_position(current_map);
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
                    
                    string callsign = $"{plane.callsign}";
                    if (plane.updateState == PlaneData.UpdateState.LATE)
                    {
                        // XXX also for sky planes?
                        callsign = $"({callsign})";
                    }

                    if (detail_level == DetailLevel.CALLSIGN)
                    {
                        Text.Add(
                            $"{callsign}",
                           ROT_180_Y * Matrix.T(pos),
                            MAP_TEXT_STYLE,
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
                            $"{callsign}\n{plane.last_heading:F0}°\n{sstring}\n{astring}\n{vstring}",
                            ROT_180_Y * Matrix.T(text_pos),
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
                        $"{plane.callsign}\n{astring}\n{sstring}",
                        Matrix.R(textquat) * Matrix.T(pos),
                        SKY_TEXT_STYLE,
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
                            LANDMARK_TEXT_STYLE,
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
                    ClearTracks();
                UI.SameLine();
                if (UI.Toggle("Scan QR code", ref scanning_for_qrcodes))
                {
                    Log.Info($"qr code button toggled, now {scanning_for_qrcodes}");
                    SetQRCodeScan();
                }
                UI.Label($"{plane_data.Count} planes seen, {num_map_planes} active");

                UI.HSeparator();
                UI.PushId("map");

                UI.Label("Map:");
                foreach (OSMMap map in maps.Values)
                {
                    UI.SameLine();
                    if (UI.Radio(map.name, current_map_name == map.name))
                    {
                        // Switch map
                        SetMap(map.name);
                        // XXX for now
                        ClearTracks();

                        // Signal update to query extent
                        data_query_extent = new Vec4(current_map.min_lat, current_map.max_lat, current_map.min_lon, current_map.max_lon);
                        query_update_queue.Enqueue(data_query_extent);
                    }
                }

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
            map.image_width = 3840;
            map.image_height = 4096;

            // Schiphol Airport
            map = maps["Schiphol Airport"] = new OSMMap(
                "Schiphol Airport",
                51.890054f, 52.696361f, 4.042969f, 5.361328f, 12
            );

            map.texture = Tex.FromFile("Maps\\schiphol-lon-4.042969-5.361328-lat-51.890054-52.696361-c-4.702148-52.293208-z12-3840x3840.png");
            map.image_width = 3840;
            map.image_height = 3840;
        }

        public static void SetMap(string map)
        {
            current_map_name = map;
            current_map = maps[map];
            
            // Compute MR size for map
            float map_geo_height = REALWORLD_MAP_WIDTH * current_map.height / current_map.width;
            Log.Info($"Map geometry size = {REALWORLD_MAP_WIDTH} x {map_geo_height}");
            map_quad = Mesh.GeneratePlane(new Vec2(REALWORLD_MAP_WIDTH, map_geo_height), -Vec3.Forward, Vec3.Up);
            map_scale_km_to_scene = REALWORLD_MAP_WIDTH / current_map.width;

#if true
            map_texture = current_map.texture;
            map_material[MatParamName.DiffuseTex] = map_texture;
#else
            var map_thread = new Thread(OSMTiles.FetchMapTiles);
            map_thread.IsBackground = true;
            // XXX fix map arg
            map_thread.Start(MapConfiguration>(updates, current_map));
            Log.Info("Map tile fetch thread started");
#endif
            // XXX need to recompute extrapolated map positions 
        }

        public static void ClearTracks()
        {
            foreach (var plane in plane_data.Values)
                plane.ClearTrack();
        }

        static async void FetchConfiguration(object obj)
        {
            ConcurrentQueue<string> url_input_queue = obj as ConcurrentQueue<string>;

            // XXX someone wrong with using the blocking collection?
            // YYY probably need a BlockingCollection in the main thread as well, when placing items in it
            //using (BlockingCollection<string> blocking_queue = new BlockingCollection<string>(url_input_queue))
            {
                string url;
                HttpClient http_client = new HttpClient();

                while (true)
                {
                    //Log.Info($"(configuration fetch thread) waiting for url");
                    //url = blocking_queue.Take();

                    if (url_input_queue.TryDequeue(out url))
                    {
                        // Got updated extent
                        Log.Info($"(configuration fetch thread) Got updated config URL: {url}");

                        try
                        {
                            HttpResponseMessage response = await http_client.GetAsync(url);
                            response.EnsureSuccessStatusCode();
                            string body = await response.Content.ReadAsStringAsync();
                            //Log.Info("(data fetch): " + body);

                            JSONNode root_node = JSON.Parse(body);
                            updates_queue.Enqueue(new Tuple<string, object>("config_data", root_node));
                        }
                        catch (HttpRequestException e)
                        {
                            Log.Info("(data fetch): Exception " + e.Message);
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
                    updates_queue.Enqueue(new Tuple<string,object>("plane_data", root_node));
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
            Log.Info($"SetQRCodeScan, scanning_for_qr_codes = {scanning_for_qrcodes}");
            if (qrcode_watcher == null)
            {
                Log.Info("Cannot startg QR code scanning, no permission given!");
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
    }
}
