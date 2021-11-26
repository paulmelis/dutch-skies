using System;

namespace DutchSkies
{
    // XXX need to check this conversion ;-)
    // Based on https://gis.stackexchange.com/a/142871
    public class Projection
    {
        public const float RADIUS_METERS = 6378136.98f;
        public const float RADIUS_KILOMETERS = RADIUS_METERS / 1000f;
        const float RANGE = RADIUS_METERS * MathF.PI * 2.0f;
        const float LON_TO_X = RANGE / 360.0f;
        const float RADIANS_OVER_DEGREES = MathF.PI / 180.0f;

        // WGS84 (EPSG:4326) -> Web Mercator (OSM, EPSG:3857)
        // EPSG:3857 has +X to the right, +Y up, with Y=0 at the equator
        // Output units are *meters*
        public static void epsg4326_to_epsg3857(ref float x, ref float y, float lon, float lat)
        {
            // Note: this is a cylindrical projection, i.e. distances in X will usually be far off except near the equator
            x = lon * LON_TO_X;

            if (lat > 86.0)
                y = RANGE;
            else if (lat < -86.0)
                y = -RANGE;
            else
            {
                y = lat * RADIANS_OVER_DEGREES;
                y = MathF.Log(MathF.Tan(y) + (1.0f / MathF.Cos(y)));
                y *= RADIUS_METERS;
            }
        }
    }
}

