using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using SimpleJSON;
using StereoKit;

namespace DutchSkies
{
    class Program
    {
        static void Main(string[] args)
        {
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

            // Create assets used by the app
            Pose cubePose = new Pose(0, 0, -0.5f, Quat.Identity);

            Model plane_model = Model.FromFile("Airplane.scaled.glb");
            if (plane_model == null)
                Log.Err("Could not load plane model");

            const float plane_size_m = 0.01f;

            // Map

            OSMMap osm_map = new OSMMap();

            Tex map_texture = Tex.FromFile("Maps\\map-lon-2.812500-7.734375-lat-50.513427-53.748711-c-5.273438-52.131069-z10-3584x3840.png");
            // XXX should the map be square? or aspect based on resolution?
            float map_geo_height = 1f * osm_map.height / osm_map.width;
            float map_scale_km_to_scene = 1f / osm_map.width;
            Log.Info($"Map geometry size = {1} x {map_geo_height}");
            Mesh map_quad = Mesh.GeneratePlane(new Vec2(1f, map_geo_height), -Vec3.Forward, Vec3.Up);
            Material map_material = Default.Material.Copy();
            map_material[MatParamName.DiffuseTex] = map_texture;
            // Disable backface culling on the map for now
            map_material.FaceCull = Cull.None;

            Matrix floorTransform = Matrix.TS(0, -1.5f, 0, new Vec3(30, 0.1f, 30));
            Material floorMaterial = new Material(Shader.FromFile("floor.hlsl"));
            floorMaterial.Transparency = Transparency.Blend;

            TextAlign alignX = TextAlign.XLeft;
            TextAlign alignY = TextAlign.YTop;

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

            Pose windowPose = new Pose(-.4f, 0, 0, Quat.LookDir(1, 0, 1));

            Dictionary<string, Vec3> plane_positions = new Dictionary<string, Vec3>();
            Dictionary<string, float> plane_headings = new Dictionary<string, float>();

            // Core application loop
            while (SK.Step(() =>
            {
                if (SK.System.displayType == Display.Opaque)
                    Default.MeshCube.Draw(floorMaterial, floorTransform);

                Lines.AddAxis(Pose.Identity, 0.1f);

#if false
                UI.WindowBegin("Head", ref windowPose, new Vec2(20, 0) * U.cm, UIWin.Normal);
                Pose head = Input.Head;
                UI.Label(String.Format("POS xyz: {0,9:F6} {1,9:F6} {2,9:F6}\nDirection: {3,9:F6} {4,9:F6} {5,9:F6}", 
                        head.position.x, head.position.y, head.position.z,
                        head.Ray.direction.x, head.Ray.direction.y, head.Ray.direction.z));
                UI.WindowEnd();
#endif

#if false
                if (plane_model != null)
                {
                    UI.Handle("Cube", ref cubePose, plane_model.Bounds);
                    plane_model.Draw(cubePose.ToMatrix());
                }
                else
                    Text.Add("No model!", Matrix.TR(new Vec3(0, .1f, 0), Quat.LookDir(0, 0, 1)), TextAlign.Center, alignX | alignY);
#endif

                // Update plane data, if any

                if (!data_updates.IsEmpty)
                {
                    JSONNode root_node;
                    // XXX check?
                    data_updates.TryDequeue(out root_node);

                    JSONNode states = root_node["states"];
                    Log.Info("Got {0} new states", states.Count);

                    for (int i = 0; i < states.Count; i++)
                    {
                        JSONNode plane = states[i];

                        string id = plane[0];                   // 24-bit ICAO address as string
                        float lon = plane[5];
                        float lat = plane[6];
                        float heading = plane[10];              // 0 - 360
                        float height = plane[13] * 0.001f;      // Kilometers

#if false
                        // Compute plane position in Earth-centric model
                        Matrix M = Matrix.R(-(90f-lat), 0f, 0f) * Matrix.R(0f, lon, 0f);
                        Vec3 p = new Vec3(0f, Projection.RADIUS_KILOMETERS + 0.001f * height, 0f);
                        Log.Info($"e = {M.Transform(p)}");

                        // Compute map-local position (in kilometers)
                        p = osm_map.EarthToMapCentric.Transform(M.Transform(p));
                        Log.Info($"plane {i}; lat = {lat}, lon = {lon}, height = {height}; p = {p} (map-centric, km)");
#endif
                        float x = 0f, y = 0f,  z = 0f;
                        osm_map.Project(ref x, ref y, lon, lat);
                        plane_positions[id] = new Vec3(x, y, height) * map_scale_km_to_scene;
                        Log.Info($"plane {i}; lat = {lat}, lon = {lon}, height = {height}; x = {x}; y = {z}");

                        plane_headings[id] = heading;
                    }
                }

                // Draw map and planes

                Hierarchy.Push(Matrix.R(-90f, 0f, 0f) * Matrix.T(Vec3.Forward * 1) * Matrix.T(Vec3.Up * -0.7f));

                map_quad.Draw(map_material, Matrix.Identity);

                // Plane models
                // XXX should really apply the x rot and scale to orient/scale the model on the loaded model itself
                foreach (KeyValuePair<string,Vec3> item in plane_positions)
                    plane_model.Draw(Matrix.R(90f, 0f, 0f) * Matrix.R(0f, 0f, -plane_headings[item.Key]) * Matrix.S(plane_size_m) * Matrix.T(item.Value));

                // Plane lines
                foreach (var pos in plane_positions.Values)
                    Lines.Add(pos, new Vec3(pos.x, pos.y, 0f), new Color(1f,0f,0f), 0.001f);

                Hierarchy.Pop();
            })) ;
            SK.Shutdown();
        }
        static async void FetchPlaneUpdates(object data)
        {
            const string URL = "https://opensky-network.org/api/states/all?lamin=50.492&lomin=2.856&lamax=53.883&lomax=7.535";

            ConcurrentQueue<JSONNode> update_queue = data as ConcurrentQueue<JSONNode>;

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

                Thread.Sleep(5 * 1000);
            }
        }
    }
}
