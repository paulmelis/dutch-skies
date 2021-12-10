using System;
using System.Collections.Generic;
using StereoKit;

namespace DutchSkies
{
    public class Landmark
    {
        public string id;
        public float lat, lon;
        public float top_altitude;
        public float bottom_altitude;
        public float height;
        public Vec3 map_position;       // XXX currently unused
        public Vec3 sky_position;

        public Landmark(string id, float lat, float lon, float topalt, float botalt = 0f)
        {
            this.id = id;
            this.lat = lat;
            this.lon = lon;
            top_altitude = topalt;
            bottom_altitude = botalt;
            height = top_altitude - bottom_altitude;
            map_position = new Vec3();
            sky_position = new Vec3();
        }
    };

    class LandmarkObservation
    {
        public Vec3 origin;
        public Vec3 direction;
        public LandmarkObservation(Vec3 pos, Vec3 dir)
        {
            origin = pos;
            direction = dir;
            //Log.Info($"LO: {origin}, {direction}");
        }
    };

    /*
     * Given a set of reference points in the local tangent plane, and a set of observations
     * (head position + view direction) in the SK world coordinate system, compute a rotation in Y,
     * followed by translation in XZ, that maps the observations onto the reference points.
     * This currently uses a pretty naive (but functional) solution strategy through simulated annealing.
     */
    class AlignmentSolver
    {        
        // Reference points in local tangent plane (i.e. sky coordinate system, +Y is up)
        public Dictionary<string, Vec3> reference_points;
        // All observations in SK world coordinate space, i.e. +Y is up
        public Dictionary<string, List<LandmarkObservation>> observations;
        public Random random;

        // Return closest distance *in the XZ plane* between the line from points p to q, and point t
        static float line_point_distance_xz(Vec3 p, Vec3 q, Vec3 t)
        {
            float x1 = p.x;
            //float y1 = p.y;
            float z1 = p.z;

            float x2 = q.x;
            //float y2 = q.y;
            float z2 = q.z;

            float x0 = t.x;
            //float y0 = t.y;
            float z0 = t.z;

            float num = MathF.Abs((x2 - x1) * (z1 - z0) - (x1 - x0) * (z2 - z1));
            float den = MathF.Sqrt(MathF.Pow(x2 - x1, 2f) + MathF.Pow(z2 - z1, 2f));

            return num / den;
        }

        // Positive rotation = CCW
        // XXX can probably use Matrix.R directly here
        static Vec3 rotate_y(Vec3 p, float angle_degrees)
        {
            float a = angle_degrees / 180f * MathF.PI;
            float cos_a = MathF.Cos(a);
            float sin_a = MathF.Sin(a);

            float qx = cos_a * p.x + sin_a * p.z;
            float qz = -sin_a * p.x + cos_a * p.z;

            return new Vec3(qx, p.y, qz);
        }

        static public void Test()
        {
            AlignmentSolver s = new AlignmentSolver();

            s.SetReference("RT", new Vec3(-891.0309f, 148f, 701.9933f));
            s.SetReference("HKF", new Vec3(222.3811f, 33.5f, -189.894100f));
            s.SetReference("HKB", new Vec3(259.083f, 30.5f, -178.2989f));
            s.SetReference("BR", new Vec3(303.6142f, 30.5f, 79.835310f));
            s.SetReference("DHL", new Vec3(-476.4835f, 64f, 254.3338f));
            s.SetReference("SS", new Vec3(-52.751860f, 11f, -22.81f));
            s.SetReference("BOOM", new Vec3(50.029740f, 9f, 115.9509f));

            s.AddObservation("DHL", new Vec3(1.070687f, 0.084614f, 3.150276f), new Vec3(0.922612f, 0.123531f, -0.365413f));
            s.AddObservation("SS", new Vec3(1.187483f, 0.075501f, 3.316733f), new Vec3(0.762326f, 0.122043f, 0.635582f));
            s.AddObservation("BOOM", new Vec3(1.207898f, 0.053789f, 3.258828f), new Vec3(-0.362435f, 0.037529f, -0.931253f));
            s.AddObservation("BOOM", new Vec3(2.160876f, 0.044619f, -5.497616f), new Vec3(-0.394080f, 0.017386f, -0.918912f));
            s.AddObservation("SS", new Vec3(2.198612f, 0.063130f, -5.415352f), new Vec3(0.691618f, 0.079084f, 0.717921f));
            s.AddObservation("HKF", new Vec3(2.145756f, 0.059008f, -5.169587f), new Vec3(-0.792862f, 0.044595f, 0.607767f));
            s.AddObservation("RT", new Vec3(1.912785f, 0.056219f, -5.512104f), new Vec3(0.844261f, 0.071267f, -0.531173f));

            Vec3 t;
            float r;
            s.Solve(out t, out r);

            Log.Info($"solution tx={t.x}, ty={t.y}, tz={t.z}, r={r}");
        }

        public AlignmentSolver()
        {
            reference_points = new Dictionary<string, Vec3>();
            observations = new Dictionary<string, List<LandmarkObservation>>();
            random = new Random();
        }

        // Float in [-0.5, 0.5]
        public float RandUnit()
        {
            return (float)(random.NextDouble() - 0.5);
        }

        public void SetReference(string name, Vec3 position)
        {
            reference_points[name] = position;
        }

        public void AddObservation(string name, Vec3 head_position, Vec3 view_direction)
        {
            if (!observations.ContainsKey(name))
                observations[name] = new List<LandmarkObservation>();

            LandmarkObservation lmo = new LandmarkObservation(head_position, view_direction);
            observations[name].Add(lmo);
        }

        public void RemoveObservations(string name)
        {
            if (!observations.ContainsKey(name))
                return;

            observations[name].Clear();
        }
        public int ObservationCount(string name)
        {
            if (!observations.ContainsKey(name))
                return 0;

            return observations[name].Count;
        }

        public float ComputeEnergy(float tx, float ty, float tz, float r)
        {
            float e2 = 0f;
            int n = 0;
            Vec3 o2, p2;
            float dist;

            foreach (KeyValuePair<string,List<LandmarkObservation>> item in observations)
            {
                string name = item.Key;
                //Log.Info(name);

                foreach (LandmarkObservation ob in item.Value)
                {
                    Vec3 translation = new Vec3(tx, ty, tz);

                    o2 = rotate_y(ob.origin, r) + translation;
                    //Log.Info($"{o2}");

                    p2 = rotate_y(ob.origin + ob.direction, r) + translation;
                    //Log.Info($"{p2}");

                    dist = line_point_distance_xz(o2, p2, reference_points[name]);
                    //Log.Info($"{dist}");

                    e2 += dist * dist;
                    n++;
                }
            }

            if (n == 0)
                return 1e6f;

            return MathF.Sqrt(e2 / n);
        }

        public void Clear()
        {
            reference_points.Clear();
            observations.Clear();
        }

        public void ClearReferences()
        {
            reference_points.Clear();
        }

        public void ClearObservations()
        {
            observations.Clear();
        }
        
        // XXX ty is never changed and will always be 0
        public void Solve(out Vec3 translation, out float rotation)
        {
            float tx = 0f, ty = 0f, tz = 0f, r = 0f, energy;
            float best_tx, best_ty, best_tz, best_r;
            float cand_tx, cand_ty, cand_tz, cand_r;
            float best_energy;

            best_tx = tx;
            best_ty = ty;
            best_tz = tz;
            best_r = r;
            best_energy = ComputeEnergy(tx, ty, tz, r);
            Log.Info($"Energy {best_energy:F6} (initial) | tx={tx:F6}, ty={ty:F6}, tz={tz:F6}, r={r:F6}");

            const int K = 20000;
            const int WO = 200;
            int without_improvement = 0;
            float T, p;
            int idx;

            for (int k = 0; k < K; k++)
            {
                T = 1f - (k + 1) / K;

                // Pick neighbour
                cand_tx = tx;
                cand_ty = ty;
                cand_tz = tz;
                cand_r = r;

                idx = random.Next() % 3;
                if (idx == 0)
                    cand_tx = tx + RandUnit() * 200f * T;
                // Note: ty is never altered
                else if (idx == 1)
                    cand_tz = tz + RandUnit() * 200f * T;
                else
                    cand_r = (r + RandUnit() * 360f * T) % 360;

                energy = ComputeEnergy(cand_tx, cand_ty, cand_tz, cand_r);

                if (energy < best_energy)
                {
                    // Better solution, always accept
                    Log.Info($"[{k}] Energy {energy:F6} (new best) | tx={cand_tx:F6}, ty={cand_ty:F6}, tz={cand_tz:F6}, r={cand_r:F6}");
                    best_energy = energy;
                    best_tx = cand_tx;
                    best_ty = cand_ty;
                    best_tz = cand_tz;
                    best_r = cand_r;
                    without_improvement = 0;
                }
                else if (T > 0f)
                {
                    without_improvement += 1;
                    if (without_improvement == WO)
                    {
                        // Stuck in local minimum, restart with best solution found so far
                        Log.Info($"[{k}] Restarting with best solution so far");
                        tx = best_tx;
                        ty = best_ty;
                        tz = best_tz;
                        r = best_r;
                        without_improvement = 0;
                        continue;
                    }

                    p = MathF.Exp(-(energy - best_energy) / T);
                    if (random.Next() <= p)
                    {
                        // Worse solution, but accept anyway
                        tx = cand_tx;
                        ty = cand_ty;
                        tz = cand_tz;
                        r = cand_r;
                    }
                }
            }

            Log.Info($"Best: tx={tx:F6}, ty={ty:F6}, tz={tz:F6}, r={r:F6} (energy {best_energy:F6})");

            translation = new Vec3(best_tx, best_ty, best_tz);
            rotation = best_r;
        }
    };

}
