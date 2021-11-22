using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using SimpleJSON;
using StereoKit;

namespace DutchSkies
{
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
            plane_model.RootNode.LocalTransform = plane_model.RootNode.LocalTransform * Matrix.S(plane_size_m); // * Matrix.R(90f, 0f, 0f);

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
            DetailLevel detail_level = DetailLevel.CALLSIGN;
            bool show_vlines = true;

            Dictionary<string, PlaneData> plane_data = new Dictionary<string, PlaneData>();

            TextStyle text_style = Text.MakeStyle(Default.Font, 0.5f * U.cm, new Color(1f, 0f, 0f));
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
                    UI.SameLine();
                UI.Toggle("Plane lines", ref show_vlines);
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
                            plane_data[id] = new PlaneData(id, osm_map);

                        plane_data[id].ProcessDataUpdate(update_time, plane);

#if false
                        // Compute plane position in Earth-centric model
                        Matrix M = Matrix.R(-(90f-lat), 0f, 0f) * Matrix.R(0f, lon, 0f);
                        Vec3 p = new Vec3(0f, Projection.RADIUS_KILOMETERS + 0.001f * height, 0f);
                        Log.Info($"e = {M.Transform(p)}");

                        // Compute map-local position (in kilometers)
                        p = osm_map.EarthToMapCentric.Transform(M.Transform(p));
                        Log.Info($"plane {i}; lat = {lat}, lon = {lon}, height = {height}; p = {p} (map-centric, km)");
#endif
                    }
                }

                //
                // Draw map and planes
                //

                Hierarchy.Push(Matrix.R(-90f, 0f, 0f) * Matrix.T(Vec3.Forward * 1) * Matrix.T(Vec3.Up * -0.7f));

                map_quad.Draw(map_material, Matrix.Identity);

                // Plane models

                float draw_time = Time.Totalf;

                foreach (var plane in plane_data.Values)
                {
                    plane.Update(draw_time);

                    //Lines.AddAxis(new Pose(plane.computed_position * map_scale_km_to_scene, Quat.FromAngles(0f, 0f, -plane.last_heading)));
                    plane_model.Draw(Matrix.R(plane.computed_climb_angle, 0f, -plane.last_heading) * Matrix.T(plane.computed_position * map_scale_km_to_scene));
                }

                // Plane information

                if (detail_level == DetailLevel.CALLSIGN)
                {
                    foreach (var plane in plane_data.Values)
                    {
                        Vec3 pos = plane.computed_position;

                        Text.Add(
                            $"{plane.callsign}",
                            Matrix.R(-90f, 180f, 0f) * Matrix.T(pos * map_scale_km_to_scene),
                            text_style,
                            TextAlign.XLeft | TextAlign.YTop,
                            TextAlign.XLeft | TextAlign.YTop,
                            -0.01f, 0f);
                    }
                }
                else if (detail_level == DetailLevel.FULL)
                {
                    foreach (var plane in plane_data.Values)
                    {
                        float vrate = plane.last_vertical_rate;
                        string vstring = " ";

                        if (vrate > 1f)
                            vstring = $"▲ {vrate:F0} m/s";
                        else if (vrate < -1f)
                            vstring = $"▼ {-vrate:F0} m/s";

                        Vec3 pos = plane.computed_position;

                        TextAlign align = TextAlign.XLeft | TextAlign.YTop;
                        if (pos.z < 7.5f)
                            align = TextAlign.XLeft | TextAlign.YBottom;

                        Text.Add(
                            $"{plane.callsign}\n{plane.last_heading:F0}°\n{plane.last_speed * 3.6f:N0} km/h\n{plane.computed_height:N0} m\n{vstring}",
                            Matrix.R(-90f, 180f, 0f) * Matrix.T(pos * map_scale_km_to_scene),
                            text_style,
                            align,
                            TextAlign.XLeft | TextAlign.YTop,
                            -0.006f, -0.008f);
                    }
                }

                // Plane lines vertically to the ground position

                if (show_vlines)
                {
                    foreach (var plane in plane_data.Values)
                    {
                        var pos = plane.computed_position * map_scale_km_to_scene;
                        Lines.Add(pos, new Vec3(pos.x, pos.y, 0f), new Color(1f, 0f, 0f), 0.001f);
                    }
                }

                Hierarchy.Pop();
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

                Thread.Sleep(10 * 1000);
            }
        }
    }
}
