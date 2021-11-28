using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using SimpleJSON;
using StereoKit;

namespace DutchSkies
{
    public class ObserverData
    {
        public float lat, lon;
        public float floor_altitude;  // meters
        public Vec3 map_position;

        public ObserverData()
        {
            // SURF building at Amsterdam Science Park
            lat = 52.357036140185144f;
            lon = 4.954487434653384f;
            floor_altitude = /* street level */ -3.64f + 4f /* one floor */;
        }

        public void update_map_position(OSMMap map)
        {
            float x = 0f, y = 0f;
            map.Project(ref x, ref y, lon, lat);
            map_position = new Vec3(x, y, floor_altitude/1000f);
            Log.Info($"observer map pos = {x:F6}, {y:F6}");
        }
    };

    class Program
    {
        enum DetailLevel { NONE, CALLSIGN, FULL };
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

            Renderer.SetClip(0.08f, 10000f);
            Renderer.EnableSky = false;

            // Map

            const float REALWORLD_MAP_WIDTH = 1.5f; // meters

            Matrix ROT_MIN90_X = Matrix.R(-90f, 0f, 0f);
            Matrix ROT_180_Y = Matrix.R(0f, 180f, 0f);
            Matrix MAP_PLACEMENT_XFORM = Matrix.T(1f * Vec3.Forward - 0.7f * Vec3.Up);

            OSMMap osm_map = new OSMMap();

            Tex map_texture = null; // Tex.FromFile("Maps\\map-lon-2.812500-7.734375-lat-50.513427-53.748711-c-5.273438-52.131069-z10-3584x3840.png");
            // XXX should the map be square? or aspect based on resolution?
            float map_geo_height = REALWORLD_MAP_WIDTH * osm_map.height / osm_map.width;
            float map_scale_km_to_scene = REALWORLD_MAP_WIDTH / osm_map.width;
            Log.Info($"Map geometry size = {REALWORLD_MAP_WIDTH} x {map_geo_height}");
            Mesh map_quad = Mesh.GeneratePlane(new Vec2(REALWORLD_MAP_WIDTH, map_geo_height), -Vec3.Forward, Vec3.Up);
            Material map_material = Default.Material.Copy();
#if false
            map_material[MatParamName.DiffuseTex] = map_texture;
            // Disable backface culling on the map for now, for debugging
            map_material.FaceCull = Cull.None;
#endif
            ConcurrentQueue<byte[]> map_updates = new ConcurrentQueue<byte[]>();

            var map_thread = new Thread(OSMTiles.FetchMapTiles);
            map_thread.IsBackground = true;
            map_thread.Start(new Tuple<ConcurrentQueue<byte[]>, MapConfiguration>(map_updates, osm_map.current_configuration));
            Log.Info("Data thread started");

            // Create assets used by the app
            Model plane_model = Model.FromFile("Airplane-cleaned.rotated.glb");
            if (plane_model == null)
                Log.Err("Could not load plane model");

            const float plane_size_m = 0.015f;  // Decent size
            Matrix MAP_SCALE_PLANE_SIZE = Matrix.S(plane_size_m);

            // Rotate and scale plane model to put it in the XY plane, with the nose pointing in the -Z direction (check this)
            //plane_model.RootNode.LocalTransform = plane_model.RootNode.LocalTransform * Matrix.S(plane_size_m); // * Matrix.R(90f, 0f, 0f);

            // XXX need to figure out why the marker needs to be much smaller compared to the plane model, doesn't make sense
            Mesh plane_ground_marker = Mesh.GenerateCylinder(0.001f, 0.002f, Vec3.UnitY, 8);
            Material plane_marker_material = Default.Material.Copy();
            plane_marker_material[MatParamName.ColorTint] = new Color(0f, 0f, 1f);

            ObserverData observer = new ObserverData();
            observer.update_map_position(osm_map);
            Mesh observer_marker = Mesh.GenerateCylinder(0.001f, 0.01f, Vec3.UnitY, 8);
            Material observer_marker_material = Default.Material.Copy();
            observer_marker_material[MatParamName.ColorTint] = new Color(0f, 1f, 0f);

            // Floor (for non-seethrough devices)

            Matrix floorTransform = Matrix.TS(0, -1.5f, 0, new Vec3(30, 0.1f, 30));
            Material floorMaterial = new Material(Shader.FromFile("floor.hlsl"));
            floorMaterial.Transparency = Transparency.Blend;

            // Data update thread

            ConcurrentQueue<JSONNode> data_updates = new ConcurrentQueue<JSONNode>();
            var data_thread = new Thread(FetchPlaneUpdates);
            data_thread.IsBackground = true;
            data_thread.Start(data_updates);
            Log.Info("Data thread started");

            // Initial head pose in physical space is apparently taken as origin, with
            // head view direction as forward (-Z), Y is up, X to the right, i.e. right-handed
            //
            // Line.AddAxis shows an axis with these directions:
            // Red = Vec3.Right = +X
            // Green = Vec3.Up = +Y
            // Blue = Vec3.Forward = -Z (NOTE!)

            Pose windowPose = new Pose(0.5f, -0.2f, -0.5f, Quat.LookDir(-1, 0, 1));
            DetailLevel detail_level = DetailLevel.FULL;
            bool map_visible = true;
            bool map_show_planes = true, sky_show_planes = true;
            bool show_map_vlines = true, show_sky_vlines = false;
            bool show_flight_units = false;
            bool show_track_lines = true;
            int num_map_planes = 0;

            const float track_line_thickness = 0.001f;
            Color32 track_line_color = new Color32(0, 0, 255, 255);
            Color VLINE_COLOR = new Color(1f, 0f, 0f);
            Color SKY_TRACK_LINE_COLOR = new Color(0.4f, 1f, 0.4f);

            Dictionary<string, PlaneData> plane_data = new Dictionary<string, PlaneData>();

            TextStyle text_style_map = Text.MakeStyle(Default.Font, 0.5f * U.cm, new Color(1f, 0f, 0f));
            TextStyle text_style_sky = Text.MakeStyle(Default.Font, 15f * U.m, new Color(1f, 0f, 0f));
            JSONNode root_node;

            byte[] map_image;

            int fps_num_frames = 0;
            float fps_start_time = Time.Totalf;
            float fps = 0f;

            // Core application loop
            while (SK.Step(() =>
            {
                if (SK.System.displayType == Display.Opaque)
                    Default.MeshCube.Draw(floorMaterial, floorTransform);

                // World origin (for debugging)
                Lines.AddAxis(Pose.Identity, 0.1f);

                //
                // Process received plane data, if any
                //

                while (!data_updates.IsEmpty)
                {
                    if (map_updates.TryDequeue(out map_image))
                    {
                        Log.Info("Got updated map image!");
                        map_material[MatParamName.DiffuseTex] = Tex.FromMemory(map_image);
                        // Disable backface culling on the map for now, for debugging
                        map_material.FaceCull = Cull.None;
                    }

                    data_updates.TryDequeue(out root_node);

                    JSONNode states = root_node["states"];
                    Log.Info("Got {0} new states", states.Count);

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

                            int fl = (int)MathF.Round(plane.computed_altitude / 30.48f);
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
                            astring = $"{plane.computed_altitude:N0} m";

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

                    if (show_map_vlines)
                        Lines.Add(pos, new Vec3(pos.x, 0f, pos.z), VLINE_COLOR, 0.001f);

                    // Historical track
                    if (show_track_lines && plane.map_positions.Count >= 2)
                    {
                        LinePoint[] lp = new LinePoint[plane.map_positions.Count];
                        int idx = 0;
                        foreach (Vec3 p in plane.map_positions)
                            lp[idx++] = new LinePoint(ROT_MIN90_X * p * map_scale_km_to_scene, track_line_color, track_line_thickness);
                        Lines.Add(lp);
                    }
                }


                // Observer location

                Vec3 observer_pos = ROT_MIN90_X.Transform(observer.map_position) * map_scale_km_to_scene;
                observer_marker.Draw(observer_marker_material, Matrix.T(0f,0.005f,0f) * Matrix.T(observer_pos));

                Hierarchy.Pop();

                //
                // Draw planes in sky
                // Assume Forward (-Z) is pointing North
                //

                foreach (var plane in plane_data.Values)
                {
                    var pos = ROT_MIN90_X.Transform(plane.computed_sky_position);
                    var prev_pos = ROT_MIN90_X.Transform(plane.previous_sky_position);

                    if (plane.on_ground)
                        continue;

                    // Don't bother with planes below the horizon
                    if (pos.y < 0f)
                        continue;

                    if (sky_show_planes)
                    {
                        // Plane
                        if (plane.observer_distance > 3f)
                        {
                            // To avoid large clipping distances move plane along the line from plane to viewer,
                            // with a smaller plane model to avoid a weird scaling visual issues

                            // Vector from head to plane 
                            //Vec3 v = pos - Input.Head.position;
                            //v.Normalize();

                            //pos = Input.Head.position + 3000f * v;
                            pos *= 3f / plane.observer_distance;
                            prev_pos *= 3f / plane.observer_distance;
                            //Log.Info($"[{plane.callsign}] position scaled to {pos}");

                            plane_model.Draw(Matrix.S(30f) * ROT_MIN90_X * Matrix.R(0f, -plane.last_heading, 0f) * Matrix.T(pos));
                        }
                        else
                        {
                            // Plane with length 100 meter, larger than an A380 ;-)
                            plane_model.Draw(Matrix.S(100f) * ROT_MIN90_X * Matrix.R(0f, -plane.last_heading, 0f) * Matrix.T(pos));
                            //Lines.Add(pos, new Vec3(pos.x, pos.y, 0f), new Color(1f, 0f, 0f), 0.001f);
                        }
                    }


                    if (show_sky_vlines)
                    {
                        // Vertical line, start slightly below plane to make room for text
                        Lines.Add(new Vec3(pos.x, pos.y - 120f, pos.z), new Vec3(pos.x, 0f, pos.z), VLINE_COLOR, 3f);
                    }

                    // Track line
                    Lines.Add(prev_pos, pos, SKY_TRACK_LINE_COLOR, 3f);

                    // Text labels
                    Quat textquat = Quat.LookAt(pos, head_pos, Vec3.UnitY);

                    string astring = "";
                    string sstring = "";

                    if (show_flight_units)
                    {
                        sstring = $"{plane.last_velocity * 1.94384449f:N0} kn";
                        int fl = (int)MathF.Round(plane.computed_altitude / 30.48f);
                        astring = $"FL {fl:D3}";
                    }
                    else
                    {
                        sstring = $"{plane.last_velocity * 3.6f:N0} km / h";
                        astring = $"{plane.computed_altitude:N0} m";
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

                fps_num_frames++;
                float now = Time.Totalf;
                if (now - fps_start_time > 0.5f)
                {
                    fps = fps_num_frames / (now - fps_start_time);
                    fps_num_frames = 0;
                    fps_start_time = now;
                }

                // UI (drawn late, so we can show accurate statistics)

                UI.WindowBegin("Controls", ref windowPose, new Vec2(35, 0) * U.cm, UIWin.Normal);

                UI.Toggle("Flight units", ref show_flight_units);

                UI.PushId("map");
                UI.Label("Map:");
                UI.Toggle("Visible", ref map_visible);
                UI.SameLine();
                UI.Toggle("Planes", ref map_show_planes);
                UI.SameLine();
                UI.Toggle("VLines", ref show_map_vlines);
                UI.Label("Plane details");
                UI.SameLine();
                if (UI.Radio("None", detail_level == DetailLevel.NONE)) detail_level = DetailLevel.NONE;
                UI.SameLine();
                if (UI.Radio("Callsign", detail_level == DetailLevel.CALLSIGN)) detail_level = DetailLevel.CALLSIGN;
                UI.SameLine();
                if (UI.Radio("Full", detail_level == DetailLevel.FULL)) detail_level = DetailLevel.FULL;
                UI.Toggle("Track lines", ref show_track_lines);
                UI.PopId();

                UI.PushId("sky");
                UI.Label("Sky:");
                UI.Toggle("Planes", ref sky_show_planes);
                UI.SameLine();
                UI.Toggle("VLines", ref show_sky_vlines);
                UI.PopId();

                UI.Label($"{plane_data.Count} planes seen, {num_map_planes} active");
                UI.SameLine();
                UI.Text($"{fps:F1} FPS", TextAlign.BottomRight);
                UI.WindowEnd();

            }));

            SK.Shutdown();
        }
        static async void FetchPlaneUpdates(object update_queue_obj)
        {
            const string URL = "https://opensky-network.org/api/states/all?lamin=50.513427&lomin=2.812500&lamax=53.748711&lomax=7.734375";

            ConcurrentQueue<JSONNode> update_queue = update_queue_obj as ConcurrentQueue<JSONNode>;

            while (true)
            {
                try
                {
                    HttpClient client = new HttpClient();
                    HttpResponseMessage response = await client.GetAsync(URL);
                    response.EnsureSuccessStatusCode();
                    string body = await response.Content.ReadAsStringAsync();
                    Log.Info("(data fetch): " + body);

                    JSONNode root_node = JSON.Parse(body);
                    update_queue.Enqueue(root_node);
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
