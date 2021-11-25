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

        public ObserverData()
        {
            // SURF building at Amsterdam Science Park
            lat = 52.357036140185144f;
            lon = 4.954487434653384f;
            floor_altitude = /* street level */ -3.64f + 3f /* one floor */;
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

            OSMMap osm_map = new OSMMap();

            Tex map_texture = Tex.FromFile("Maps\\map-lon-2.812500-7.734375-lat-50.513427-53.748711-c-5.273438-52.131069-z10-3584x3840.png");
            // XXX should the map be square? or aspect based on resolution?
            float map_geo_height = REALWORLD_MAP_WIDTH * osm_map.height / osm_map.width;
            float map_scale_km_to_scene = REALWORLD_MAP_WIDTH / osm_map.width;
            Log.Info($"Map geometry size = {REALWORLD_MAP_WIDTH} x {map_geo_height}");
            Mesh map_quad = Mesh.GeneratePlane(new Vec2(REALWORLD_MAP_WIDTH, map_geo_height), -Vec3.Forward, Vec3.Up);
            Material map_material = Default.Material.Copy();
            map_material[MatParamName.DiffuseTex] = map_texture;
            // Disable backface culling on the map for now, for debugging
            map_material.FaceCull = Cull.None;

            // Create assets used by the app
            Model plane_model = Model.FromFile("Airplane-cleaned.rotated.glb");
            if (plane_model == null)
                Log.Err("Could not load plane model");

            const float plane_size_m = 0.015f;  // Decent size

            // Rotate and scale plane model to put it in the XY plane, with the nose pointing in the -Z direction (check this)
            //plane_model.RootNode.LocalTransform = plane_model.RootNode.LocalTransform * Matrix.S(plane_size_m); // * Matrix.R(90f, 0f, 0f);

            // XXX need to figure out why the marker needs to be much smaller compared to the plane model, doesn't make sense
            Mesh plane_ground_marker = Mesh.GenerateCylinder(0.001f, 0.002f, Vec3.UnitY, 8);
            Material plane_marker_material = Default.Material.Copy();
            plane_marker_material[MatParamName.ColorTint] = new Color(0f, 0f, 1f);

            ObserverData observer = new ObserverData();

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
            bool        show_vlines = true;
            bool        show_flight_units = false;

            Dictionary<string, PlaneData> plane_data = new Dictionary<string, PlaneData>();

            TextStyle text_style_map = Text.MakeStyle(Default.Font, 0.5f * U.cm, new Color(1f, 0f, 0f));
            JSONNode root_node;
           
            // Core application loop
            while (SK.Step(() =>
            {
                if (SK.System.displayType == Display.Opaque)
                    Default.MeshCube.Draw(floorMaterial, floorTransform);

                // World origin (for debugging)
                Lines.AddAxis(Pose.Identity, 0.1f);

                // UI

                UI.WindowBegin("Controls", ref windowPose, new Vec2(35, 0) * U.cm, UIWin.Normal);
                UI.Label("Plane details");
                UI.SameLine();
                if (UI.Radio("None", detail_level == DetailLevel.NONE)) detail_level = DetailLevel.NONE;
                UI.SameLine();
                if (UI.Radio("Callsign", detail_level == DetailLevel.CALLSIGN)) detail_level = DetailLevel.CALLSIGN;
                UI.SameLine();
                if (UI.Radio("Full", detail_level == DetailLevel.FULL)) detail_level = DetailLevel.FULL;

                UI.Toggle("Vertical lines", ref show_vlines);
                UI.SameLine();
                UI.Toggle("Flight units", ref show_flight_units);
                UI.WindowEnd();

                // Process received plane data, if any

                while (!data_updates.IsEmpty)
                {
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

                Hierarchy.Push(Matrix.R(-90f, 0f, 0f) * Matrix.T(Vec3.Forward * 1) * Matrix.T(Vec3.Up * -0.7f));

                // Map

                map_quad.Draw(map_material, Matrix.Identity);

                // Planes

                foreach (var plane in plane_data.Values)
                {
                    plane.Update(draw_time);

                    var pos = plane.computed_map_position * map_scale_km_to_scene;

                    // Plane model
                    if (!plane.on_ground)
                    {
                        //Lines.AddAxis(new Pose(plane.computed_position * map_scale_km_to_scene, Quat.FromAngles(0f, 0f, -plane.last_heading)));
                        plane_model.Draw(Matrix.S(plane_size_m) * Matrix.R(-plane.computed_climb_angle * 2f, 0f, 0f)
                            * Matrix.R(0f, 0f, -plane.last_heading) * Matrix.T(pos));
                    }
                    else
                    {
                        // XXX could set z to 0, as there seem to be cases where a plane is marked on-ground, but has an incorrect altitude value
                        plane_ground_marker.Draw(plane_marker_material, Matrix.R(0f, 0f, -plane.last_heading) * Matrix.T(pos));
                    }

                    // Plane information

                    if (detail_level == DetailLevel.CALLSIGN)
                    {
                        Text.Add(
                            $"{plane.callsign}",
                            Matrix.R(-90f, 180f, 0f) * Matrix.T(pos),
                            text_style_map,
                            TextAlign.XLeft | TextAlign.YTop,
                            TextAlign.XLeft | TextAlign.YTop,
                            -0.01f, 0f);
                    }
                    else if (detail_level == DetailLevel.FULL)
                    {
                        if (plane.on_ground)
                            continue;

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
                        if (pos.z < 0.05f)
                        {
                            pos_align = TextAlign.XLeft | TextAlign.YBottom;
                            text_pos.z = 0.01f;
                        }

                        Text.Add(
                            $"{plane.callsign}\n{plane.last_heading:F0}°\n{sstring}\n{astring}\n{vstring}",
                            Matrix.R(-90f, 180f, 0f) * Matrix.T(text_pos),
                            text_style_map,
                            pos_align,
                            TextAlign.XLeft | TextAlign.YTop,
                            -0.006f, 0f);
                    }

                    // Plane lines vertically to the ground position

                    if (show_vlines)
                        Lines.Add(pos, new Vec3(pos.x, pos.y, 0f), new Color(1f, 0f, 0f), 0.001f);
                }

                Hierarchy.Pop();

                //
                // Draw planes in sky
                // Assume Forward (-Z) is pointing North
                //

                TextStyle text_style_sky = Text.MakeStyle(Default.Font, 20f * U.m, new Color(1f, 0f, 0f));

                Vec3 head_pos = Input.Head.position;

                foreach (var plane in plane_data.Values)
                {
                    var pos = Matrix.R(-90f, 0f, 0f).Transform(plane.computed_sky_position);
                    var prev_pos = Matrix.R(-90f, 0f, 0f).Transform(plane.previous_sky_position);

                    if (plane.on_ground)
                        continue;

                    // Don't bother with planes below the horizon
                    if (pos.y < 0f)
                        continue;

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
                        Log.Info($"[{plane.callsign}] position scaled to {pos}");

                        plane_model.Draw(Matrix.S(30f) * Matrix.R(-90f,0f,0f) * Matrix.R(0f, -plane.last_heading, 0f) * Matrix.T(pos));
                    }
                    else
                    {
                        // Plane with length 100 meter, larger than an A380 ;-)
                        plane_model.Draw(Matrix.S(100f) * Matrix.R(-90f, 0f, 0f) * Matrix.R(0f, -plane.last_heading, 0f) * Matrix.T(pos));
                        //Lines.Add(pos, new Vec3(pos.x, pos.y, 0f), new Color(1f, 0f, 0f), 0.001f);
                    }

                    // Starts slightly below plane to make room for text
                    Lines.Add(new Vec3(pos.x, pos.y-75f, pos.z), new Vec3(pos.x, 0f, pos.z), new Color(1f, 0f, 0f), 3f);

                    if (plane.observer_distance > 3f)
                    {
                        // To avoid large clipping distances move plane along the line from plane to viewer,
                        // scaling the plane to avoid a weird visual size
                        prev_pos *= 3f / plane.observer_distance;
                    }

                    Lines.Add(prev_pos, pos, new Color(0.4f, 1f, 0.4f), 3f);

                    // Text labels
                    Quat textquat = Quat.LookAt(pos, head_pos - pos, Vec3.UnitX);

                    Text.Add(
                        $"({plane.observer_distance:F0} km)",
                         Matrix.T(pos),
                        text_style_sky,
                        TextAlign.XCenter | TextAlign.YTop,
                        TextAlign.XLeft | TextAlign.YTop,
                        0f, 40f);

                    Text.Add(
                        $"{plane.callsign}\n{plane.computed_altitude:N0} m",
                        Matrix.T(pos),
                        text_style_sky,
                        TextAlign.XCenter | TextAlign.YTop,
                        TextAlign.XLeft | TextAlign.YTop,
                        0f, -30f);
                }

            }));

            SK.Shutdown();
        }
        static async void FetchPlaneUpdates(object update_queue_obj)
        {
            const string URL = "https://opensky-network.org/api/states/all?lamin=50.492&lomin=2.856&lamax=53.883&lomax=7.535";

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
