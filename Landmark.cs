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
        public Vec2 origin;
        public Vec2 direction;
        public LandmarkObservation(Vec3 pos, Vec3 dir)
        {
            origin = new Vec2(pos.x, -pos.z);
            direction = new Vec2(dir.x, -dir.z);
            //Log.Info($"LO: {origin}, {direction}");
        }
    };

    class AlignmentSolver
    {
        public Dictionary<string, Vec2> references;
        public Dictionary<string, List<LandmarkObservation>> observations;
        public Random random;

        // Return closest distance between 2D point t and the line between points p and q
        static float line_point_distance(Vec2 p, Vec2 q, Vec2 t)
        {
            float x1 = p.x;
            float y1 = p.y;

            float x2 = q.x;
            float y2 = q.y;

            float x0 = t.x;
            float y0 = t.y;

            float num = MathF.Abs((x2 - x1) * (y1 - y0) - (x1 - x0) * (y2 - y1));
            float den = MathF.Sqrt(MathF.Pow(x2 - x1, 2f) + MathF.Pow(y2 - y1, 2f));

            return num / den;
        }

        // Positive rotation = CCW
        static Vec2 rotate(Vec2 p, float angle_degrees)
        {
            float a = angle_degrees / 180f * MathF.PI;
            float cos_a = MathF.Cos(a);
            float sin_a = MathF.Sin(a);

            float qx = cos_a * p.x - sin_a * p.y;
            float qy = sin_a * p.x + cos_a * p.y;

            return new Vec2(qx, qy);
        }

        static public void Test()
        {
            AlignmentSolver s = new AlignmentSolver();

            s.SetReference("A", new Vec3(3f, 0f, -6f));
            s.SetReference("B", new Vec3(-7f, 0f, 11f));
            s.SetReference("C", new Vec3(2f, 0f, 3f));
            s.SetReference("D", new Vec3(-6f, 0f, -8f));

            s.AddObservation("A", new Vec3(0f, 0f, 0f), new Vec3(1.7f, 0f, -10.8f));
            s.AddObservation("A", new Vec3(-2.5f, 0f, -1.5f), new Vec3(3.9f, 0f, -9.9f));
            s.AddObservation("B", new Vec3(0f, 0f, 0f), new Vec3(-0.6f, 0f, 8.7f));
            s.AddObservation("C", new Vec3(0f, 0f, 0f), new Vec3(4.4f, 0f, -2.3f));
            s.AddObservation("D", new Vec3(0f, 0f, 0f), new Vec3(-7.5f, 0f, -9f));

            float tx = -2.9f;
            float ty = -3.3f;
            float r = -25.06f;

            Log.Info($"{s.ComputeEnergy(tx, ty, r)}");
            Log.Info($"{s.ComputeEnergy(tx, ty, r + 90f)}");
            Log.Info($"{s.ComputeEnergy(tx, ty, r + 180f)}");
            Log.Info($"{s.ComputeEnergy(tx, ty, r + 270f)}");
            Log.Info($"{s.ComputeEnergy(-2.972659f, -3.278703f, 155.837377f - 180f)}");
            Log.Info($"{s.ComputeEnergy(-2.972659f, -3.278703f, 155.837377f)}");

            Vec3 t;

            s.Solve(out t, out r);

            Log.Info($"solution tx={t.x}, ty={-t.z}, r={r}");
        }

        public AlignmentSolver()
        {
            references = new Dictionary<string, Vec2>();
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
            references[name] = new Vec2(position.x, -position.z);
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

        public float ComputeEnergy(float tx, float ty, float r)
        {
            float e2 = 0f;
            int n = 0;
            Vec2 o2, p2;
            float dist;

            foreach (KeyValuePair<string,List<LandmarkObservation>> item in observations)
            {
                string name = item.Key;
                //Log.Info(name);

                foreach (LandmarkObservation ob in item.Value)
                {
                    o2 = rotate(ob.origin, r);
                    o2 = new Vec2(o2.x + tx, o2.y + ty);
                    //Log.Info($"{o2}");

                    p2 = rotate(ob.origin + ob.direction, r);
                    p2 = new Vec2(p2.x + tx, p2.y + ty);
                    //Log.Info($"{p2}");

                    dist = line_point_distance(o2, p2, references[name]);
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
            references.Clear();
            observations.Clear();
        }

        public void ClearReferences()
        {
            references.Clear();
        }

        public void ClearObservations()
        {
            observations.Clear();
        }
        
        // Outputs as (tx, 0, -ty), to be consistent with the way input values are set
        public void Solve(out Vec3 translation, out float rotation)
        {
            float tx = 0f, ty = 0f, r = 0f, energy;
            float best_tx, best_ty, best_r;
            float cand_tx, cand_ty, cand_r;
            float best_energy;

            best_tx = tx;
            best_ty = ty;
            best_r = r;
            best_energy = ComputeEnergy(tx, ty, r);
            Log.Info($"Energy {best_energy:F6} (initial) | tx={tx:F6}, ty={ty:F6}, r={r:F6}");

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
                cand_r = r;

                idx = random.Next() % 3;
                if (idx == 0)
                    cand_tx = tx + RandUnit() * 200f * T;
                else if (idx == 1)
                    cand_ty = ty + RandUnit() * 200f * T;
                else
                    cand_r = (r + RandUnit() * 360f * T) % 360;

                energy = ComputeEnergy(cand_tx, cand_ty, cand_r);

                if (energy < best_energy)
                {
                    Log.Info($"[{k}] Energy {energy:F6} (new best) | tx={tx:F6}, ty={ty:F6}, r={r:F6}");
                    best_energy = energy;
                    best_tx = cand_tx;
                    best_ty = cand_ty;
                    best_r = cand_r;
                    without_improvement = 0;
                }
                else if (T > 0f)
                {
                    without_improvement += 1;
                    if (without_improvement == WO)
                    {
                        // Restart with last best solution
                        Log.Info($"[{k}] Restarting with best solution so far");
                        tx = best_tx;
                        ty = best_ty;
                        r = best_r;
                        without_improvement = 0;
                        continue;
                    }

                    p = MathF.Exp(-(energy - best_energy) / T);
                    if (random.Next() <= p)
                    {
                        tx = cand_tx;
                        ty = cand_ty;
                        r = cand_r;
                    }
                }
            }

            Log.Info($"Best: tx={tx:F6}, ty={ty:F6}, r={r:F6} (energy {best_energy:F6})");

            translation = new Vec3(best_tx, 0f, -best_ty);
            rotation = best_r;
        }
    };

}
