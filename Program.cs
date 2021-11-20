using System;
using System.Collections.Concurrent;
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

            Tex map_texture = Tex.FromFile("Maps\\map-lon-2.812500-7.734375-lat-50.513427-53.748711-c-5.273438-52.131069-z10-3584x3840.png");
            Mesh map_quad = Mesh.GeneratePlane(new Vec2(1f, 1f), Vec3.Up, Vec3.Forward);
            Material map_material = Default.Material.Copy();
            map_material[MatParamName.DiffuseTex] = map_texture;
            // XXX disable backface culling
            map_material.FaceCull = Cull.None;

            Model cube = Model.FromMesh(
                Mesh.GenerateRoundedCube(Vec3.One * 0.1f, 0.02f),
                Default.MaterialUI);

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

            // Core application loop
            while (SK.Step(() =>
            {
                Lines.AddAxis(Pose.Identity, 0.1f);

                UI.WindowBegin("Head", ref windowPose, new Vec2(20, 0) * U.cm, UIWin.Normal);
                Pose head = Input.Head;
                UI.Label(String.Format("POS xyz: {0,9:F6} {1,9:F6} {2,9:F6}\nDirection: {3,9:F6} {4,9:F6} {5,9:F6}", 
                        head.position.x, head.position.y, head.position.z,
                        head.Ray.direction.x, head.Ray.direction.y, head.Ray.direction.z));
                UI.WindowEnd();

                if (SK.System.displayType == Display.Opaque)
                    Default.MeshCube.Draw(floorMaterial, floorTransform);

                /*if (plane_model != null)
                {
                    UI.Handle("Cube", ref cubePose, plane_model.Bounds);
                    plane_model.Draw(cubePose.ToMatrix());
                }
                else
                    Text.Add("No model!", Matrix.TR(new Vec3(0, .1f, 0), Quat.LookDir(0, 0, 1)), TextAlign.Center, alignX | alignY);*/

                if (!data_updates.IsEmpty)
                {
                    JSONNode root_node;
                    // XXX check?
                    data_updates.TryDequeue(out root_node);

                    Log.Info("Got {0} new states", root_node["states"].Count);
                }

                //map_quad.Draw(map_material, Matrix.Identity);
                map_quad.Draw(map_material, Matrix.T(Vec3.Forward * 1) * Matrix.T(Vec3.Up * -0.7f));

                //UI.Handle("Cube", ref cubePose, cube.Bounds);
                //cube.Draw(cubePose.ToMatrix());

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

                Thread.Sleep(10 * 1000);
            }
        }
    }
}
