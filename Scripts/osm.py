#!/usr/bin/env python
# Based on info and code from https://wiki.openstreetmap.org/wiki/Slippy_map_tilenames
# All OSM tiles are 256x256 pixels.
# Retrieves tiles from http://[abc].tile.openstreetmap.org
# Paul Melis, SURF <paul.melis@surf.nl>
import sys, math
from io import BytesIO
from PIL import Image
import requests

def coordinate_to_tile(lat_deg, lon_deg, zoom):
    """Return tile number that contains given lat/lon coordinate"""
    lat_rad = math.radians(lat_deg)
    n = 2.0 ** zoom
    xtile = int((lon_deg + 180.0) / 360.0 * n)
    ytile = int((1.0 - math.log(math.tan(lat_rad) + (1 / math.cos(lat_rad))) / math.pi) / 2.0 * n)
    return (xtile, ytile)

def tile_nw_corner(xtile, ytile, zoom):
    """
    Convert tile number and zoom to lat/lon coordinate of NW-corner of tile

    Use the function with xtile+1 and/or ytile+1 to get the other corners. 
    With xtile+0.5 & ytile+0.5 it will return the center of the tile.
    """
    n = 2.0 ** zoom
    lon_deg = xtile / n * 360.0 - 180.0
    lat_rad = math.atan(math.sinh(math.pi * (1 - 2 * ytile / n)))
    lat_deg = math.degrees(lat_rad)
    return (lat_deg, lon_deg)


if __name__ == '__main__':
    
    TILE_SIZE = 256
    
    # XXX should use left-right, bottom-top for range names
    # XXX write metadata to separate json file

    # Retrieves tiles based on range that needs to be covered,
    # and pastes them together into a single image.
    # The output image will (in almost all cases) cover an area
    # larger than requested, as tiles are not clipped.
    
    # Netherlands
    name = 'netherlands'
    lat_range = [50.513427, 53.748711]
    lon_range = [2.812500, 7.734375]
    zoom = 10
    
    # Schiphol
    #name = 'schiphol'
    #lat_range = [52.106505, 52.536273]
    #lon_range = [4.130859, 5.361328]
    #zoom = 12
    
    # Determine tiles needed
    lat = 0.5*(lat_range[0]+lat_range[1])
    lon = 0.5*(lon_range[0]+lon_range[1])

    maxj = coordinate_to_tile(lat_range[0], lon, zoom)[1]
    minj = coordinate_to_tile(lat_range[1], lon, zoom)[1]

    mini = coordinate_to_tile(lat, lon_range[0], zoom)[0]
    maxi = coordinate_to_tile(lat, lon_range[1], zoom)[0]

    print('tile range: i = %d-%d, j=%d,%d' % (mini, maxi, minj, maxj))

    nrows = maxj - minj + 1
    ncols = maxi - mini + 1

    # Determine lat/lon range from tile extent
    maxlat, minlon = tile_nw_corner(mini, minj, zoom)
    minlat, maxlon = tile_nw_corner(maxi+1, maxj+1, zoom)
    center_lat = 0.5*(minlat + maxlat)
    center_lon = 0.5*(minlon + maxlon)
    print('map range: ', minlat, minlon, '->', maxlat, maxlon)
    print('center: ', center_lat, center_lon)

    # Compute map size at center (only used to print)
    R = 6378136.98
    r = R * math.cos(math.radians(0.5*(lat_range[0]+lat_range[1])))
    londiff = lon_range[1] - lon_range[0]
    map_width = londiff / 360.0 * 2 * math.pi * r 
    map_height = (lat_range[1] - lat_range[0]) / 360.0 * 2*math.pi*R
    print('Map size at center = %.3f x %.3f km' % (map_width/1000, map_height/1000))

    img = Image.new('RGB', (ncols*TILE_SIZE, nrows*TILE_SIZE))

    print('Retrieving %d x %d tiles' % (ncols, nrows))

    osm_idx = 0
    for j in range(minj, maxj+1):
        for i in range(mini, maxi+1):
            sys.stdout.write('(%d,%d) ' % (i, j))
            sys.stdout.flush()

            url = "http://%s.tile.openstreetmap.org/%d/%d/%d.png" % \
                (chr(ord('a')+osm_idx), zoom, i, j)

            r = requests.get(url)
            if r.status_code != 200:
            	print("\nCould not get %s (error %d)" % (url, r.status_code))
            assert r.status_code == 200
            data = r.content

            tileimg = Image.open(BytesIO(data))
            #tileimg.save('tile-%d-%d.png' % (i, j))
            img.paste(tileimg.convert('RGB'), ((i-mini)*TILE_SIZE, (j-minj)*TILE_SIZE))

            osm_idx = (osm_idx+1) % 3

    sys.stdout.write('\nWriting output image...')
    sys.stdout.flush()

    img.save('%s-lon-%.6f-%.6f-lat-%.6f-%.6f-c-%.6f-%.6f-z%d-%dx%d.png' % \
        (name, minlon, maxlon, minlat, maxlat, center_lon, center_lat, zoom, img.size[0], img.size[1]))

    sys.stdout.write('done\n')
    sys.stdout.flush()
